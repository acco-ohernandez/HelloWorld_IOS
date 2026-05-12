using System.Diagnostics;

namespace HelloWorld_IOS.Services;

/// <summary>
/// Thin static facade so call sites can write a single line and have it tee to both
/// Debug.WriteLine (for VS Output) and the persistent SessionLogger (for the in-app
/// Diagnostics page). Initialized once during MauiProgram.CreateMauiApp().
/// </summary>
public static class Logger
{
    private static SessionLogger? _session;

    public static void Init(SessionLogger session) => _session = session;

    public static void Write(string tag, string message)
    {
        if (_session != null) _session.Write(tag, message);
        else Debug.WriteLine($"[{tag}] {message}");
    }

    public static void WriteException(string tag, Exception ex, string? context = null)
    {
        if (_session != null) _session.WriteException(tag, ex, context);
        else Debug.WriteLine($"[{tag}] {context}: {ex}");
    }
}
