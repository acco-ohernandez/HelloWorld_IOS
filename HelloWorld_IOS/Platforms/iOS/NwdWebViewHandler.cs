using CoreGraphics;
using Foundation;
using HelloWorld_IOS.Controls;
using HelloWorld_IOS.Services;
using Microsoft.Maui.Handlers;
using WebKit;

namespace HelloWorld_IOS.Platforms.iOS;

// Owns the WKWebView. Creates the WKWebViewConfiguration with:
//   - app://     scheme handler -> serves Resources/Raw/wwwroot/** from the bundle
//   - files://   scheme handler -> serves picked files from FileSystem.CacheDirectory/tabs/{tabId}/
//   - nwdHost    script message handler -> JS -> host pipeline
// Also injects an early script that bridges window.chrome.webview to
// window.webkit.messageHandlers.nwdHost so viewer.html runs unchanged.
public class NwdWebViewHandler : ViewHandler<NwdWebView, WKWebView>
{
    public static IPropertyMapper<NwdWebView, NwdWebViewHandler> PropertyMapper = new PropertyMapper<NwdWebView, NwdWebViewHandler>(ViewMapper)
    {
        [nameof(NwdWebView.Source)] = MapSource,
    };

    public NwdWebViewHandler() : base(PropertyMapper) { }

    private NwdScriptMessageHandler? _scriptHandler;
    private NavDelegate? _navDelegate;

    protected override WKWebView CreatePlatformView()
    {
        var config = new WKWebViewConfiguration();

        // 1) app:// for viewer.html + vendor/ from the bundle.
        config.SetUrlSchemeHandler(new AppSchemeHandler(), AppSchemeHandler.Scheme);

        // 2) files:// for per-tab picked files.
        config.SetUrlSchemeHandler(new NwdViewerSchemeHandler(new TabFileStore()), NwdViewerSchemeHandler.Scheme);

        // 3) Script message handler. The closure is bound here; the actual
        // VirtualView reference is read at message time so swapping the
        // virtual view (rare in MAUI) does not leak a stale one.
        _scriptHandler = new NwdScriptMessageHandler(json => VirtualView?.RaiseMessageReceived(json));
        config.UserContentController.AddScriptMessageHandler(_scriptHandler, NwdScriptMessageHandler.Name);

        // 4) Early-injected shim: maps window.chrome.webview.* to window.webkit.messageHandlers.nwdHost.
        // This runs before any document script so viewer.html's first postHost call
        // works. Equivalent to the head-of-document <script> we keep in the HTML.
        var bridgeScript = new WKUserScript(
            (NSString)BridgeShimSource,
            WKUserScriptInjectionTime.AtDocumentStart,
            isForMainFrameOnly: false);
        config.UserContentController.AddUserScript(bridgeScript);

        var webView = new WKWebView(CGRect.Empty, config);

        // Recover from web-content-process termination (iOS jettisons it under memory
        // pressure while backgrounded — the page goes blank and JS state is lost).
        _navDelegate = new NavDelegate(this);
        webView.NavigationDelegate = _navDelegate;

        // For dev: enable WKWebView's web inspector if SDK supports it (iOS 16.4+).
        try { webView.SetValueForKey(NSObject.FromObject(true), (NSString)"inspectable"); }
        catch { /* older iOS, ignore */ }

        // iPad: turn off the bounce/scroll behavior that fights with three.js controls.
        webView.ScrollView.Bounces = false;
        webView.ScrollView.ScrollEnabled = false;
        webView.AllowsBackForwardNavigationGestures = false;
        webView.Opaque = true;

        return webView;
    }

    protected override void ConnectHandler(WKWebView platformView)
    {
        base.ConnectHandler(platformView);
        if (VirtualView is not null)
        {
            VirtualView.Sender = json =>
            {
                if (platformView is null) return false;
                // JsonSerializer.Serialize on a string returns a JSON-encoded
                // string literal — exactly what we need to safely interpolate
                // into JS source. e.g. {"a":1} -> "{\"a\":1}".
                var jsLiteral = System.Text.Json.JsonSerializer.Serialize(json);
                var script = $"window.dispatchEvent(new CustomEvent('HybridWebViewMessageReceived',{{detail: {jsLiteral} }}))";
                platformView.EvaluateJavaScript(script, (_, _) => { });
                return true;
            };
        }

        if (!string.IsNullOrEmpty(VirtualView?.Source))
            NavigateTo(platformView, VirtualView.Source!);
    }

    protected override void DisconnectHandler(WKWebView platformView)
    {
        try
        {
            platformView.Configuration.UserContentController.RemoveScriptMessageHandler(NwdScriptMessageHandler.Name);
        }
        catch { }
        if (VirtualView is not null) VirtualView.Sender = null;
        base.DisconnectHandler(platformView);
    }

    private static void MapSource(NwdWebViewHandler handler, NwdWebView view)
    {
        if (handler.PlatformView is null || string.IsNullOrEmpty(view.Source)) return;
        NavigateTo(handler.PlatformView, view.Source!);
    }

    private static void NavigateTo(WKWebView webView, string url)
    {
        var nsUrl = NSUrl.FromString(url);
        if (nsUrl is null) return;
        var request = new NSUrlRequest(nsUrl);
        webView.LoadRequest(request);
    }

    // WKNavigationDelegate that catches the web content process being killed and
    // reloads viewer.html. The control event lets the host reset its ready handshake
    // and arm a re-hydrate so the current tabs replay into the fresh JS world.
    private sealed class NavDelegate : WKNavigationDelegate
    {
        private readonly NwdWebViewHandler _handler;
        public NavDelegate(NwdWebViewHandler handler) => _handler = handler;

        public override void WebViewWebContentProcessDidTerminate(WKWebView webView)
        {
            // Fire first (synchronously) so the bridge re-queues + arms re-hydrate
            // before the reloaded page posts 'ready'.
            _handler.VirtualView?.RaiseWebContentProcessTerminated();
            var source = _handler.VirtualView?.Source;
            if (!string.IsNullOrEmpty(source))
                NavigateTo(webView, source!);
        }
    }

    // Bridge shim: viewer.html already has a JS-side polyfill that handles
    // window.HybridWebView; we satisfy it here by wiring a SendRawMessage shim
    // and dispatching incoming host messages via a CustomEvent the polyfill listens for.
    private const string BridgeShimSource = """
        (function () {
          if (window.HybridWebView) return;
          window.HybridWebView = {
            SendRawMessage: function (s) {
              try { window.webkit.messageHandlers.nwdHost.postMessage(typeof s === 'string' ? s : JSON.stringify(s)); }
              catch (e) { console.error('[bridge] post failed', e); }
            },
            // The polyfill in viewer.html listens for the
            // 'HybridWebViewMessageReceived' DOM event; the Sender lambda in
            // ConnectHandler dispatches that event with detail = json string.
          };
        })();
        """;
}
