# 3D Model Viewer for iPad — Project Notes

**Last updated:** May 7, 2026
**Status:** v1.0.0 (build 1) live in TestFlight Internal Testing, installed on test iPads

This is the high-level project overview — the "what + why + where we are" doc. For build/deploy mechanics see [CLAUDE-TOOLCHAIN.md](CLAUDE-TOOLCHAIN.md). For app architecture and feature details see [CLAUDE-VIEWER.md](CLAUDE-VIEWER.md).

## What this app is

A 3D Model Viewer for iPad. Opens common BIM/CAD model files (`.fbx`, `.obj`, `.gltf`, `.glb`, `.stl`, `.ifc`, plus Navisworks `.nwd` / `.nwc` via cloud translation), renders them in WebGL, lets the user inspect properties and navigate. iPad-only; Custom App-style internal distribution intended (specifics TBD — see "Phase 3 decision pending" below).

It's a port/companion to the Windows WPF app **NwdViewer.Desktop**. Both share a common C# library (`NwdViewer.Aps`) for Autodesk Platform Services API calls.

## Why .NET MAUI (and not Swift in Xcode)

Two reasons:
1. **Code sharing** — `NwdViewer.Aps` is C#. Reusing it on iPad meant the iOS app also had to be C#. MAUI is the Microsoft-supported way to do that on iOS.
2. **Optional Android future** — if we ever ship this on Android, MAUI gets us ~80% there. Swift on iOS would be 0%.

The trade-off is a more complex toolchain (Windows Visual Studio + Pair-to-Mac + Mac build agent + iPad), all documented in `CLAUDE-TOOLCHAIN.md`.

## Tech stack

- **.NET MAUI 10** (`net10.0-ios` target framework, iPad only, iOS 15+)
- **WKWebView** with custom URL scheme handlers — hosts the rendering UI
- **three.js 0.149 + web-ifc 0.0.44** (vendored offline) — does the actual 3D rendering in JavaScript
- **Autodesk Platform Services (APS)** — cloud translation for Navisworks files; auth + upload + translate + view
- **iOS Keychain** (via MAUI `SecureStorage`) — stores APS credentials per-device
- **GitHub** — source control, branch `Dev_01_Release_Testing` for distribution work

## Build pipeline

```
Windows VS  →  Pair-to-Mac (SSH)  →  Mac (Xcode + .NET SDK)  →  Mac AOT compile + sign
  →  Output: HelloWorld_IOS.app  →  Install on iPad via USB (Debug)
                                 OR  Package as .ipa  →  Transporter  →  App Store Connect
```

**Mac build host:** Intel Mac, IP `192.168.1.139`, user `orlandohernandez`. SSH key auth set up for scripted access.

**Build times:** First device build ~8 min (full AOT). Incremental ~30-90 sec.

## Project timeline

### Phase 1 — Hello World pipeline (Apr 27, 2026)

Goal: prove Windows VS → Mac → iPad works end-to-end with a vanilla MAUI Hello World app. Took 6+ hours, mostly fighting free-tier Apple ID signing limitations (7-day cert expiry, VS's Apple Accounts dialog needs paid-only API keys, etc.).

Workaround used: created a throwaway native Xcode project (`~/Desktop/Bootstrap` on the Mac) with Bundle ID `com.orlandohernandez.Bootstrap` to bootstrap the cert/profile pair, then reused them from VS via manual provisioning. Knowledge captured in `CLAUDE-TOOLCHAIN.md`.

### Phase 2 — Build the actual viewer (Apr 28 – May 6, 2026)

Port the WPF 3D viewer to MAUI iPad. Key technical wins:
- **Reused `viewer.html` + `vendor/` JS folder verbatim** from the desktop app — no rewrite of the rendering code
- **Custom WKURLSchemeHandler** (`AppSchemeHandler.cs`, `NwdViewerSchemeHandler.cs`) replaces the WebView2 file-loading path the Windows app used
- **JavaScript ↔ C# bridge** (`ViewerBridge.cs` + `NwdScriptMessageHandler.cs`) routes messages between viewer.html and the MAUI shell
- **Properties panel, tabs, file picker, theme switcher, robust Fit-to-View** — all native MAUI UI
- **Custom UTIs** in Info.plist register the app as a handler for `.fbx`, `.gltf`, `.glb`, `.obj`, `.stl`, `.ifc`, `.nwd`, `.nwc` — appears in iOS Files app "Open With" menus

### Phase 2b — APS cloud integration (May, 2026)

Added the cloud-translation path for Navisworks files (which can't render natively in three.js). When the user opens a `.nwd` / `.nwc`:
1. Settings page collects APS credentials (Client ID + Secret + Bucket Key) → stored in iOS Keychain
2. App authenticates via 2-legged OAuth, uploads the file to APS Object Storage, kicks off a translation job
3. Polls until manifest is "success", then loads in the same viewer with an APS-issued viewer token
4. Properties panel shows hierarchy + metadata pulled from APS Model Derivative API

Same pipeline as the WPF desktop app, sharing the `NwdViewer.Aps` library.

### Phase 3 — Production identity + first App Store upload (May 7, 2026)

Today. Migrated off free-tier signing onto paid Apple Developer Program (Individual enrollment, $99/yr).

| Item | Value |
|---|---|
| Apple ID | `orlando2503@gmail.com` |
| Team ID | `8RJY4X4Y3H` (preserved from prior Personal Team — the old free-tier ID stays valid after upgrade) |
| Bundle ID | `com.accoes.nwd3dviewer` |
| App Store Connect record | `NWD Viewer v1` (SKU `nwd3dviewer-2026`) |
| App ID description | "This is a viewer for 3D models exported from Navis" |
| First build | `1.0.0 (1)`, ASC ID `6767396471`, uploaded May 7 8:47 PM via Transporter |

**End-to-end verified** — the production-signed build installed on the iPad via TestFlight Internal Testing. Closes the loop on the entire pipeline.

## Phase 3 decision still pending

Original plan was **Custom App via Apple Business Manager → Microsoft Intune** for company-iPad distribution. **That requires Organization enrollment, not Individual.** Need to choose:

| Option | Best for | Lift | Notes |
|---|---|---|---|
| **Ad Hoc + Intune LOB** | Up to 100 company iPads | Half day | Closest equivalent to Custom App on Individual. Ad Hoc-signed `.ipa`, deployed via Intune as Line-of-Business app. Manual UDID collection per device. |
| **TestFlight External** | 5–10K testers, OK with quarterly redeploy | None (already signed for App Store) | Each build expires 90 days after upload. Apple Beta App Review needed (~24h). Has "this is a beta" framing. |
| **Public App Store** | Anyone | Medium (App Review + marketing fields) | Permanent listing. Anyone with App Store can install. |
| **Upgrade to Org enrollment** | Many iPads + want full Intune Custom App | High (~1-2 weeks) | Get D-U-N-S → Apple Org verification → re-issue certs + transfer Bundle ID. Unlocks original plan. |

**Current lean:** Ad Hoc + Intune LOB is the best fit on Individual *right now*. If the iPad fleet grows past 50–60 devices, start the D-U-N-S paperwork in parallel for an Org upgrade — that takes the longest.

## Costs

| Item | Cost | Frequency |
|---|---|---|
| Apple Developer Program (Individual) | $99 | Annually |
| Intel Mac build host | (already owned) | One-time |
| GitHub repo | $0 | (private repo, free tier OK) |
| Autodesk APS | Per-credit | Pay-as-you-go (~5–15 credits per NWC translation, ~30–100+ per NWD; account-level entitlement also matters) |
| Microsoft Intune | (existing company tenant) | Already in place |
| Org upgrade (if pursued later) | $99 | Annually, separate from Individual |

## Hard-won knowledge / gotchas

Captured in `CLAUDE-TOOLCHAIN.md`. The four most painful ones:

1. **macOS auto-cleans `~/Library/MobileDevice/Provisioning Profiles/`.** The traditional `cp` recipe doesn't survive on modern macOS. **Fix:** symlink that path to Xcode's auth-managed dir. One-time setup, self-updates.
2. **`<ArchiveOnBuild>true</ArchiveOnBuild>` only fires under `dotnet publish`, not `dotnet build`.** VS's "Build Solution" produces a `.app` but no `.ipa`. **Fix:** package manually (zip the `.app` inside a `Payload/` folder) or use `deploy-appstore.ps1`.
3. **Custom App via ABM requires Organization enrollment.** Individual cannot do Custom App. Affects Phase 3 distribution choice.
4. **Don't F5 a Release build.** App Store provisioning profiles can't be installed on devices directly (`devicectl` returns `0xe800801f`). Always **Build** (Ctrl+Shift+B), then upload the `.ipa`.

## Repo layout

```
HelloWorld_IOS/
├── HelloWorld_IOS.sln                     ← Visual Studio solution
├── PROJECT-OVERVIEW.md                    ← This file (the high-level story)
├── CLAUDE-TOOLCHAIN.md                    ← Build/deploy reference (the painful knowledge)
├── CLAUDE-VIEWER.md                       ← Project structure + features
├── deploy-ipad.ps1                        ← SSH-based Debug deploy to iPad
├── deploy-appstore.ps1                    ← SSH-based Release .ipa packaging
├── HelloWorld_IOS/                        ← MAUI app project
│   ├── HelloWorld_IOS.csproj             ← Bundle ID, signing, version live here
│   ├── Views/, ViewModels/, Services/    ← MAUI UI + bridge code
│   ├── Platforms/iOS/                    ← Custom WKWebView handlers
│   └── Resources/Raw/wwwroot/            ← viewer.html + vendor/ (3D rendering)
└── NwdViewer.Aps/                         ← Shared APS client library (also used by NwdViewer.Desktop)
```

## What to do when picking this back up

1. Read `CLAUDE-TOOLCHAIN.md` for build/deploy reference.
2. Read `CLAUDE-VIEWER.md` for what the app does and how it's structured.
3. Check current state: `git log --oneline -20` and `git branch -a` on the `Dev_01_Release_Testing` branch.
4. The first thing to do post-pickup is probably the Phase 3 distribution decision (above). After that, branding swap (replace placeholder `.NET` icon).

## Key external accounts and where they live

| Account | URL | Purpose |
|---|---|---|
| Apple Developer | developer.apple.com | Bundle IDs, certs, provisioning profiles |
| App Store Connect | appstoreconnect.apple.com | App listing, TestFlight, build uploads |
| GitHub | github.com/acco-ohernandez/HelloWorld_IOS | Source control |
| Autodesk APS | aps.autodesk.com | NWD/NWC cloud translation API |
| Microsoft Intune (company tenant) | endpoint.microsoft.com | Future iPad distribution |
