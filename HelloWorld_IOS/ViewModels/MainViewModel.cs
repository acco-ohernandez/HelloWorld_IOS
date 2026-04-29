using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HelloWorld_IOS.Services;

namespace HelloWorld_IOS.ViewModels;

// v2-APS-port-notes:
//   When NwdViewer.Aps is ported into this solution, the APS surface is reactivated by:
//     1. Injecting the ApsServices class (or per-client ApsAuth / ApsOss / ApsModelDerivative)
//        via the constructor — same shape as the WPF MainViewModel.
//     2. Calling AddApsTab / TranslateAsync / LoadApsPropertiesAsync from a Settings-gated
//        toolbar button. v1 leaves the toolbar entry hidden but the methods compile.
//     3. The credential store is already a v2 seam (Services/CredentialStore.cs) — no change
//        needed when wiring real APS calls.
public sealed partial class MainViewModel : ObservableObject
{
    private readonly CredentialStore _credentials;
    private int _nextTabId = 1;

    [ObservableProperty] private string statusText = "Ready.";
    [ObservableProperty] private int progressPercent;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private TabViewModel? activeTab;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public MainViewModel(CredentialStore credentials)
    {
        _credentials = credentials;
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

    private void AddTab(TabViewModel tab)
    {
        Tabs.Add(tab);
        SetActive(tab);
    }

    public void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.Remove(tab);
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

    // ===== v2 APS seams (intentionally throwing) =================================
    // These compile but are not call-sited in v1. When NwdViewer.Aps is ported,
    // remove the throws and forward to the real ApsServices instance.

    public bool HasCredentials => _credentials.HasCredentials;

    public TabViewModel AddApsTab(string filePath, string urn, string token)
        => throw new NotImplementedException("APS path is a v2 deliverable.");

    public Task<(string urn, string token, string? modelGuid)> TranslateAsync(string localPath, CancellationToken ct = default)
        => throw new NotImplementedException("APS path is a v2 deliverable.");

    public Task LoadApsPropertiesAsync(TabViewModel tab, int objectId, string modelGuid, CancellationToken ct = default)
        => throw new NotImplementedException("APS path is a v2 deliverable.");
}
