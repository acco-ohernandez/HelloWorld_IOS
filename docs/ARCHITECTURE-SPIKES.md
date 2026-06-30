# Architecture Spikes — NwdViewer

Design notes for the two large initiatives that came out of the 2026-06-24 large-model
investigation (a 476 MB federated NWD: 5G upload timeout, 44-min on-device translation, laggy
navigation, model lost after backgrounding). These are **not implemented** — the targeted fixes
in branch `Dev_03_LargeModel_Upload_Lifecycle_UX` (upload timeout, resumable upload, WebGL
recovery, error/progress UX) address the acute pain. Each spike below is a candidate for its own
branch once prioritized.

Related code already in place that these build on:
- `NwdViewer.Aps/OssClient.cs` — multipart upload + `ToUrn` (URN = URL-safe base64 of `objectId`).
- `NwdViewer.Aps/ModelDerivativeClient.cs` — `StartTranslationAsync` (`force=false`), `WaitForTranslationAsync`.
- `HelloWorld_IOS/ViewModels/MainViewModel.cs` `TranslateAsync` — the upload→translate→poll→view orchestration.

---

## Spike 1 — Off-device pre-translate pipeline (P1.4)  ★ recommended

### Problem
Every first open of a model pays the full upload **and** translation cost on the iPad: the
reference model uploaded in ~53 s on Wi-Fi then translated for **2635 s (44 min)** before it
could be viewed. APS caches the manifest per URN, so a *re-open* of the identical file is instant
— but the **first** person to open any model eats the whole wait, on a device and network far
weaker than a workstation. The object key is also the bare filename (`Path.GetFileName`,
`MainViewModel.cs`), so two different models that share a name collide, and an *edited* file with
the same name silently reuses the stale manifest under `force=false`.

### Proposal
Move ingestion + translation **off the iPad** to a one-time pre-processing step, and have the app
open an already-translated URN.

```
Workstation / server (has the NWD, fast disk + network)
   ingest → hash file → upload (object key = hash) → StartTranslation(force=false) → wait
        → record { fileHash, fileName, urn, modelGuid, translatedAt } in a shared index
                                   │
                                   ▼
iPad app on Open:  hash the picked file (or read a sidecar) → look up urn by hash
   → if found: load the existing URN immediately (skip upload + translate entirely)
   → if not found: fall back to today's on-device upload+translate path
```

### Components
- **Pre-translate tool.** A small .NET console app reusing `NwdViewer.Aps` (same `AuthClient` /
  `OssClient` / `ModelDerivativeClient`), or a step bolted onto the existing `NwdViewer.Desktop`
  WPF app (it already references the library). Input: a folder of NWD/NWC. Output: uploaded +
  translated + an index entry per file.
- **Object key = content hash** (e.g. SHA-256, or a cheaper xxHash of size+head+tail for speed).
  This is the stale-manifest fix: identical content → same key → APS cache hit; edited content →
  new key → fresh translation. Deferred out of `Dev_03` deliberately because it only pays off
  with this index.
- **Shared index.** Simplest first cut: a JSON map (`fileHash → {urn, modelGuid}`) in the same
  APS bucket (or the team's Google Drive `!_ios_Testing` area already used for logs). A small REST
  endpoint is the better long-term home but isn't required for a v1.
- **App lookup.** On Open of an `.nwd`/`.nwc`, hash the local file, fetch the index, and if a URN
  exists, call `_bridge.LoadAps(...)` directly with a freshly minted viewer token — skipping
  `TranslateAsync` entirely. Keep the current on-device path as the fallback for un-indexed files.

### Feasibility / risk
- **High value, moderate effort.** Reuses the whole existing APS client; no viewer changes.
- Hashing a 476 MB file on-device is a few seconds — acceptable, and can be cached per tab file.
- Requires someone/something to run the pre-translate step (CI job, a "publish to iPad" button in
  the desktop app, or a watched folder). Operational, not technical, is the main cost.
- Cleanly subsumes the P1.4 + content-hash-key work into one coherent feature.

---

## Spike 2 — Native render fork: glTF/USDZ → SceneKit/RealityKit (P2.4)

### Problem
A 476 MB federated model is too heavy for responsive navigation inside WKWebView on an iPad Air,
and the WebView is also where the lifecycle fragility lives (GPU context loss on background — the
`Dev_03` WebGL-recovery work mitigates but doesn't eliminate it). A cached URN (Spike 1) removes
the *wait* but not the *lag* or the lifecycle risk.

### Proposal
Render natively on iOS instead of in three.js/WKWebView, for the formats where it's feasible:
- Convert/export to a native-friendly format (glTF → SceneKit `SCNScene`, or USDZ → RealityKit).
- Host an `SCNView` / `ARView` via a MAUI custom handler (same pattern as `NwdWebView` /
  `NwdWebViewHandler` — a cross-platform `View` façade with an iOS handler in `Platforms/iOS/`).
- Native rendering survives app backgrounding far better (no WebGL context to lose) and gives
  Metal-backed navigation performance.

### Feasibility / risk
- **Durable but large.** This is a second rendering stack, not a tweak.
- **Loses the shared `viewer.html`** that keeps the desktop and iPad versions logically identical,
  and loses the APS property-tree / metadata integration that the Autodesk Viewer gives for free.
- **NWD/NWC don't go native** — there's no direct SceneKit path for SVF2. Federated Navisworks
  models would have to stay on the APS/WKWebView path regardless, so this is at best a *split*:
  native for offline FBX/OBJ/glTF/STL, WebView for APS. That split is itself a maintenance cost.
- **USDZ/glTF conversion pipeline** needed for anything not already in those formats.

### Recommendation
Scope a **narrow spike first**: one offline format (glTF or USDZ) through a SceneKit/RealityKit
handler, measured against the current three.js path for nav performance and background-survival.
Only expand if the win is clearly worth running two renderers. Keep APS/WKWebView for federated
NWD either way. Lower priority than Spike 1 — the pre-translate pipeline removes the most painful
symptom (the 44-min wait) for far less effort.
