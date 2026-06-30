using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using HelloWorld_IOS.Services;
using NwdViewer.Aps;

namespace HelloWorld_IOS.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly CredentialStore _credentials;
    private readonly Func<ApsCredentials, ApsServices> _apsFactory;
    private ApsServices? _aps;     // cached across calls; recreated when credentials change.
    private int _nextTabId = 1;

    [ObservableProperty] private string statusText = "Ready.";
    [ObservableProperty] private int progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeterminateProgress))]
    private bool isBusy;

    // True only during the APS translate-poll phase, where APS reports progress=0% for almost
    // the whole job — so a determinate bar looks frozen. The UI swaps to an indeterminate
    // ActivityIndicator + an elapsed-time status line instead.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeterminateProgress))]
    private bool isTranslating;

    [ObservableProperty] private TabViewModel? activeTab;

    /// <summary>Show the determinate progress bar only when busy but NOT mid-translate.</summary>
    public bool ShowDeterminateProgress => IsBusy && !IsTranslating;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public MainViewModel(CredentialStore credentials, Func<ApsCredentials, ApsServices> apsFactory)
    {
        _credentials = credentials;
        _apsFactory = apsFactory;
    }

    public int NewTabId() => _nextTabId++;

    public TabViewModel AddOfflineTab(string filePath)
    {
        var tab = new TabViewModel(NewTabId(), TabMode.Offline)
        {
            Title = Path.GetFileName(filePath),
            FilePath = filePath,
        };
        AddTab(tab);
        return tab;
    }

    public TabViewModel AddApsTab(string filePath, string urn, string token)
    {
        var tab = new TabViewModel(NewTabId(), TabMode.Aps)
        {
            Title = Path.GetFileName(filePath) + " (APS)",
            FilePath = filePath,
            Urn = urn,
            Token = token,
        };
        AddTab(tab);
        return tab;
    }

    private void AddTab(TabViewModel tab)
    {
        Tabs.Add(tab);
        Logger.Info("tab.open", $"id={tab.TabId} mode={tab.Mode} title='{tab.Title}'");
        SetActive(tab);
    }

    public void CloseTab(TabViewModel tab) => CloseTab(tab, reason: "user");

    public void CloseTab(TabViewModel tab, string reason)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.Remove(tab);
        Logger.Info("tab.close", $"id={tab.TabId} mode={tab.Mode} reason={reason}");
        if (ReferenceEquals(ActiveTab, tab))
        {
            var next = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
            SetActive(next);
        }
    }

    public void SetActive(TabViewModel? tab)
    {
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, tab);
        ActiveTab = tab;
    }

    public void PopulateOfflineProperties(TabViewModel tab, string name, string meshType, string material, int vertexCount)
    {
        tab.Properties.Clear();
        tab.Properties.Add(new PropertyNode { Key = "Name",     Value = name });
        tab.Properties.Add(new PropertyNode { Key = "Type",     Value = meshType });
        tab.Properties.Add(new PropertyNode { Key = "Material", Value = material });
        tab.Properties.Add(new PropertyNode { Key = "Vertices", Value = vertexCount.ToString() });
        StatusText = $"Selected: {name}";
    }

    // ===== APS surface ==========================================================

    public bool HasCredentials => _credentials.HasCredentials;

    /// <summary>Force a fresh ApsServices on next use. Call after the user updates credentials.</summary>
    public void InvalidateApsServices()
    {
        _aps?.Dispose();
        _aps = null;
    }

    private async Task<ApsServices> EnsureServicesAsync()
    {
        if (_aps != null) return _aps;
        var creds = await _credentials.LoadAsync()
            ?? throw new InvalidOperationException("APS credentials are not configured.");
        _aps = _apsFactory(creds);
        return _aps;
    }

    public async Task<(string urn, string token, string? modelGuid)> TranslateAsync(string localPath, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var aps = await EnsureServicesAsync();

            StatusText = "Ensuring APS bucket...";
            ProgressPercent = 0;
            await aps.Oss.EnsureBucketAsync(ct);

            var objectKey = Path.GetFileName(localPath);
            StatusText = $"Uploading {objectKey} to APS...";
            var uploadProgress = new Progress<int>(p => ProgressPercent = Math.Min(p, 95));
            var urn = await aps.Oss.UploadAsync(localPath, objectKey, uploadProgress, ct);
            Logger.Info("aps.upload", $"uploaded local={Path.GetFileName(localPath)} urn={urn}");

            StatusText = "Starting translation...";
            ProgressPercent = 0;
            await aps.ModelDerivative.StartTranslationAsync(urn, ct: ct);

            StatusText = "Translating… this can take several minutes for large models.";
            IsTranslating = true;
            var translateSw = Stopwatch.StartNew();
            await aps.ModelDerivative.WaitForTranslationAsync(urn,
                new Progress<int>(p =>
                {
                    // APS reports 0% for most of the job, so lead with elapsed time (which always
                    // advances) and append the percent only once it becomes meaningful.
                    var e = translateSw.Elapsed;
                    var pctText = p > 0 ? $" ({p}%)" : "";
                    StatusText = $"Translating… {(int)e.TotalMinutes:00}:{e.Seconds:00} elapsed{pctText}";
                }), ct);
            IsTranslating = false;

            var metadata = await aps.ModelDerivative.GetMetadataAsync(urn, ct);
            var primary = metadata.FirstOrDefault(m => m.Role == "3d") ?? metadata.FirstOrDefault();

            StatusText = "Fetching viewer token...";
            var token = await aps.Auth.GetViewerTokenAsync(ct);

            StatusText = $"Ready. URN: {urn}";
            ProgressPercent = 100;
            return (urn, token, primary?.Guid);
        }
        finally
        {
            IsBusy = false;
            IsTranslating = false;
        }
    }

    public async Task LoadApsPropertiesAsync(TabViewModel tab, int objectId, string modelGuid, CancellationToken ct = default)
    {
        if (_aps == null || tab.Urn == null) return;
        try
        {
            var props = await _aps.ModelDerivative.GetObjectPropertiesAsync(tab.Urn, modelGuid, objectId, ct);
            tab.Properties.Clear();

            var entry = props?.Data?.Collection?.FirstOrDefault();
            if (entry == null)
            {
                // APS sometimes returns 200 with no collection while still indexing.
                var raw = _aps.ModelDerivative.LastPropertiesRawBody ?? "(no body)";
                Logger.Warn("aps.properties", $"no collection for dbId={objectId}. Raw: {(raw.Length > 400 ? raw[..400] + "..." : raw)}");
                tab.Properties.Add(new PropertyNode { Key = "Info",
                    Value = "Properties not available yet — APS may still be indexing. Try again in a moment." });
                StatusText = $"Object #{objectId}: properties not yet available.";
                return;
            }

            tab.Properties.Add(new PropertyNode { Key = "Name", Value = entry.Name ?? "(unnamed)" });
            if (!string.IsNullOrEmpty(entry.ExternalId))
                tab.Properties.Add(new PropertyNode { Key = "External ID", Value = entry.ExternalId });
            if (entry.Properties != null)
            {
                foreach (var category in entry.Properties)
                {
                    var catNode = new PropertyNode { Key = category.Key };
                    if (category.Value != null)
                    {
                        foreach (var prop in category.Value)
                            catNode.Children.Add(new PropertyNode { Key = prop.Key, Value = prop.Value?.ToString() ?? string.Empty });
                    }
                    tab.Properties.Add(catNode);
                }
            }
            StatusText = $"Selected: {entry.Name ?? $"#{objectId}"}";
        }
        catch (Exception ex)
        {
            Logger.Error("aps.properties", $"failed to load properties for object {objectId}", ex);
            StatusText = $"Properties error: {ex.Message}";
        }
    }

    public void Dispose() => _aps?.Dispose();
}
