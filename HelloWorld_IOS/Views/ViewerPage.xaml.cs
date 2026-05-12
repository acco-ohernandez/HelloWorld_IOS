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
    private readonly IServiceProvider _services;
    private bool _viewerReady;
    private string _theme = "light";
    private bool _propertiesCollapsed;       // user-toggled state; sticks across rotations
    private bool _orientationInitialized;    // guards the per-orientation default seed

    public ViewerPage(MainViewModel vm, TabFileStore store, PickedFileImporter importer, IServiceProvider services)
    {
        InitializeComponent();
        _vm = vm;
        _store = store;
        _importer = importer;
        _services = services;
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
        var list = results.ToList();
        if (list.Count == 0) return;

        // Split picks into APS (nwd/nwc) and offline. APS is processed
        // sequentially because each translation is a credit-paying server
        // job and parallelizing hits APS rate limits anyway. Offline can
        // be handled with the existing single-tab batch (lead + siblings).
        var apsFiles    = list.Where(r => IsApsExtension(r.FileName)).ToList();
        var offlineFiles = list.Where(r => PickedFileImporter.IsModelExtension(r.FileName)).ToList();

        if (apsFiles.Count == 0 && offlineFiles.Count == 0)
        {
            _vm.StatusText = "No supported 3D file in selection.";
            return;
        }

        if (offlineFiles.Count > 0)
            await OpenOfflineBatchAsync(list, offlineFiles[0]);

        foreach (var r in apsFiles)
            await OpenApsFileAsync(r);
    }

    private static bool IsApsExtension(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext is ".nwd" or ".nwc";
    }

    private async Task OpenOfflineBatchAsync(List<FileResult> all, FileResult lead)
    {
        var tab = _vm.AddOfflineTab(lead.FileName);
        _bridge.CreateTab(tab.TabId, ViewModels.TabMode.Offline);
        _bridge.SwitchTab(tab.TabId);

        try
        {
            // Copy lead + sibling files (textures, .mtl, .bin, etc.) into the
            // same tab folder so relative refs resolve via the nwdviewer-files://
            // scheme handler. Skip APS files in the batch — those route on
            // their own.
            foreach (var r in all)
            {
                if (IsApsExtension(r.FileName)) continue;
                await _importer.ImportAsync(r, tab.TabId);
            }

            var url = _store.BuildUrl(tab.TabId, lead.FileName);
            var fmt = PickedFileImporter.FormatFromExtension(lead.FileName);
            _bridge.LoadOffline(tab.TabId, url, fmt);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Import failed: {ex.Message}";
        }
    }

    private async Task OpenApsFileAsync(FileResult result)
    {
        // Auto-prompt Settings if creds are missing. The user can also reach
        // Settings any time from the toolbar gear button.
        if (!_vm.HasCredentials)
        {
            var page = _services.GetRequiredService<Views.SettingsPage>();
            await Navigation.PushModalAsync(page);
            // After the Settings sheet dismisses, re-check; if still missing,
            // the user cancelled — abort cleanly without creating a tab.
            if (!_vm.HasCredentials)
            {
                _vm.StatusText = $"Cancelled: APS credentials are required to open {result.FileName}.";
                return;
            }
        }

        // Create the tab early so the user sees the title and progress in the
        // status bar while uploading + translating.
        var tab = _vm.AddApsTab(result.FileName, urn: string.Empty, token: string.Empty);
        _bridge.CreateTab(tab.TabId, ViewModels.TabMode.Aps);
        _bridge.SwitchTab(tab.TabId);

        try
        {
            // Copy local file into the tab cache so we have a stable path
            // for the upload step (also matches the offline file layout).
            await _importer.ImportAsync(result, tab.TabId);
            var localPath = Path.Combine(_store.GetTabDirectory(tab.TabId), result.FileName);

            var (urn, token, modelGuid) = await _vm.TranslateAsync(localPath);
            tab.Urn = urn;
            tab.Token = token;
            tab.ApsModelGuid = modelGuid;

            // APS Viewer SDK in JS prepends 'urn:' itself, so send the stripped form.
            _bridge.LoadAps(tab.TabId, NwdViewer.Aps.OssClient.WithoutPrefix(urn), token);
        }
        catch (Exception ex)
        {
            // Translation failed AFTER we eagerly created+switched the tab to
            // give the user immediate status feedback. Tear that phantom tab
            // back down now — otherwise the user is left with: (1) an empty
            // APS tab in the strip they have to manually close, and (2) the
            // 'aps-active' CSS class still on, showing the APS viewer area
            // (which thanks to the dispose() fix in viewer.html is now empty,
            // but was previously revealing the stale model from the prior tab).
            // Mirrors the cleanup sequence in OnCloseTabClicked.
            try
            {
                _bridge.CloseTab(tab.TabId);
                _store.Delete(tab.TabId);
                _vm.CloseTab(tab);
            }
            catch { /* tab teardown is best-effort; don't mask the original error */ }

            await ShowApsErrorAsync(ex.Message, result.FileName);
        }
    }

    /// <summary>
    /// APS errors carry their actionable detail inside the response body
    /// (see the EnsureSuccessOrThrowAsync helper in NwdViewer.Aps/OssClient.cs +
    /// AuthClient.cs). The status bar truncates them; pop a modal so the user
    /// can read the full message and translate the policy name into a fix.
    /// </summary>
    private async Task ShowApsErrorAsync(string fullMessage, string filename)
    {
        // Persist the full body before the modal eats it — Diagnostics page reads this back.
        Logger.Write("aps-error", $"file={filename}\n{fullMessage}");

        // Status bar gets a short summary. Full message goes to the alert.
        _vm.StatusText = $"APS error opening {filename} (tap for details).";

        var hint = ClassifyApsError(fullMessage);
        var alertBody = string.IsNullOrWhiteSpace(hint)
            ? fullMessage
            : $"{hint}\n\n— Full APS response —\n{fullMessage}";
        try { await DisplayAlertAsync("APS error", alertBody, "OK"); }
        catch { /* if no MainPage yet (shouldn't happen here), fall back to status bar */ }
    }

    private static string? ClassifyApsError(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        // Specific 403 patterns we've documented in CLAUDE-VIEWER.md.

        // 'ProductAccessRequiresCapacity' is an undocumented internal APS policy name
        // (it does not appear anywhere in Autodesk's public docs — verified via web
        // search 2026-05-11). Best inference from the authoritative APS docs we DO
        // have:
        //   - APS Business Model Evolution (Dec 8, 2025) introduced a two-tier
        //     Free + Paid model. Model Derivative became a "rated" API with
        //     monthly free-tier caps.
        //     https://aps.autodesk.com/blog/aps-business-model-evolution
        //   - May 2026 update added subscription-tied API access: "If you have a
        //     qualifying Autodesk product subscription, you'll receive monthly API
        //     usage included." Exact mapping of subscription -> API not public.
        //     https://aps.autodesk.com/blog/aps-continues-evolve-data-model-apis-included-subscriptions-plus-flexible-ways-scale
        // Empirically verified for this app:
        //   - NWC translates successfully (same auth/app/token)
        //   - NWD denied with this policy
        //   - Purchasing 300 Flex tokens did not change the response
        //   - /modelderivative/v2/designdata/formats lists 'nwd' as supported
        // Conclusion: NWD requires a qualifying Autodesk product subscription
        // under the new model that this account does not have. Autodesk has not
        // publicly documented which subscription qualifies; only APS Support can
        // confirm for a given account.
        if (body.Contains("ProductAccessRequiresCapacity", StringComparison.OrdinalIgnoreCase))
            return "APS rejected NWD translation for this account. NWC files translate " +
                   "successfully on the same account — so this is an account-level " +
                   "entitlement gate, not credits, auth, or a code bug.\n\n" +
                   "APS launched a new two-tier pricing model on Dec 8, 2025, and in May " +
                   "2026 began tying API access to specific Autodesk product subscriptions. " +
                   "NWD translation appears to require a \"qualifying Autodesk product " +
                   "subscription\" under the new model — exact requirements aren't " +
                   "publicly documented.\n\n" +
                   "To resolve:\n" +
                   "1. Check entitlements at https://manage.autodesk.com → Reporting → " +
                   "Resource and API usage.\n" +
                   "2. Contact Autodesk APS Support (https://aps.autodesk.com/support) " +
                   "for the definitive answer on which subscription unlocks NWD.";

        // 'Token exchange denied' is the generic APS denial wrapper. If the more-specific
        // ProductAccessRequiresCapacity match above didn't fire, the policy name is
        // something we haven't seen yet — fall back to a more general explanation.
        if (body.Contains("Token exchange access denied", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Token exchange denied", StringComparison.OrdinalIgnoreCase))
            return "APS denied the translation request. This is usually an account-level " +
                   "entitlement issue rather than a credits issue — APS's two-tier model " +
                   "(launched Dec 2025) and subscription-tied API access (May 2026) gate " +
                   "some operations behind a qualifying Autodesk product subscription. " +
                   "Check https://manage.autodesk.com → Reporting → Resource and API " +
                   "usage to see what's entitled on this account, then contact APS Support " +
                   "(https://aps.autodesk.com/support) with the full error below if you " +
                   "can't identify the missing entitlement.";

        if (body.Contains("Bucket key", StringComparison.OrdinalIgnoreCase) && body.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            return "The bucket key is invalid. Open Settings (gear icon) and ensure the Bucket Key is lowercase letters / digits / underscores only — no hyphens, no uppercase. 3-128 characters. Must be globally unique across all APS apps.";
        if (body.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
            return "Client ID or Secret rejected by APS. Open Settings and re-enter. Make sure they match the values shown at https://aps.autodesk.com → your app.";
        return null;
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

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        var page = _services.GetRequiredService<Views.SettingsPage>();
        await Navigation.PushModalAsync(page);
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
