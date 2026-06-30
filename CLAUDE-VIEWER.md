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
    │   ├── MainViewModel.cs                     # observable Tabs + StatusText/Progress; APS surface live
    │   ├── TabViewModel.cs                      # TabId, Title, FilePath, Properties; APS fields kept
    │   ├── PropertyNode.cs                      # Key/Value/Children — verbatim from upstream
    │   ├── SettingsViewModel.cs                 # APS creds form + VerboseNavLogging toggle (Preferences-backed)
    │   └── DiagnosticsViewModel.cs              # enumerate sessions, share single / export-all-as-zip, delete
    ├── Views/
    │   ├── ViewerPage.xaml(.cs)                 # toolbar / tab strip / WebView / props panel / status
    │   ├── SettingsPage.xaml(.cs)               # APS creds form + Troubleshooting section (diag link, nav-log toggle)
    │   └── DiagnosticsPage.xaml(.cs)            # master/detail browser for ~/Library/logs/*.log + iOS share sheet
    ├── Controls/
    │   └── NwdWebView.cs                        # cross-platform façade for WKWebView host
    ├── Converters/
    │   └── Converters.cs                        # ActiveTabBgConverter, ProgressConverter
    ├── Services/
    │   ├── ViewerBridge.cs                      # PostOrQueue + RawMessageReceived parser (mirrors WPF OnViewerMessage)
    │   ├── TabFileStore.cs                      # tabId → CacheDirectory/tabs/{tabId}/ + path-traversal guard
    │   ├── PickedFileImporter.cs                # FilePicker FileResult → tab folder copy
    │   ├── CredentialStore.cs                   # SecureStorage wrapper for APS creds
    │   ├── SessionLogger.cs                     # per-launch .log file + rolling 50-session retention
    │   ├── Logger.cs                            # static facade: Info/Warn/Error(category, msg) over SessionLogger
    │   └── ApsLogBridge.cs                      # forwards NwdViewer.Aps' IApsLogger calls into the host Logger
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
2. iOS Document Picker opens — integrates with Files app, iCloud Drive, and any signed-in Document Provider extensions (Box, Google Drive). Filtering relies on the **custom UTIs** declared in `Platforms/iOS/Info.plist` under `UTExportedTypeDeclarations` (`com.accoes.{ifc,gltf,glb,obj,fbx,stl,nwd,nwc}`).
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

**Message types implemented:**
- Outbound (host → JS): `createTab`, `switchTab`, `closeTab`, `loadOffline`, `loadAps`, `setTheme`, `captureImage`.
- Inbound (JS → host): `ready`, `loadStart`, `loadProgress`, `loadEnd`, `loaded`, `selectionOffline`, `selection` (APS), `apsDiag`, `imageData`, `error`, `jsError`.
- The `apsDiag` channel doubles as the viewer's structured diagnostic stream — fit math (`fit:`, `fit.frame:`), APS viewer lifecycle (`pre-load`, `post-load`), and navigation events (`nav.*`) all flow through it. The host bridge routes them to `Logger.Info("viewer.js", …)` and the `nav.*` subset is gated on the Settings toggle.

## viewer.html patches

`Resources/Raw/wwwroot/viewer.html` is byte-identical to upstream **except for these changes**, which must be re-applied if you re-sync from upstream:

1. **`chrome.webview` polyfill** (in the early `<script>` block, before `postHost`): defines `window.chrome.webview.postMessage` / `addEventListener` to delegate to `window.HybridWebView` (which the iOS handler injects via a `WKUserScript` at document-start). Lets the rest of `viewer.html` use the WebView2-shaped API unchanged.

2. **OrbitControls touch convention** (after `new OrbitControls(...)`): `controls.touches = { ONE: THREE.TOUCH.PAN, TWO: THREE.TOUCH.DOLLY_ROTATE }` — 1-finger **pan**, 2-finger pinch-zoom + orbit. Matches Autodesk Viewer (NWC/NWD) and Navisworks Freedom on iPad. The earlier 1-finger-rotate mapping traced a radial arc around `controls.target` and didn't match the APS feel. Mouse mappings (left=orbit, right=pan, wheel=zoom) are configured separately and unchanged. Tap-to-select still uses the 4-px pointerdown/up movement threshold.

3. **APS CDN lazy-load**: removed the static `<link>` and `<script>` tags pointing at `developer.api.autodesk.com`; introduced `ensureApsSdk()` that injects them on first APS use. Keeps the offline path's network-free guarantee.

4. **Render-loop canvas clear** (in the `animate()` function): when there's no active tab or the active tab has no `loadedObject`, render an empty scene with the theme background color so a closed tab doesn't leave its last frame painted on the WebGL canvas.

5. **Selection no longer recenters camera**: removed the `focusOnObject(st, hit.object)` call at the end of the viewport-click handler. Selection only updates the properties panel; double-click still frames the picked object.

6. **Renderer keeps CSS in sync with the drawing buffer** (`resizeRenderer()`): `renderer.setSize(w, h)` — default `updateStyle=true`. Earlier code passed `false`, which left the canvas's CSS dimensions at the attribute size (= cssPx × devicePixelRatio = 2× the container on retina). With `overflow: hidden` on `#viewport`, that hid the bottom-right ¾ of the rendered scene and made a centered Fit appear in the corner. The two `setSize(..., false)` calls inside `captureImage` are intentional — they temporarily upscale the buffer for a higher-res screenshot and immediately restore.

7. **Fit bounding box: two-pass outlier rejection + iterative centroid refinement** (`getVisibleBoundingBox()`):
   - **Stage A** iteratively recomputes centroid + median distance and drops meshes more than 3× the median away. Up to 3 passes converge the cluster center; catches survey markers / origin references thousands of meters from the building.
   - **Stage B** rejects any surviving mesh whose bbox diagonal is more than 20× the median diagonal. Catches large "site" / "ground" meshes that sit at the cluster center but span far more space than any single building element (these would otherwise blow up the union bbox and force Fit to zoom way out).
   - Logs a `fit: meshes=N → inliers=M · box.size=… · box.center=…` diag line on every Fit so we can correlate misframings with what the filter saw.

8. **`frameCameraToBox` explicit `lookAt` + full diag dump**: explicit `camera.lookAt(center)` before `controls.update()` (OrbitControls' update calls lookAt too, but the explicit call covers the one-frame window before the next animate tick — matches what `setView()` already does). Emits a `fit.frame:` line per call with canvas size, aspect, dir, posBefore/posAfter, targetBefore/targetAfter.

9. **Navigation event logging**: button-press handlers emit `apsDiag` messages (`nav.view: …`, `nav.bake: …`, `nav.theme: …`, `nav.tree-panel: …`). Gated host-side by the **Verbose navigation logging** Settings toggle.

10. **WebGL context-loss recovery**: iOS reclaims the GPU when the app is backgrounded; the canvas previously came back blank ("Open a model to begin") on resume and never recovered. Now both the shared three.js `renderer.domElement` **and** the APS Viewer's own canvas have `webglcontextlost`/`webglcontextrestored` listeners. On loss: `preventDefault()` (required so the browser will re-fire `restored`), set a `contextLost` flag, and the `animate()` loop early-returns to stop drawing into a dead context. On restore: re-apply renderer state (`setPixelRatio`, `localClippingEnabled`, `resizeRenderer`) and replay the active model — offline tabs remember their source via `st.lastUrl`/`st.lastFormat`, APS tabs re-run `loadApsTab(tabId, urn, token)`. A host-posted `checkHealth` message (sent on app resume, via `App.Resumed` → `ViewerBridge.CheckHealth`) is the backstop: if the context is lost but the browser didn't auto-fire `restored`, it calls `renderer.forceContextRestore()` and nudges the APS viewer with `resize()` + `invalidate()`. All transitions emit a `webglEvent` message logged under the `viewer.webgl` category. A full web-content-process termination (memory pressure) is caught separately by a `WKNavigationDelegate.WebViewWebContentProcessDidTerminate` in `NwdWebViewHandler` → reload viewer.html + `ViewerBridge.PrepareForReload()` (resets the `ready` handshake) + a one-shot re-hydrate that replays the open tabs (`ViewerPage.RehydrateTabs`).

Two minor CSS additions for touch (in the `body` rule): `-webkit-touch-callout: none; -webkit-user-select: none;` to suppress iOS magnifier/selection overlays on canvas long-presses.

## Session logging + Diagnostics

A persistent per-app-launch log file landed alongside the APS HTTP instrumentation. The goal: every diagnostic signal — APS calls, viewer events, errors, navigation actions — lives in a file the user can read in-app, share via the iOS share sheet, or export as a zip of the last 50 sessions. Replaces "attach VS and watch Debug Output" for any post-deploy bug.

**Format:** `HH:mm:ss.fff [LEVEL] [category.subcategory] message`
- Levels: `INFO ` / `WARN ` / `ERROR` (padded to 5 chars).
- Categories: dotted hierarchy so `grep '\[aps\.'` matches an entire subsystem.
- Per-launch file at `{FileSystem.AppDataDirectory}/logs/yyyy-MM-dd_HH-mm-ss.log`. Rolling 50-file retention pruned at startup.
- Secrets (`access_token`, `client_secret`, `Bearer …`) regex-redacted before write.

**Category map:**

| Category | What lands here |
|---|---|
| `app.start` | Launch header: build version, device, app data dir, log dir |
| `app.lifecycle` | `OnStart` / `OnSleep` / `OnResume` |
| `app.settings` | Non-secret diff when credentials are saved (id prefix, bucket, secretChanged) |
| `file.pick` | Picker results: name, size, extension |
| `file.import` | Per-tab import outcome including dep count |
| `tab.open` / `tab.close` | Tab lifecycle with `reason=user` / `reason=failed-teardown` |
| `aps.auth` | Token cache hit/miss, scope label, HTTP status + elapsed ms |
| `aps.bucket` | EnsureBucket: `created` / `already exists (409)` outcomes |
| `aps.upload` | Per-step signed-S3 init / PUT / complete with bytes + throughput |
| `aps.translate` | POST `/job` with URN preview, status, elapsed |
| `aps.manifest` | Polling cycles + status transitions, final poll count |
| `aps.metadata` / `aps.properties` | Metadata fetch + object-properties fetch |
| `aps.error` | Full 4xx/5xx response body (always written, even after the modal closes) |
| `viewer.js` | JS-side diag (`apsDiag:`, `fit:`, `fit.frame:`, `nav.*`) and JS errors |
| `viewer.webgl` | WebGL context lifecycle: `context lost` / `restored` / `reinit` for the three.js and APS canvases (see viewer.html patch #10) |
| `net.error` | Client-side network failures classified apart from APS errors — a timeout (`ApsHttp` TimeoutException) or transport failure before/independent of an APS reply (see `ViewerPage.ClassifyFailure`) |
| `bridge.error` | C# bridge parse failures |

**Plumbing:**

```
JS viewer.html ── postHost ──▶ NwdScriptMessageHandler ──▶ ViewerBridge.OnRawMessageReceived
                                                                     │
                                                                     ▼
NwdViewer.Aps clients ── ApsLog.Info(cat, msg) ──▶ IApsLogger (ApsLogBridge) ──▶ Logger.Info
                                                                                       │
                                                                                       ▼
                                                                                SessionLogger
                                                                                   │
                                                                                   ▼
                                                                          .log file (AppDataDirectory)
                                                                                   │
                                                                                   ▼
                                                                      DiagnosticsPage (shareable)
```

- `NwdViewer.Aps/ApsLog.cs` defines `IApsLogger` + a static `ApsLog` facade. The library never references MAUI — this is the seam. `MauiProgram.CreateMauiApp()` registers `ApsLogBridge` as the sink at startup.
- `Logger.cs` (iOS app, static) sits in front of the singleton `SessionLogger` so call sites stay terse: `Logger.Info("aps.upload", "…")`. Tees to `Debug.WriteLine` too so it shows up in VS Output when attached.
- **Verbose nav logging toggle** in Settings (default OFF). When OFF, `apsDiag` messages whose body starts with `nav.` are dropped at the bridge. Diagnostic dumps (`fit:`, `fit.frame:`, APS HTTP, app lifecycle, file/tab/settings) are always logged.
- **DiagnosticsPage** (`Views/DiagnosticsPage.xaml(.cs)` + `ViewModels/DiagnosticsViewModel.cs`) is a master/detail UI: session list on the left, read-only `Editor` (UITextView-backed) on the right for tap-to-select text. Toolbar buttons: **Share** (iOS share sheet via `Share.Default.RequestAsync`), **Export all** (zips the whole `logs/` dir to `CacheDirectory` and shares), **Delete** (per file, active session protected), **Clear all (keep active)**. Auto-selects the newest session on open. Reachable from SettingsPage → Troubleshooting.

## NwdViewer.Aps local patches (beyond error-message surfacing)

A few changes to the shared library that should eventually be upstreamed:

- **`AuthClient` cache is keyed by scope** (was: single `_cached` field). The previous implementation returned the most-recently-fetched token regardless of which scope was requested — once `GetViewerTokenAsync` ran (after a successful translate), every subsequent `GetInternalTokenAsync` call silently reused the viewer-scoped token. Replaced with `Dictionary<string, (TokenResponse, DateTimeOffset)>`. Also instruments cache hit/miss with TTL via `ApsLog.Info("aps.auth", …)`.
- **`ModelDerivativeClient.StartTranslationAsync(urn, bool forceRetranslate = false, ...)`** — `x-ads-force` is now opt-in. Was unconditionally true, which re-ran APS's policy gate on every retry and burned credits even when a manifest already existed.
- **HTTP instrumentation**: `AuthClient`, `OssClient`, `ModelDerivativeClient.GetMetadataAsync` / `StartTranslationAsync` / `WaitForTranslationAsync` all wrap their `SendAsync` calls with `Stopwatch` + `ApsLog.Info(…)` for status + elapsed ms. `WaitForTranslationAsync` only logs the first poll + each status transition (not every poll) to keep the log readable for long translates.
- **`NwdViewer.Aps/ApsLog.cs`** — new file. `IApsLogger` interface + static `ApsLog.SetSink/Info/Warn/Error`. Library stays UI-agnostic.

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
- Singletons: `SessionLogger`, `MainViewModel`, `CredentialStore`, `TabFileStore`. Plus `Func<ApsCredentials, ApsServices>` factory (per-call construction).
- Transient: `PickedFileImporter`, `ViewerPage`, `SettingsViewModel`, `SettingsPage`, `DiagnosticsViewModel`, `DiagnosticsPage`.
- `AddHttpClient()` registered.
- `ConfigureMauiHandlers` binds `NwdWebView` to `NwdWebViewHandler` only on iOS (`#if IOS`).
- **After `builder.Build()`**: `Logger.Init(...)` wires the static facade to the resolved `SessionLogger`, and `ApsLog.SetSink(new ApsLogBridge())` connects the library-side log facade to the same sink.

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
- `Views/ViewerPage.xaml(.cs)` — added gear (⚙) toolbar button between **Properties** and **Theme**. `OpenFilesAsync` splits picks into three buckets: APS files (`.nwd` / `.nwc`), offline **model files** (`.fbx` / `.obj` / `.gltf` / `.glb` / `.stl` / `.ifc`), and **dependency files** (`.mtl` / `.bin` / textures). Each picked model file opens its own tab — multi-pick gives multi-tab. Dependency files are copied into every offline-model tab so relative refs (`.mtl` textures, `.bin` buffers) resolve via the `nwdviewer-files://` handler. APS files run sequentially because translation jobs are credit-paying server work and APS rate-limits parallel calls. **Auto-prompt** for SettingsPage if the user picks an APS file with no credentials saved. **Failed translate** tears down just that tab (`reason=failed-teardown`) and continues with the next file in the batch.
- `Services/ViewerBridge.cs` — `selection` case calls `MainViewModel.LoadApsPropertiesAsync`; `apsDiag` and `error` cases now real handlers.
- `Platforms/iOS/Info.plist` — added `com.accoes.nwd` and `.nwc` to `UTExportedTypeDeclarations` and `LSItemContentTypes` (originally registered as `com.orlandohernandez.*`; renamed when the app moved to the production `com.accoes.nwd3dviewer` Bundle ID on 2026-05-07).

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
- `403 ... Token exchange denied. Policy 'ProductAccessRequiresCapacity' has effect: deny` — account-level NWD entitlement gate under the new APS business model. The policy name itself is **not publicly documented** anywhere on Autodesk's docs (verified via web search 2026-05-11) — it's an internal name. **Resolved 2026-05-13:** fresh-filename NWD translation now succeeds for this account (proven via session log: POST `/job` returns 201, manifest poll 1 = `pending`, transitions to `success` after real wall-clock work — see [session log 2026-05-14 19:55-19:59](G:/My%20Drive/!_ios_Testing/APS_Stuff/) capturing `ORH_01.nwd` translating in 56.5s). Autodesk didn't publicly announce a fix; the gate quietly opened sometime between 2026-05-12 and 2026-05-13. Background context preserved below in case the gate ever reappears:
  - **Dec 8, 2025:** APS launched a two-tier (Free + Paid) business model. Model Derivative became one of four "rated" APIs with monthly Free-tier caps. ([APS Business Model Evolution](https://aps.autodesk.com/blog/aps-business-model-evolution))
  - **May 2026:** APS added subscription-tied API access — *"If you have a qualifying Autodesk product subscription, you'll receive monthly API usage included with your subscription."* Exact subscription→API mapping is not in the public docs. ([APS continues to evolve: Data Model APIs included with subscriptions, plus flexible ways to scale](https://aps.autodesk.com/blog/aps-continues-evolve-data-model-apis-included-subscriptions-plus-flexible-ways-scale))
  - **The `ViewerPage.ClassifyApsError` hint text and message-matching are still in place** so if the gate returns we surface the right action (check `manage.autodesk.com → Reporting → Resource and API usage`, contact APS Support).
  - **Distinguishing cached vs. fresh translation in the log:** APS caches manifests per URN (deterministic from `bucketKey/filename`), so re-uploading the same file returns the existing manifest instantly. To verify a fresh URN is actually translating end-to-end, look for `poll 1 ... status=pending` followed by transitions through `inprogress` to `success`. Poll-1 `status=success` means cache hit.
- `404` on `/manifest` shortly after `StartTranslation` — APS hasn't begun indexing yet. The `WaitForTranslationAsync` poll loop tolerates this on the first cycle.
- Translation hangs at `0%` for several minutes on a large NWD — normal; SVF2 translation of complex linked-NWD files is slow.

## Out of scope (still)

- **Image / PDF capture** (`captureImage` JS message). PDFsharp doesn't compile cross-platform; revisit with QuestPDF.
- **Drag-and-drop** from external apps. MAUI's `DropGestureRecognizer` doesn't surface external file drops as `FileResult`; needs a `UIDropInteraction` on the iOS handler. FilePicker covers all sources for now.
- **Portrait properties as a bottom sheet.** Currently the panel just hides in portrait; the landscape side panel works fine via the **Properties ◀/▶** toolbar toggle.
- **iPhone layout polish.** Page reflows but isn't tuned.
- **Cancel running translation.** Upstream WPF doesn't offer it either; not blocking.
- **APS multi-file batch with "continue with remaining?" prompt.** Current behavior is sequential best-effort; per-file failure aborts that file but the next file proceeds.
- **Off-device pre-translate pipeline** and **native SceneKit/RealityKit render fork** — the two large initiatives from the 2026-06-24 large-model investigation. Designed but not built; see [docs/ARCHITECTURE-SPIKES.md](docs/ARCHITECTURE-SPIKES.md).

## Deploying

Normal flow: F5 in VS with the iPad selected as the run target. See [CLAUDE-TOOLCHAIN.md](CLAUDE-TOOLCHAIN.md) for the Pair-to-Mac and signing details.

When VS's run dropdown shows generic "Remote Device" instead of "Orlando's iPad" (which makes F5 build but silently no-op the install): **unplug and replug the iPad's USB cable to the Mac, then close + reopen VS**. The broker re-enumerates the device on cable reconnect. mlaunch reports `<InterfaceType>Wifi</InterfaceType>` in the broken state (visible via SSH to the Mac); should be `Usb` after replug.

Fallback when even that doesn't work: `pwsh ./deploy-ipad.ps1` (in the solution root) does locate-app + install + launch via SSH to the Mac and `xcrun devicectl`. SSH key auth is set up at `~/.ssh/id_ed25519_mac` (Windows) → `~/.ssh/authorized_keys` (Mac, tagged `claude-deploy@<host>`). The script is purely a fallback; F5 is the normal path once the dropdown enumerates.

## Diagnostics

- **In-app session log** (primary diagnostic surface for post-deploy bugs): Settings → Troubleshooting → **Open diagnostics (session logs)**. Lists the last 50 session files; tap to view, **Share** sends one through the iOS share sheet, **Export all** zips everything to a single `.zip`. See "Session logging + Diagnostics" above for the format and category map. **The full APS 4xx/5xx response body is logged here before the modal closes** — so even if the user dismissed the error dialog, the actionable detail is recoverable.
- **WebView console:** Safari on the Mac → Develop → Orlando's iPad → 3D Model Viewer. Full DevTools: console, Sources, Network. Web Inspector is enabled on iOS 16.4+ via the KVO `inspectable` flag in `NwdWebViewHandler`.
- **VS Output (Debug)**: `Logger.Info/Warn/Error` also tee to `Debug.WriteLine`, so every line in the in-app session log also appears in VS's Output → Debug pane while attached. Useful for live tailing during a connected session.
- **JS uncaught errors:** routed through `jsError` messages → `Logger.Error("viewer.js", …)` and surface in the status bar.
- **Mac build agent logs:** `~/Library/Logs/Xamarin.Messaging-17.14.271/` on the Mac. The IDB log records `--listdev`/`--listsim` invocations; absence of `--listdev` while the dropdown shows generic "Remote Device" is the broken-enumeration signature.

## Useful upstream pointers

- Upstream `CLAUDE.md`: `C:\Visual Studio Files\NWC_NWC_Viewer\CLAUDE.md` — message protocol tables, gotchas (WebView2 ES module quirks, APS canvas sizing, click-vs-drag threshold, etc.), and the file-to-feature map.
- Upstream `viewer.html`: same path, `NwdViewer.Desktop\viewer.html` — single source of truth; sync our copy after any upstream change.
- Upstream WPF shell: `MainWindow.xaml.cs` — the patterns we transliterated (OnViewerMessage, the queue handshake, OnWebResourceRequested's path-traversal guard).
