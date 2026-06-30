using System.Reflection;

namespace HelloWorld_IOS.Services;

/// <summary>
/// Build identity surfaced in the status bar + app.start log so you can tell at a glance whether
/// the running app is the latest build. The timestamp is injected at compile time via an
/// AssemblyMetadataAttribute (see HelloWorld_IOS.csproj), so it changes on every (re)build —
/// unlike the version/build number, which only moves when bumped.
/// </summary>
public static class BuildInfo
{
    /// <summary>Compile time (Pacific), e.g. "2026-06-30 15:05 PST", or "unknown" if not injected.</summary>
    public static string Timestamp { get; }

    /// <summary>e.g. "v1.0 (1) · 2026-06-30 15:05 PST".</summary>
    public static string Stamp { get; }

    static BuildInfo()
    {
        var ts = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value;
        Timestamp = string.IsNullOrEmpty(ts) ? "unknown" : ts + " PST";
        Stamp = $"v{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString}) · {Timestamp}";
    }
}
