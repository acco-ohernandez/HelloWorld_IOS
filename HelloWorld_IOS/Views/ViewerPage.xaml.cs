using HelloWorld_IOS.Controls;
using HelloWorld_IOS.Services;
using HelloWorld_IOS.ViewModels;

namespace HelloWorld_IOS.Views;

public partial class ViewerPage : ContentPage
{
    private readonly MainViewModel _vm;
    private readonly TabFileStore _store;
    private readonly PickedFileImporter _importer;
    private readonly ViewerBridge _bridge;
    private bool _viewerReady;
    private string _theme = "light";
    private bool _propertiesCollapsed;       // user-toggled state; sticks across rotations
    private bool _orientationInitialized;    // guards the per-orientation default seed

    public ViewerPage(MainViewModel vm, TabFileStore store, PickedFileImporter importer)
    {
        InitializeComponent();
        _vm = vm;
        _store = store;
        _importer = importer;
        _bridge = new ViewerBridge(vm);
        BindingContext = vm;

        Viewer.MessageReceived += OnViewerMessage;
        // The custom URL scheme is registered on iOS by NwdWebViewHandler.
        // Other TFMs are not supported in v1; the Windows TFM compiles but
        // the WebView won't render content there (the Mac is the deploy target).
        Viewer.Source = "nwdviewer-app:///viewer.html";

        // Send-side: wired once the platform handler installs Viewer.Sender.
        // We attach lazily via a Loaded callback on the Viewer.
        Loaded += OnPageLoaded;
        SizeChanged += OnPageSizeChanged;

        // Watch ActiveTab so we can post a switchTab and toggle properties visibility.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveTab) && _vm.ActiveTab is not null)
            {
                _bridge.SwitchTab(_vm.ActiveTab.TabId);
                UpdatePropertiesVisibility();
            }
        };

        DropGestureRecognizer drop = new() { AllowDrop = true };
        drop.Drop += OnRootDrop;
        ((Grid)Content).GestureRecognizers.Add(drop);
    }

    private void OnPageLoaded(object? sender, EventArgs e)
    {
        _bridge.AttachSender(json => Viewer.SendMessage(json));
        ApplyOrientationLayout();
    }

    private void OnPageSizeChanged(object? sender, EventArgs e) => ApplyOrientationLayout();

    private void ApplyOrientationLayout()
    {
        // Initial-load defaults: panel expanded in landscape, collapsed in
        // portrait (320 pt is ~40% of an iPad in portrait, so collapsed feels
        // right by default). After first layout, _propertiesCollapsed is the
        // user's explicit toggle and survives rotations.
        var landscape = Width > Height && Width > 600;
        if (!_orientationInitialized && Width > 0 && Height > 0)
        {
            _propertiesCollapsed = !landscape;
            _orientationInitialized = true;
        }
        var showPanel = !_propertiesCollapsed;
        BodyGrid.ColumnDefinitions[1].Width = new GridLength(showPanel ? 1 : 0);
        BodyGrid.ColumnDefinitions[2].Width = new GridLength(showPanel ? 320 : 0);
        UpdatePropertiesVisibility();
        UpdatePropertiesToggleLabel();
    }

    private void UpdatePropertiesVisibility()
    {
        var visible = BodyGrid.ColumnDefinitions[2].Width.Value > 0;
        PropertiesPanel.IsVisible = visible && _vm.ActiveTab is not null;
    }

    private void UpdatePropertiesToggleLabel()
    {
        // ◀ (left-pointing) = panel is open, click to collapse.
        // ▶ (right-pointing) = panel is collapsed, click to expand.
        // The "Properties" word stays so the affordance is clear.
        PropertiesToggleButton.Text = _propertiesCollapsed ? "Properties ▶" : "Properties ◀";
    }

    private void OnPropertiesToggleClicked(object? sender, EventArgs e)
    {
        _propertiesCollapsed = !_propertiesCollapsed;
        ApplyOrientationLayout();
    }

    private void OnViewerMessage(object? sender, string json)
    {
        Dispatcher.Dispatch(() =>
        {
            _bridge.OnRawMessageReceived(json);
            // Track when JS has acknowledged ready so the Open button can
            // safely send loadOffline (the bridge queue handles pre-ready
            // sends, so this is just a UX nicety for status messaging).
            if (!_viewerReady && json.Contains("\"ready\"")) _viewerReady = true;
        });
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        try
        {
            var options = new PickOptions
            {
                PickerTitle = "Pick a 3D model",
                // FileTypes intentionally null on iOS for v1: the bundled
                // exported UTIs in Info.plist drive the picker filter at
                // the system level. Post-pick we filter by extension.
            };
            var results = await FilePicker.PickMultipleAsync(options);
            if (results is null) return;

            await OpenFilesAsync(results.Where(r => r is not null)!);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Open failed: {ex.Message}";
        }
    }

    private async Task OpenFilesAsync(IEnumerable<FileResult> results)
    {
        // Multi-file open = pick lead model file (first supported), copy all
        // siblings into the same tab folder so relative refs resolve via the
        // nwdviewer-files:// scheme handler.
        var list = results.ToList();
        if (list.Count == 0) return;

        var lead = list.FirstOrDefault(r => PickedFileImporter.IsModelExtension(r.FileName));
        if (lead is null)
        {
            _vm.StatusText = "No supported 3D file in selection.";
            return;
        }

        var tab = _vm.AddOfflineTab(lead.FileName);
        _bridge.CreateTab(tab.TabId, ViewModels.TabMode.Offline);
        _bridge.SwitchTab(tab.TabId);

        try
        {
            foreach (var r in list)
                await _importer.ImportAsync(r, tab.TabId);

            var url = _store.BuildUrl(tab.TabId, lead.FileName);
            var fmt = PickedFileImporter.FormatFromExtension(lead.FileName);
            _bridge.LoadOffline(tab.TabId, url, fmt);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Import failed: {ex.Message}";
        }
    }

    private void OnNewTabClicked(object? sender, EventArgs e)
    {
        // No-op for v1 — empty tabs aren't useful since the page surface only
        // shows content once a model is loaded. Keeping the button so users
        // discover Open as the primary action; revisit when adding a tabs UX.
        _vm.StatusText = "Tap Open to add a tab with a model.";
    }

    private void OnThemeToggleClicked(object? sender, EventArgs e)
    {
        _theme = _theme == "light" ? "dark" : "light";
        _bridge.SetTheme(_theme);
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is TabViewModel tab)
            _vm.SetActive(tab);
    }

    private void OnCloseTabClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: TabViewModel tab })
        {
            _bridge.CloseTab(tab.TabId);
            _store.Delete(tab.TabId);
            _vm.CloseTab(tab);
        }
    }

    private void OnRootDrop(object? sender, DropEventArgs e)
    {
        // v1.5: iPadOS drag-drop of files needs a UIDropInteraction wired on
        // the iOS-side handler (MAUI's DropGestureRecognizer doesn't expose
        // file URLs from external Files-app drags as IFileResult). The
        // FilePicker path covers all file sources for v1, so this is stubbed.
        _vm.StatusText = "Use Open to load a model — drag-and-drop is on the v1.5 list.";
    }
}
