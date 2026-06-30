using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HelloWorld_IOS.Services;

public enum LogLevel { Info, Warn, Error }

/// <summary>
/// Per-app-launch log file in {AppDataDirectory}/logs/. Keeps the most recent
/// 50 sessions; older files are pruned at startup. Writes are flushed per line
/// so a crash doesn't lose the last entry. Surfaced to the user via DiagnosticsPage.
///
/// Format: HH:mm:ss.fff [LEVEL] [category.subcategory] message
///   - LEVEL is one of INFO / WARN / ERROR, padded to 5 chars.
///   - category uses a dotted hierarchy: aps.upload, aps.translate, app.start, etc.
///   - secrets (access_token / client_secret / Bearer tokens) are redacted before write.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    public const int MaxRetainedSessions = 50;

    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    private bool _disposed;

    public string SessionFilePath { get; }
    public string LogDirectory { get; }

    public SessionLogger()
    {
        LogDirectory = Path.Combine(FileSystem.AppDataDirectory, "logs");
        Directory.CreateDirectory(LogDirectory);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        SessionFilePath = Path.Combine(LogDirectory, $"{stamp}.log");

        try
        {
            PruneOldSessions();
            _writer = new StreamWriter(new FileStream(
                SessionFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };

            // Build-identity header: lets you correlate a log with the .ipa that produced it.
            Info("app.start", $"NwdViewer v{AppInfo.Current.VersionString} build {AppInfo.Current.BuildString}");
            Info("app.start", $"Built: {BuildInfo.Timestamp}");
            Info("app.start", $"Device: {DeviceInfo.Manufacturer} {DeviceInfo.Model} · {DeviceInfo.Platform} {DeviceInfo.VersionString} · {DeviceInfo.Idiom}");
            Info("app.start", $"App data: {FileSystem.AppDataDirectory}");
            Info("app.start", $"Log dir:  {LogDirectory}");
        }
        catch (Exception ex)
        {
            // Logging must never crash the app. If we can't open the file, fall back to Debug.
            Debug.WriteLine($"[SessionLogger] failed to open log file: {ex}");
            _writer = null;
        }
    }

    public void Info(string category, string message)  => Write(LogLevel.Info, category, message);
    public void Warn(string category, string message)  => Write(LogLevel.Warn, category, message);
    public void Error(string category, string message, Exception? ex = null)
    {
        var text = ex is null
            ? message
            : $"{message}\n    {ex.GetType().Name}: {ex.Message}\n    {ex.StackTrace}";
        Write(LogLevel.Error, category, text);
    }

    private void Write(LogLevel level, string category, string message)
    {
        if (_disposed) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{LevelTag(level)}] [{category}] {Redact(message)}";
        Debug.WriteLine(line);
        if (_writer == null) return;
        lock (_gate)
        {
            try { _writer.WriteLine(line); }
            catch (Exception ex) { Debug.WriteLine($"[SessionLogger] write failed: {ex.Message}"); }
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Info  => "INFO ",
        LogLevel.Warn  => "WARN ",
        LogLevel.Error => "ERROR",
        _              => "?????",
    };

    public IReadOnlyList<FileInfo> EnumerateSessions()
    {
        if (!Directory.Exists(LogDirectory)) return Array.Empty<FileInfo>();
        return new DirectoryInfo(LogDirectory)
            .EnumerateFiles("*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try { _writer?.Dispose(); }
            catch { /* ignore */ }
        }
    }

    private void PruneOldSessions()
    {
        try
        {
            var files = new DirectoryInfo(LogDirectory)
                .EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxRetainedSessions - 1)  // -1 to leave room for the about-to-be-created file
                .ToList();
            foreach (var f in files)
            {
                try { f.Delete(); }
                catch { /* a single failed delete shouldn't block startup */ }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionLogger] prune failed: {ex.Message}");
        }
    }

    // Defence-in-depth so an APS error body or stray paste never bleeds secrets into a shared log.
    private static readonly Regex AccessTokenPattern = new(@"""access_token""\s*:\s*""[^""]*""", RegexOptions.Compiled);
    private static readonly Regex ClientSecretPattern = new(@"""client_secret""\s*:\s*""[^""]*""", RegexOptions.Compiled);
    private static readonly Regex BearerPattern = new(@"Bearer\s+[A-Za-z0-9\-_.]+", RegexOptions.Compiled);

    public static string Redact(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = AccessTokenPattern.Replace(s, @"""access_token"":""***""");
        s = ClientSecretPattern.Replace(s, @"""client_secret"":""***""");
        s = BearerPattern.Replace(s, "Bearer ***");
        return s;
    }
}
