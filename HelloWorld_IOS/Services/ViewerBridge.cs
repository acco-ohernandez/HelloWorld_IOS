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

    public ViewerBridge(MainViewModel vm)
    {
        _vm = vm;
    }

    // Wire the host->JS sender. ViewerPage calls this after HybridWebView is
    // constructed: `bridge.AttachSender(json => webView.SendRawMessage(json));`
    public void AttachSender(Action<string> send) => _send = send;

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
                    Logger.Info("viewer.js", $"apsDiag: {GetStringOrNull(doc.RootElement, "msg") ?? ""}");
                    break;

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
