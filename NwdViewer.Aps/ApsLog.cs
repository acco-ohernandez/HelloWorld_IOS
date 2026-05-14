namespace NwdViewer.Aps;

/// <summary>
/// Logging contract for APS client diagnostics. Implemented by the host app
/// (iOS layer) and registered via <see cref="ApsLog.SetSink"/>. The library
/// itself never references MAUI/iOS types; this seam keeps NwdViewer.Aps
/// UI-agnostic while still giving the in-app Diagnostics page visibility
/// into every APS HTTP call.
/// </summary>
public interface IApsLogger
{
    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message, Exception? ex = null);
}

/// <summary>
/// Static facade so library code can log via a single line without threading
/// an ILogger through every constructor. If no sink is set (e.g., in tests),
/// calls are silently no-op.
/// </summary>
public static class ApsLog
{
    private static IApsLogger? _sink;

    public static void SetSink(IApsLogger? sink) => _sink = sink;

    public static void Info(string category, string message) => _sink?.Info(category, message);
    public static void Warn(string category, string message) => _sink?.Warn(category, message);
    public static void Error(string category, string message, Exception? ex = null) => _sink?.Error(category, message, ex);
}
