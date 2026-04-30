# 3D Model Viewer — iPad MAUI Host

A .NET MAUI iPad app that hosts the **NwdViewer.Desktop** `viewer.html` (three.js 0.149 + web-ifc 0.0.44) verbatim, rendering FBX, IFC, glTF/GLB, OBJ, and STL **offline**, plus **NWD/NWC** through the Autodesk Platform Services (APS) cloud-translation pipeline. The Windows WPF shell of the upstream project is **rewritten** here for MAUI + WKWebView + custom URL-scheme handlers. The viewer.html itself and the entire `vendor/` JS folder are **shipped unmodified except for documented patches**, so the desktop and iPad versions stay logically identical.

For build/deploy/signing/Pair-to-Mac concerns, see [CLAUDE-TOOLCHAIN.md](CLAUDE-TOOLCHAIN.md). This doc is about the code.

## Solution structure

```
HelloWorld_IOS.sln
├── HelloWorld_IOS/                # MAUI iPad app project (the shell)
└── NwdViewer.Aps/                 # APS REST client library, copied verbatim from
                                   # ../NWC_NWC_Viewer/NwdViewer.Aps. Pure HTTP, net8.0,
                                   # zero platform-specific APIs. Sync upstream when
                                   # it changes; do not edit locally except for
                                   # error-message improvements (see "APS error surfacing"
                                   # below). Note: `*.aps` in .gitignore matches this
                                   # folder on case-insensitive Windows; an explicit
                                   # re-include rule is in .gitignore — keep it.
```

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

## APS cloud path (v2 — shipped)

NWD/NWC files route through the **Autodesk Platform Services** cloud-translation pipeline (upload → translate → SVF2 viewer), all the way through the JS-side APS Viewer 7.x SDK. The seams kept in v1 made this fully additive — no shell refactor.

**Library:** `NwdViewer.Aps/` is a sibling project, copied verbatim from `../NWC_NWC_Viewer/NwdViewer.Aps`. TFM `net8.0`, single dep `Microsoft.Extensions.Http`, zero platform-specific APIs. **Don't fork divergent changes locally** — sync upstream when it changes. The one local exception is APS error-message surfacing (see "APS error surfacing" below) which we'd want to upstream eventually.

**Files added for v2:**
- `Services/ApsServices.cs` — facade: HttpClient (10-min timeout) + `AuthClient` + `OssClient` + `ModelDerivativeClient`. Disposable. Created per-call by `MainViewModel` and reused across that translation.
- `Views/SettingsPage.xaml(.cs)` — modal `ContentPage` with three Entries (Client ID, Client Secret with `IsPassword=true`, Bucket Key) + Cancel/Save toolbar.
- `ViewModels/SettingsViewModel.cs` — binds the form, validates the Bucket Key against APS rules (`^[a-z0-9_]{3,128}$` — lowercase letters, digits, underscores only; no hyphens, no uppercase), wraps `CredentialStore.Save/LoadAsync`.

**Files modified for v2:**
- `MauiProgram.cs` — DI registration: `Func<ApsCredentials, ApsServices>` factory (singleton), `SettingsViewModel` + `SettingsPage` (transient).
- `MainViewModel.cs` — APS surface fully un-stubbed. `_aps` cached across calls; `InvalidateApsServices()` called by SettingsPage on Save so updated credentials take effect on the next translation. `MainViewModel` is now `IDisposable`.
- `Views/ViewerPage.xaml(.cs)` — added gear (⚙) toolbar button between **Properties** and **Theme**. `OpenFilesAsync` splits picks into APS (.nwd / .nwc) and offline streams; APS files run sequentially because translation jobs are credit-paying server work and APS rate-limits parallel calls. **Auto-prompt** for SettingsPage if the user picks an APS file with no credentials saved.
- `Services/ViewerBridge.cs` — `selection` case calls `MainViewModel.LoadApsPropertiesAsync`; `apsDiag` and `error` cases now real handlers.
- `Platforms/iOS/Info.plist` — added `com.orlandohernandez.nwd` and `.nwc` to `UTExportedTypeDeclarations` and `LSItemContentTypes`.

**Settings + credentials flow:**
- Persistent ⚙ gear button in the toolbar opens SettingsPage anytime.
- If the user picks an `.nwd` / `.nwc` with no credentials, SettingsPage auto-opens. On Save, the import resumes; on Cancel, status bar shows "Cancelled: APS credentials are required to open …" and no tab is created.
- Credentials live in iOS Keychain via `Services/CredentialStore` (already a v1 seam) under target keys `NwdViewer.ApsClientId`, `NwdViewer.ApsClientSecret`, `NwdViewer.ApsBucketKey`.

**Bucket Key rules** (a frequent source of 400 errors):
- Lowercase letters, digits, underscores only — `^[a-z0-9_]{3,128}$`. No hyphens. No uppercase.
- Must be globally unique across **all APS apps in the world**, not just your account. Include something personal-ish.
- `SettingsViewModel.IsValidBucketKey` rejects bad keys at Save time so they don't reach APS.

**APS error surfacing (local patch to NwdViewer.Aps):**
The upstream library uses `EnsureSuccessStatusCode()` which throws an `HttpRequestException` whose message contains *only* the status code (e.g., `"400 (Bad Request)"`) — the response body, which usually contains the actual reason, is dropped. We added a private `EnsureSuccessOrThrowAsync` helper in `OssClient.cs` and inlined the same pattern in `AuthClient.cs` so APS error bodies (e.g., `"Bucket key is invalid"`, `"Token exchange denied. Policy 'ProductAccessRequiresCapacity' has effect: deny"`) surface to the status bar and Debug output. Local-only patch; sync upstream when convenient.

**Known APS errors, in plain English:**
- `400 ... Bucket key is invalid` — bucket key violates the format rule above. Open Settings, fix it, Save.
- `403 ... Token exchange denied. Policy 'ProductAccessRequiresCapacity' has effect: deny` — APS account is out of cloud credits OR the requested format isn't included in the account's entitlements. Especially common on **NWD** even when **NWC** works on the same account: NWD lives in a different entitlement bundle (Construction / BIM 360 / ACC) and needs explicit provisioning in your APS app at https://aps.autodesk.com.
- `404` on `/manifest` shortly after `StartTranslation` — APS hasn't begun indexing yet. The `WaitForTranslationAsync` poll loop tolerates this on the first cycle.
- Translation hangs at `0%` for several minutes on a large NWD — normal; SVF2 translation of complex linked-NWD files is slow.

## Out of scope (still)

- **Image / PDF capture** (`captureImage` JS message). PDFsharp doesn't compile cross-platform; revisit with QuestPDF.
- **Drag-and-drop** from external apps. MAUI's `DropGestureRecognizer` doesn't surface external file drops as `FileResult`; needs a `UIDropInteraction` on the iOS handler. FilePicker covers all sources for now.
- **Portrait properties as a bottom sheet.** Currently the panel just hides in portrait; the landscape side panel works fine via the **Properties ◀/▶** toolbar toggle.
- **iPhone layout polish.** Page reflows but isn't tuned.
- **Cancel running translation.** Upstream WPF doesn't offer it either; not blocking.
- **APS multi-file batch with "continue with remaining?" prompt.** Current behavior is sequential best-effort; per-file failure aborts that file but the next file proceeds.

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
