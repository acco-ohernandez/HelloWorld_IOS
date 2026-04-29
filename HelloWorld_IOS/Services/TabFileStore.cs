namespace HelloWorld_IOS.Services;

// Maps tabId -> a per-tab folder under FileSystem.CacheDirectory.
// Keeps imported model files alive until the tab is closed (then the folder
// is recursively deleted so iOS can reclaim cache space).
//
// The path layout mirrors the URL space served by NwdViewerSchemeHandler:
//   {CacheDirectory}/tabs/{tabId}/{filename}
//   nwdviewer-files://{tabId}/{filename}
public sealed class TabFileStore
{
    private static readonly string Root = Path.Combine(FileSystem.CacheDirectory, "tabs");

    public string EnsureTabDirectory(int tabId)
    {
        var dir = Path.Combine(Root, tabId.ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetTabDirectory(int tabId) => Path.Combine(Root, tabId.ToString());

    public bool TryResolve(int tabId, string filename, out string fullPath)
    {
        fullPath = string.Empty;
        var tabDir = GetTabDirectory(tabId);
        if (!Directory.Exists(tabDir)) return false;

        // Path-traversal guard: same rules as MainWindow.xaml.cs OnWebResourceRequested.
        if (string.IsNullOrEmpty(filename)) return false;
        if (filename.IndexOfAny(['\0', '\\', ':']) >= 0) return false;
        if (filename.StartsWith('/')) return false;
        if (filename.Contains("..")) return false;
        if (Path.IsPathRooted(filename)) return false;

        var candidate = Path.GetFullPath(Path.Combine(tabDir, filename));
        var rootFull = Path.GetFullPath(tabDir) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootFull, StringComparison.Ordinal)) return false;
        if (!File.Exists(candidate)) return false;

        fullPath = candidate;
        return true;
    }

    public void Delete(int tabId)
    {
        try
        {
            var dir = GetTabDirectory(tabId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup; iOS may still hold a handle */ }
    }

    public string BuildUrl(int tabId, string filename)
        => $"nwdviewer-files://{tabId}/{Uri.EscapeDataString(filename)}";
}
