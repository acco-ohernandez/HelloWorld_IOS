using System.Text.Json;
using HelloWorld_IOS.ViewModels;

namespace HelloWorld_IOS.Services;

// Mirrors MainWindow.xaml.cs OnViewerMessage / PostOrQueue from the WPF source.
// All message types (offline + APS) are routed; v1 logs APS-only messages but
// does not act on them.
public sealed class ViewerBridge
{
    private readonly MainViewModel _vm;
    private readonly Queue<string> _pendingMessages = new();
    private bool _jsReady;
    private Action<string>? _send;
    private Action? _rehydrate;
    private bool _rehydratePending;

    public ViewerBridge(MainViewModel vm)
    {
        _vm = vm;
    }

    // Wire the host->JS sender. ViewerPage calls this after HybridWebView is
    // constructed: `bridge.AttachSender(json => webView.SendRawMessage(json));`
    public void AttachSender(Action<string> send) => _send = send;

    // Wire a one-shot re-hydrate callback that replays the current tabs into a freshly
    // reloaded viewer.html (after a WebView content-process crash). Invoked on the next 'ready'.
    public void AttachRehydrator(Action rehydrate) => _rehydrate = rehydrate;

    /// <summary>
    /// Call before reloading viewer.html (e.g. after a WebView content-process crash):
    /// resets the ready handshake so outbound messages re-queue until the fresh page posts
    /// 'ready', and arms a one-shot re-hydrate to replay the current tabs into the new JS world.
    /// </summary>
    public void PrepareForReload()
    {
        _jsReady = false;
        _rehydratePending = true;
    }

    // Ask the viewer to verify its WebGL context survived a background (called on app resume).
    public void CheckHealth() => PostOrQueue(new { type = "checkHealth" });

    public void PostOrQueue(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (_jsReady && _send is not null)
        {
            _send(json);
        }
        else
        {
            _pendingMessages.Enqueue(json);
        }
    }

    // ---- Outbound message helpers (kept symmetric with the WPF bridge) -------

    public void CreateTab(int tabId, TabMode mode)
        => PostOrQueue(new { type = "createTab", tabId, mode = mode == TabMode.Aps ? "aps" : "offline" });

    public void SwitchTab(int tabId)
        => PostOrQueue(new { type = "switchTab", tabId });

    public void CloseTab(int tabId)
        => PostOrQueue(new { type = "closeTab", tabId });

    public void LoadOffline(int tabId, string url, string format)
        => PostOrQueue(new { type = "loadOffline", tabId, url, format });

    public void SetTheme(string theme)
        => PostOrQueue(new { type = "setTheme", theme });

    // v2 seams — JSON shape per CLAUDE.md "WPF <-> JS bridge" section.
    public void LoadAps(int tabId, string urn, string token)
        => PostOrQueue(new { type = "loadAps", tabId, urn, token });

    public void CaptureImage(string format = "png", int scale = 1, string purpose = "image")
        => PostOrQueue(new { type = "captureImage", format, scale, purpose });

    // ---- Inbound dispatcher (called from HybridWebView.RawMessageReceived) ---

    public void OnRawMessageReceived(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "ready":
                    _jsReady = true;
                    if (_send is not null)
                    {
                        while (_pendingMessages.Count > 0) _send(_pendingMessages.Dequeue());
                    }
                    // A re-ready after a content-process crash: replay the live tabs into
                    // the fresh JS world so the user's open models come back.
                    if (_rehydratePending)
                    {
                        _rehydratePending = false;
                        _rehydrate?.Invoke();
                    }
                    break;

                case "loaded":
                    _vm.StatusText = "Loaded.";
                    _vm.IsBusy = false;
                    _vm.ProgressPercent = 100;
                    break;

                case "loadStart":
                    _vm.IsBusy = true;
                    _vm.ProgressPercent = 0;
                    var fmt = GetStringOrNull(doc.RootElement, "format");
                    _vm.StatusText = string.IsNullOrEmpty(fmt) ? "Loading..." : $"Loading {fmt!.ToUpperInvariant()}...";
                    break;

                case "loadProgress":
                    _vm.ProgressPercent = GetIntOrNull(doc.RootElement, "percent") ?? 0;
                    break;

                case "loadEnd":
                    _vm.IsBusy = false;
                    break;

                case "selectionOffline":
                {
                    var tabId = GetIntOrNull(doc.RootElement, "tabId");
                    var tab = FindTab(tabId);
                    if (tab is not null)
                    {
                        _vm.PopulateOfflineProperties(tab,
                            name:        GetStringOrNull(doc.RootElement, "name") ?? "(unknown)",
                            meshType:    GetStringOrNull(doc.RootElement, "meshType") ?? string.Empty,
                            material:    GetStringOrNull(doc.RootElement, "material") ?? string.Empty,
                            vertexCount: GetIntOrNull(doc.RootElement, "vertexCount") ?? 0);
                    }
                    break;
                }

                case "selection":
                {
                    var tabId = GetIntOrNull(doc.RootElement, "tabId");
                    var dbId  = GetIntOrNull(doc.RootElement, "dbId");
                    var tab   = FindTab(tabId);
                    if (tab is { Mode: TabMode.Aps, ApsModelGuid: { } guid } && dbId is { } id)
                    {
                        _ = _vm.LoadApsPropertiesAsync(tab, id, guid);
                    }
                    else if (tab is { Mode: TabMode.Aps, ApsModelGuid: null })
                    {
                        _vm.StatusText = "Cannot load properties — APS model GUID not yet available.";
                    }
                    break;
                }

                case "apsDiag":
                {
                    var diagMsg = GetStringOrNull(doc.RootElement, "msg") ?? "";
                    // Gate nav.* events (button presses, theme toggles, panel
                    // collapses) on the Settings toggle. Everything else
                    // (fit:, fit.frame:, pre-load, post-load, …) is unaffected.
                    if (diagMsg.StartsWith("nav.", StringComparison.Ordinal) && !Logger.VerboseNavLogging)
                        break;
                    Logger.Info("viewer.js", $"apsDiag: {diagMsg}");
                    break;
                }

                case "webglEvent":
                {
                    // WebGL context lifecycle from viewer.html (lost on background GPU reclaim,
                    // restored when the context comes back, reinit after a forced re-create).
                    // Logged so the disappearing-model symptom is diagnosable from the session
                    // log directly instead of inferred from app.lifecycle churn.
                    var evt = GetStringOrNull(doc.RootElement, "event") ?? "?";
                    var detail = GetStringOrNull(doc.RootElement, "detail");
                    var line = detail is null ? $"context {evt}" : $"context {evt}: {detail}";
                    if (evt == "lost") Logger.Warn("viewer.webgl", line);
                    else Logger.Info("viewer.webgl", line);
                    break;
                }

                case "imageData":
                    // v3: image/PDF capture path. Not in v2.
                    Logger.Warn("viewer.js", "imageData received without an active capture request (v3 feature)");
                    break;

                case "error":
                    _vm.StatusText = "Viewer error: " + (GetStringOrNull(doc.RootElement, "message") ?? "unknown");
                    break;

                case "jsError":
                    var jsMsg   = GetStringOrNull(doc.RootElement, "message");
                    var jsFile  = GetStringOrNull(doc.RootElement, "filename");
                    var jsLine  = GetIntOrNull(doc.RootElement, "lineno");
                    var jsStack = GetStringOrNull(doc.RootElement, "stack");
                    Logger.Error("viewer.js", $"{jsMsg} @ {jsFile}:{jsLine}\n{jsStack}");
                    _vm.StatusText = $"JS error: {jsMsg}";
                    _vm.IsBusy = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("bridge.error", $"failed to parse: {rawJson}", ex);
        }
    }

    private TabViewModel? FindTab(int? id)
        => id.HasValue ? _vm.Tabs.FirstOrDefault(t => t.TabId == id.Value) : null;

    private static string? GetStringOrNull(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetIntOrNull(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : null;
}
