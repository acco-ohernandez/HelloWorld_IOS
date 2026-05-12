using System.Collections.ObjectModel;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using HelloWorld_IOS.Services;

namespace HelloWorld_IOS.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly SessionLogger _logger;

    [ObservableProperty] private SessionLogEntry? selected;
    [ObservableProperty] private string selectedContent = string.Empty;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private bool isBusy;

    public ObservableCollection<SessionLogEntry> Sessions { get; } = new();

    public DiagnosticsViewModel(SessionLogger logger)
    {
        _logger = logger;
    }

    public void Refresh()
    {
        Sessions.Clear();
        foreach (var f in _logger.EnumerateSessions())
            Sessions.Add(new SessionLogEntry(f.FullName, f.Name, f.Length, f.LastWriteTime));
        StatusText = Sessions.Count == 0
            ? "No sessions yet."
            : $"{Sessions.Count} session(s). Current is at the top.";
        // Auto-pick the most recent so Share / Delete work without an explicit tap.
        if (Sessions.Count > 0) Selected = Sessions[0];
    }

    partial void OnSelectedChanged(SessionLogEntry? value)
    {
        if (value is null) { SelectedContent = string.Empty; return; }
        try
        {
            // Use FileShare.ReadWrite so we can read the live session file while it's still being written to.
            using var fs = new FileStream(value.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            SelectedContent = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            SelectedContent = $"Failed to read log: {ex.Message}";
        }
    }

    public async Task ShareSelectedAsync()
    {
        var target = Selected ?? (Sessions.Count > 0 ? Sessions[0] : null);
        if (target is null) { StatusText = "Nothing to share."; return; }
        try
        {
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = $"Export {target.Name}",
                File = new ShareFile(target.FullPath),
            });
            StatusText = $"Shared {target.Name}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Share failed: {ex.Message}";
        }
    }

    public async Task ShareAllAsZipAsync()
    {
        if (Sessions.Count == 0) { StatusText = "Nothing to export."; return; }
        try
        {
            IsBusy = true;
            var zipPath = Path.Combine(FileSystem.CacheDirectory,
                $"hellworld_logs_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            // Copy files to a temp staging dir first so we don't trip on the live writer.
            var staging = Path.Combine(FileSystem.CacheDirectory, $"logs_export_{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var s in Sessions)
                {
                    var dest = Path.Combine(staging, s.Name);
                    using var src = new FileStream(s.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dst = File.Create(dest);
                    await src.CopyToAsync(dst);
                }
                ZipFile.CreateFromDirectory(staging, zipPath);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best-effort */ }
            }

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Export all session logs",
                File = new ShareFile(zipPath),
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void DeleteSelected()
    {
        if (Selected is null) { StatusText = "Select a session first."; return; }
        // Refuse to delete the currently-open log so the writer doesn't fault.
        if (string.Equals(Selected.FullPath, _logger.SessionFilePath, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Can't delete the active session log.";
            return;
        }
        try
        {
            File.Delete(Selected.FullPath);
            Selected = null;
            SelectedContent = string.Empty;
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
        }
    }

    public void DeleteAll()
    {
        var current = _logger.SessionFilePath;
        var removed = 0;
        foreach (var s in Sessions.ToList())
        {
            if (string.Equals(s.FullPath, current, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(s.FullPath); removed++; } catch { /* skip */ }
        }
        Selected = null;
        SelectedContent = string.Empty;
        Refresh();
        StatusText = $"Cleared {removed} session(s). Active session kept.";
    }
}

public sealed record SessionLogEntry(string FullPath, string Name, long SizeBytes, DateTime LastWriteTime)
{
    public string DisplaySize => SizeBytes < 1024
        ? $"{SizeBytes} B"
        : SizeBytes < 1024 * 1024
            ? $"{SizeBytes / 1024.0:F1} KB"
            : $"{SizeBytes / (1024.0 * 1024.0):F2} MB";
    public string DisplayTime => LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
}
