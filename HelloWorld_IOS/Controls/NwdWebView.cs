namespace HelloWorld_IOS.Controls;

// Cross-platform façade for the WKWebView (iOS) / future WebView2 (Windows)
// host. Exposes only what ViewerPage needs:
//   - a Source URL to navigate to (the viewer.html entry point)
//   - SendMessage(string) for host -> JS
//   - MessageReceived event for JS -> host
// Implementation lives in Platforms/iOS/NwdWebViewHandler.cs.
public class NwdWebView : View
{
    public static readonly BindableProperty SourceProperty =
        BindableProperty.Create(nameof(Source), typeof(string), typeof(NwdWebView), default(string));

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public event EventHandler<string>? MessageReceived;

    // Raised when iOS jettisons the WKWebView's web content process (memory pressure
    // while backgrounded). The page is blank and all JS state is gone; the host resets
    // the bridge handshake and re-hydrates tabs after the page reloads.
    public event EventHandler? WebContentProcessTerminated;

    // Populated by the platform handler once the WKWebView is alive. Returns
    // false if the platform view hasn't been constructed yet (rare; usually
    // ViewerPage waits for Loaded before sending).
    internal Func<string, bool>? Sender { get; set; }

    public bool SendMessage(string json) => Sender?.Invoke(json) ?? false;

    internal void RaiseMessageReceived(string json)
        => MessageReceived?.Invoke(this, json);

    internal void RaiseWebContentProcessTerminated()
        => WebContentProcessTerminated?.Invoke(this, EventArgs.Empty);
}
