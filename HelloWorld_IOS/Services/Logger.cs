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
///   bridge.error    JS bridge parse failures
///   viewer.js       JS-side errors / diag messages
/// </summary>
public static class Logger
{
    private static SessionLogger? _session;

    /// <summary>Preferences key for the "verbose navigation logging" toggle. Default true.</summary>
    public const string VerboseNavLoggingKey = "NwdViewer.VerboseNavLogging";

    /// <summary>
    /// When true (default), nav.* lines from the JS viewer (button presses,
    /// theme toggles, panel collapses) get written to the session log.
    /// When false, those lines are dropped at the bridge before reaching
    /// SessionLogger. Diagnostic dumps (fit:, fit.frame:, aps.* HTTP) are
    /// unaffected.
    /// </summary>
    public static bool VerboseNavLogging
    {
        get => Preferences.Default.Get(VerboseNavLoggingKey, true);
        set => Preferences.Default.Set(VerboseNavLoggingKey, value);
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
