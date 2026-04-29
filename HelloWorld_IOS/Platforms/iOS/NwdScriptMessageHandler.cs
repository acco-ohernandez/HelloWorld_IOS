using Foundation;
using WebKit;

namespace HelloWorld_IOS.Platforms.iOS;

// Receives messages posted from JS via
//   window.webkit.messageHandlers.nwdHost.postMessage(...)
// and forwards them to a callback.
public sealed class NwdScriptMessageHandler : NSObject, IWKScriptMessageHandler
{
    public const string Name = "nwdHost";
    private readonly Action<string> _onMessage;

    public NwdScriptMessageHandler(Action<string> onMessage)
    {
        _onMessage = onMessage;
    }

    [Export("userContentController:didReceiveScriptMessage:")]
    public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
    {
        var body = message.Body;
        var json = body switch
        {
            NSString s => s.ToString(),
            NSDictionary d => d.ToString(),
            _ => body?.ToString() ?? string.Empty,
        };
        _onMessage(json);
    }
}
