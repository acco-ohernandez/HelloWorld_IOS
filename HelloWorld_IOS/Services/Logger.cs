using System.Diagnostics;

namespace HelloWorld_IOS.Services;

/// <summary>
/// Thin static facade so call sites can write a single line and have it tee to both
/// Debug.WriteLine (for VS Output) and the persistent SessionLogger (for the in-app
/// Diagnostics page). Initialized once during MauiProgram.CreateMauiApp().
///
/// Categories use a dotted hierarchy so a grep finds related lines quickly:
///   app.start       app launch + build identity
///   app.lifecycle   OnStart/OnSleep/OnResume + memory warnings
///   app.settings    credentials/bucket changes (non-secret diff only)
///   file.pick       file picker results
///   tab.open/close  tab lifecycle
///   aps.auth        token requests, scope, cache hit/miss
///   aps.bucket      bucket create/exists
///   aps.upload      OSS signed-S3 upload steps
///   aps.translate   Model Derivative job POST
///   aps.manifest    manifest polling cycles
///   aps.metadata    metadata fetch
///   aps.properties  object-properties fetch
///   aps.error       full 4xx/5xx response bodies
///   app.memory      iOS memory warnings (precede WebView content-process kills)
///   bridge.error    JS bridge parse failures
///   viewer.js       JS-side errors / diag messages
/// </summary>
public static class Logger
{
    private static SessionLogger? _session;

    /// <summary>
    /// Session-only "Verbose Logging" toggle (Settings page). Deliberately NOT
    /// persisted: resets to false on every app launch so field devices never
    /// accumulate noisy logs from a toggle someone forgot on. When true, nav.*
    /// and vrb.* lines from the JS viewer (button presses, camera start/stop,
    /// load progress ticks) get written to the session log; when false they are
    /// dropped at the bridge. Diagnostic dumps (fit:, watchdog, load milestones,
    /// aps.* HTTP) are always logged regardless.
    /// </summary>
    public static bool VerboseLogging { get; set; }

    /// <summary>Log an Info line only while the session-only Verbose Logging toggle is on.</summary>
    public static void Verbose(string category, string message)
    {
        if (VerboseLogging) Info(category, message);
    }

    public static void Init(SessionLogger session) => _session = session;

    public static void Info(string category, string message)
    {
        if (_session != null) _session.Info(category, message);
        else Debug.WriteLine($"[INFO ] [{category}] {message}");
    }

    public static void Warn(string category, string message)
    {
        if (_session != null) _session.Warn(category, message);
        else Debug.WriteLine($"[WARN ] [{category}] {message}");
    }

    public static void Error(string category, string message, Exception? ex = null)
    {
        if (_session != null) _session.Error(category, message, ex);
        else Debug.WriteLine($"[ERROR] [{category}] {message}{(ex == null ? "" : $" :: {ex}")}");
    }
}
