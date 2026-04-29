# 3D Model Viewer — iPad MAUI Host

A .NET MAUI iPad app that hosts the **NwdViewer.Desktop** `viewer.html` (three.js 0.149 + web-ifc 0.0.44) verbatim, rendering FBX, IFC, glTF/GLB, OBJ, and STL files offline. The Windows WPF shell of the upstream project is **rewritten** here for MAUI + WKWebView + custom URL-scheme handlers. The viewer.html itself and the entire `vendor/` JS folder are **shipped unmodified except for four documented patches**, so the desktop and iPad versions stay logically identical.

For build/deploy/signing/Pair-to-Mac concerns, see [CLAUDE-TOOLCHAIN.md](CLAUDE-TOOLCHAIN.md). This doc is about the code.

## Upstream

The viewer logic lives at `C:\Visual Studio Files\NWC_NWC_Viewer\NwdViewer.Desktop\` (Windows WPF parent project). When upstream changes, mirror the changes here:

- `NwdViewer.Desktop/viewer.html` → `HelloWorld_IOS/Resources/Raw/wwwroot/viewer.html` (re-apply the patches in "viewer.html patches" below)
- `NwdViewer.Desktop/vendor/**` → `HelloWorld_IOS/Resources/Raw/wwwroot/vendor/**` (verbatim)

The upstream `CLAUDE.md` documents the message protocol, gotchas, vendored deps, and architecture — read that first when touching `viewer.html`.

## Solution layout

```
HelloWorld_IOS.sln
└── HelloWorld_IOS/                              # the only project (was a Hello World template)
    ├── HelloWorld_IOS.csproj                   # signing, MauiAsset wwwroot/**, custom UTIs
    ├── App.xaml(.cs)                            # resolves ViewerPage from DI directly
    ├── AppShell.xaml(.cs)                       # bypassed; kept so MAUI defaults aren't disturbed
    ├── MauiProgram.cs                           # DI registrations + iOS handler binding
    ├── GlobalXmlns.cs                           # XAML namespace defaults (Views/ViewModels/Controls)
    ├── ViewModels/
    │   ├── MainViewModel.cs                     # observable Tabs + StatusText/Progress; v2-APS seams
    │   ├── TabViewModel.cs                      # TabId, Title, FilePath, Properties; APS fields kept
    │   └── PropertyNode.cs                      # Key/Value/Children — verbatim from upstream
    ├── Views/
    │   ├── ViewerPage.xaml(.cs)                 # toolbar / tab strip / WebView / props panel / status
    ├── Controls/
    │   └── NwdWebView.cs                        # cross-platform façade for WKWebView host
    ├── Converters/
    │   └── Converters.cs                        # ActiveTabBgConverter, ProgressConverter
    ├── Services/
    │   ├── ViewerBridge.cs                      # PostOrQueue + RawMessageReceived parser (mirrors WPF OnViewerMessage)
    │   ├── TabFileStore.cs                      # tabId → CacheDirectory/tabs/{tabId}/ + path-traversal guard
    │   ├── PickedFileImporter.cs                # FilePicker FileResult → tab folder copy
    │   └── CredentialStore.cs                   # SecureStorage wrapper, v2-APS seam (unused in v1)
    ├── Platforms/iOS/
    │   ├── NwdWebViewHandler.cs                 # creates WKWebView with config below
    │   ├── AppSchemeHandler.cs                  # serves nwdviewer-app:// from app bundle wwwroot/
    │   ├── NwdViewerSchemeHandler.cs            # serves nwdviewer-files://{tabId}/{filename}
    │   ├── NwdScriptMessageHandler.cs           # JS->host pipeline (window.webkit.messageHandlers.nwdHost)
    │   └── Info.plist                           # bundle ID, signing, custom UTIs, document types
    └── Resources/Raw/wwwroot/                   # MauiAsset, served at app://; ~7.4 MB
        ├── viewer.html                          # patched (see below)
        └── vendor/                              # three.js + web-ifc + web-ifc-three (verbatim)
```

A helper script at the solution root: `deploy-ipad.ps1` — see "Deploying" below.

## How a load happens (data flow)

1. User taps **Open** in the toolbar. `ViewerPage.OnOpenClicked` calls `FilePicker.PickMultipleAsync`.
2. iOS Document Picker opens — integrates with Files app, iCloud Drive, and any signed-in Document Provider extensions (Box, Google Drive). Filtering relies on the **custom UTIs** declared in `Platforms/iOS/Info.plist` under `UTExportedTypeDeclarations` (`com.orlandohernandez.{ifc,gltf,glb,obj,fbx,stl}`).
3. `PickedFileImporter.ImportAsync` opens a security-scoped stream from each `FileResult` and copies the bytes into `Path.Combine(FileSystem.CacheDirectory, "tabs", tabId.ToString(), filename)`.
4. `MainViewModel.AddOfflineTab` creates a `TabViewModel` and sets it active.
5. `ViewerBridge.LoadOffline(tabId, url, format)` posts a `loadOffline` message to JS. URL is `nwdviewer-files://{tabId}/{filename}`.
6. JS-side `loadOfflineFile` calls the appropriate three.js loader with that URL.
7. WKWebView routes the URL through `NwdViewerSchemeHandler.StartUrlSchemeTask`, which validates the path (no `..`, no rooted paths, must stay inside `tabs/{tabId}/`) and serves the bytes with the right MIME type and `Access-Control-Allow-Origin: *`.
8. three.js renders the model. Selection clicks post `selectionOffline` back through the bridge; `ViewerBridge.OnRawMessageReceived` populates `tab.Properties`, which the right-side panel binds to.

## The two URL schemes

| Scheme | Handler | What it serves |
|---|---|---|
| `nwdviewer-app://` | [AppSchemeHandler](HelloWorld_IOS/Platforms/iOS/AppSchemeHandler.cs) | Static files from the app bundle (viewer.html, vendor/**). Path is interpreted as relative to `wwwroot/` inside the bundle. WebView entry URL is `nwdviewer-app:///viewer.html`. |
| `nwdviewer-files://` | [NwdViewerSchemeHandler](HelloWorld_IOS/Platforms/iOS/NwdViewerSchemeHandler.cs) | Per-tab picked files from `FileSystem.CacheDirectory/tabs/{tabId}/`. URL host = tabId, path = filename. Includes traversal guard. |

Custom schemes (not `https://`) are required because WKWebView only accepts `setURLSchemeHandler` for non-built-in schemes. Both handlers attach a `Access-Control-Allow-Origin: *` header so cross-origin fetches between the schemes work.

## viewer.html ↔ host bridge

The bridge protocol matches NwdViewer.Desktop verbatim — same JSON shape, same message types. The upstream `CLAUDE.md` "WPF ↔ JS bridge" tables are authoritative.

**Transport on iOS:**
- Host → JS: `WKWebView.EvaluateJavaScript("window.dispatchEvent(new CustomEvent('HybridWebViewMessageReceived', {detail: <jsonStringLiteral>}))")`. The shim in `viewer.html` listens for that event and feeds it to the existing `chrome.webview.addEventListener('message', ...)` handler.
- JS → host: `window.webkit.messageHandlers.nwdHost.postMessage(json)`. `NwdScriptMessageHandler.DidReceiveScriptMessage` routes to `NwdWebView.RaiseMessageReceived`, which `ViewerPage` forwards to `ViewerBridge.OnRawMessageReceived`.

**Pending-message queue:** `ViewerBridge` queues outbound messages until JS posts `{ type: "ready" }`, then flushes — same handshake as the WPF `_pendingMessages` queue (`MainWindow.xaml.cs` lines 152-156 in upstream).

**Message types implemented vs. v2 seams:**
- v1 active: `createTab`, `switchTab`, `closeTab`, `loadOffline`, `setTheme` (outbound); `ready`, `loadStart`, `loadProgress`, `loadEnd`, `loaded`, `selectionOffline`, `jsError` (inbound).
- v1 routed-but-stubbed (logged via `Debug.WriteLine`): `selection`, `apsDiag`, `imageData`, `error` (inbound). Outbound `loadAps` and `captureImage` have helper methods that aren't called yet. Adding APS in v2 = un-comment these call sites; no protocol changes needed.

## viewer.html patches

`Resources/Raw/wwwroot/viewer.html` is byte-identical to upstream **except for these changes**, which must be re-applied if you re-sync from upstream:

1. **`chrome.webview` polyfill** (in the early `<script>` block, before `postHost`): defines `window.chrome.webview.postMessage` / `addEventListener` to delegate to `window.HybridWebView` (which the iOS handler injects via a `WKUserScript` at document-start). Lets the rest of `viewer.html` use the WebView2-shaped API unchanged.

2. **OrbitControls touch convention** (after `new OrbitControls(...)` near line 283-ish): `controls.touches = { ONE: THREE.TOUCH.ROTATE, TWO: THREE.TOUCH.DOLLY_PAN }` — 1-finger orbit, 2-finger pinch-zoom + pan. Tap-to-select uses the existing 4-px pointerdown/up movement threshold.

3. **APS CDN lazy-load**: removed the static `<link>` and `<script>` tags pointing at `developer.api.autodesk.com`; introduced `ensureApsSdk()` that injects them on first APS use. v1 never invokes `loadAps`, so the network is never hit. Makes the offline guarantee unconditional.

4. **Render-loop canvas clear** (in the `animate()` function): when there's no active tab or the active tab has no `loadedObject`, render an empty scene with the theme background color so a closed tab doesn't leave its last frame painted on the WebGL canvas.

5. **Selection no longer recenters camera**: removed the `focusOnObject(st, hit.object)` call at the end of the viewport-click handler. Selection only updates the properties panel; double-click still frames the picked object.

Two minor CSS additions for touch (in the `body` rule): `-webkit-touch-callout: none; -webkit-user-select: none;` to suppress iOS magnifier/selection overlays on canvas long-presses.

## MAUI custom WebView control

`Controls/NwdWebView.cs` is a thin `View` subclass with three surface elements: `Source` BindableProperty, `MessageReceived` event, and `SendMessage(string)` method. The actual platform implementation is `Platforms/iOS/NwdWebViewHandler.cs`, which:

1. Builds a `WKWebViewConfiguration` with **both** scheme handlers and the `nwdHost` script message handler attached (must be done **before** the WKWebView is constructed; `setURLSchemeHandler` cannot be added later).
2. Adds a `WKUserScript` at `AtDocumentStart` that defines `window.HybridWebView` (the shim target).
3. Constructs the `WKWebView` with that config and sets `webView.ScrollView.Bounces/ScrollEnabled = false` so iOS scroll bounce doesn't fight three.js's pointer events.
4. On iOS 16.4+ enables Web Inspector via `setValueForKey "inspectable"` (KVO) so Safari → Develop → Orlando's iPad lists the WebContent process for `console.log` / Sources / Network debugging.
5. Wires `Sender` (host→JS) and routes the script handler back through `VirtualView.RaiseMessageReceived`.

Why a custom control and not MAUI `WebView` or HybridWebView: WebView2/HybridWebView don't expose enough of the `WKWebViewConfiguration` to register additional `WKURLSchemeHandler`s, and we need two of them. Owning the WKWebView creation is cleaner than reflecting around the framework.

## File picker integration

`FilePicker.PickMultipleAsync` on iOS wraps `UIDocumentPickerViewController`. Filtering is driven by:
- `Platforms/iOS/Info.plist`'s `UTExportedTypeDeclarations` (custom UTIs for ifc, gltf, glb, obj, fbx, stl, all conforming to `public.data`).
- `CFBundleDocumentTypes` exposing those UTIs as document types we can open.

Box and Google Drive integration is automatic via their iOS Document Provider extensions — the system picker shows them as "Locations" alongside iCloud Drive and On My iPad. No per-provider SDK needed; the picker hands us security-scoped URLs, and `FileResult.OpenReadAsync` handles the scope under the hood when we copy bytes into the tab folder.

If a file's UTI doesn't match (e.g., a Provider extension only exposes `public.item`), it'll show greyed out. Not a v1 issue with the formats we care about; if it becomes one, add `public.data` / `public.item` as a fallback to `PickOptions.FileTypes`.

## DI registrations

In `MauiProgram.cs`:
- Singletons: `MainViewModel`, `CredentialStore`, `TabFileStore`.
- Transient: `PickedFileImporter`, `ViewerPage`.
- `AddHttpClient()` is registered even though v1 doesn't make HTTP calls — that's the v2-APS seam so the upstream `NwdViewer.Aps` clients can be dropped in unchanged.
- `ConfigureMauiHandlers` binds `NwdWebView` to `NwdWebViewHandler` only on iOS (`#if IOS`).

## v2 APS readiness (deliberate seams kept in v1)

- `TabViewModel` keeps `Mode`, `Urn`, `Token`, `ApsModelGuid` even though they're unused.
- `MainViewModel` has `AddApsTab` / `TranslateAsync` / `LoadApsPropertiesAsync` as `NotImplementedException`-throwing stubs and a `// v2-APS-port-notes` comment block at the top.
- `Services/CredentialStore.cs` is a `SecureStorage`-backed wrapper with the same `(GetAsync, SaveAsync, DeleteAsync)` shape as the WPF `CredentialStore`. Keys: `NwdViewer.ApsClientId`, `NwdViewer.ApsClientSecret`, `NwdViewer.ApsBucketKey`.
- `ViewerBridge` outbound switch reserves `loadAps` and `captureImage` helper methods. Inbound parser routes `selection`, `apsDiag`, `imageData`, `error` (logs `Debug.WriteLine` for now).
- `viewer.html` ships its full APS branch (the JS APS Viewer initialization / `loadApsTab` are still present); `ensureApsSdk()` is the only gate.

To turn APS on in v2: copy `NwdViewer.Aps/` into the solution as a class library, swap the throws in `MainViewModel` for real `ApsServices` calls, build a `SettingsPage` that writes to `CredentialStore`. No protocol or shell refactor needed.

## Out of scope for v1

- Drag-and-drop from external apps onto the page root (MAUI `DropGestureRecognizer` doesn't surface external file drops as `FileResult`; needs a `UIDropInteraction` on the iOS handler — v1.5 task). FilePicker covers all sources, so this isn't blocking.
- Portrait properties as a bottom sheet (currently the panel just hides in portrait; landscape side panel works via the **Properties ◀/▶** toolbar toggle).
- PDF export, PNG save-as. PDFsharp doesn't compile cross-platform; these come back via QuestPDF (or skipped) when APS lands.
- iPhone layout polish. The page reflows but isn't tuned.

## Deploying

Normal flow: F5 in VS with the iPad selected as the run target. See [CLAUDE-TOOLCHAIN.md](CLAUDE-TOOLCHAIN.md) for the Pair-to-Mac and signing details.

When VS's run dropdown shows generic "Remote Device" instead of "Orlando's iPad" (which makes F5 build but silently no-op the install): **unplug and replug the iPad's USB cable to the Mac, then close + reopen VS**. The broker re-enumerates the device on cable reconnect. mlaunch reports `<InterfaceType>Wifi</InterfaceType>` in the broken state (visible via SSH to the Mac); should be `Usb` after replug.

Fallback when even that doesn't work: `pwsh ./deploy-ipad.ps1` (in the solution root) does locate-app + install + launch via SSH to the Mac and `xcrun devicectl`. SSH key auth is set up at `~/.ssh/id_ed25519_mac` (Windows) → `~/.ssh/authorized_keys` (Mac, tagged `claude-deploy@<host>`). The script is purely a fallback; F5 is the normal path once the dropdown enumerates.

## Diagnostics

- **WebView console:** Safari on the Mac → Develop → Orlando's iPad → 3D Model Viewer. Full DevTools: console, Sources, Network. Web Inspector is enabled on iOS 16.4+ via the KVO `inspectable` flag in `NwdWebViewHandler`.
- **Bridge errors:** `Debug.WriteLine` output appears in VS's Output → Debug pane while attached.
- **JS uncaught errors:** routed through `jsError` messages and surface in the status bar.
- **Mac build agent logs:** `~/Library/Logs/Xamarin.Messaging-17.14.271/` on the Mac. The IDB log records `--listdev`/`--listsim` invocations; absence of `--listdev` while the dropdown shows generic "Remote Device" is the broken-enumeration signature.

## Useful upstream pointers

- Upstream `CLAUDE.md`: `C:\Visual Studio Files\NWC_NWC_Viewer\CLAUDE.md` — message protocol tables, gotchas (WebView2 ES module quirks, APS canvas sizing, click-vs-drag threshold, etc.), and the file-to-feature map.
- Upstream `viewer.html`: same path, `NwdViewer.Desktop\viewer.html` — single source of truth; sync our copy after any upstream change.
- Upstream WPF shell: `MainWindow.xaml.cs` — the patterns we transliterated (OnViewerMessage, the queue handshake, OnWebResourceRequested's path-traversal guard).
