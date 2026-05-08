# HelloWorld_IOS — Build & Deploy Toolchain Reference

The hard-won institutional knowledge about how the **Windows-VS → Mac → iPad** pipeline for this project actually works: pairing, signing, provisioning, common breakages and their fixes. **This doc is the toolchain reference; for what the project does and how the code is structured, see [CLAUDE-VIEWER.md](CLAUDE-VIEWER.md).**

The project began on 2026-04-27 as a Hello World pipeline test (which is where the toolchain knowledge below was captured). It became a real .NET MAUI iPad app — a 3D Model Viewer host — starting 2026-04-28. **2026-05-07: paid Apple Developer Program (Individual enrollment, $99/yr, same orlando2503@gmail.com Apple ID, Team ID `8RJY4X4Y3H` preserved)** activated; the free-tier Personal Team identity (`com.orlandohernandez.Bootstrap` + 7-day cert) has been retired. The free-tier sections below are kept as historical record but no longer apply to the live toolchain.

## Quick orientation

- **Solution:** `HelloWorld_IOS.sln`
- **Project:** `HelloWorld_IOS/HelloWorld_IOS.csproj`
- **TFMs:** `net10.0-ios` (iPad target) + `net10.0-windows10.0.19041.0` (local Windows test target)
- **Bundle ID:** `com.accoes.nwd3dviewer` — registered at developer.apple.com under Team ID `8RJY4X4Y3H`. Permanent once first uploaded to App Store Connect.
- **Display Name (home-screen icon):** `3D Model Viewer`
- **Display Name (App Store Connect listing):** `NWD Viewer v1` (editable until first build submitted for review)
- **Custom UTIs renamed:** `com.accoes.{ifc,gltf,glb,obj,fbx,stl,nwd,nwc}` (Info.plist)

## Environment (verified working)

### Windows (dev machine)
- Visual Studio 2022 Professional 17.14.29
- .NET SDK 10.0.201
- Workloads installed: `android`, `ios` (26.2.10233), `maccatalyst`, `maui-ios`, `maui-windows`
- **Important:** `maui-ios` had to be installed separately via elevated PowerShell (`dotnet workload install maui-ios`) — VS Installer's MAUI workload doesn't register the sub-workloads with the dotnet CLI in 17.14.x.

### Mac build host (Pair-to-Mac target)
- **IP:** `192.168.1.139`
- **Username:** `orlandohernandez`
- **Architecture:** Intel `x86_64` — NOT Apple Silicon. Use `osx-x64` URLs, never `osx-arm64`.
- **macOS:** 26.2
- **Xcode:** 26.4.1 (App Store)
- **iOS Simulator runtime:** 26.4 installed
- **.NET SDK:** 10.0.201 (osx-x64) at `/usr/local/share/dotnet/`
- **MAUI workloads** (10.0.20/10.0.100) — installed via `sudo dotnet workload install maui` after PATH was set
- **Mono framework** — installed by VS Pair-to-Mac on first connect (required by VS↔Mac protocol, not by MAUI builds themselves)
- **Xamarin.iOS 16.4.0.23** — installed manually from `https://aka.ms/xvs/pkg/macios/16.4.0.23` (legacy gate for VS Pair-to-Mac dialog; not actually used by MAUI builds)
- Xcode `xcode-select` points at `/Applications/Xcode.app/Contents/Developer`, license accepted

### Test iPad
- iPad mini (6th gen), iOS 26.0
- Apple ID `orlando2503@gmail.com` signed into Xcode — **paid Apple Developer Program, Individual enrollment** (Team ID `8RJY4X4Y3H`, legal name "Orlando Hernandez"). 1-year cert validity, no more 7-day expiry dance.
- Developer Mode enabled (Settings → Privacy & Security → Developer Mode)
- Trusted via Settings → General → VPN & Device Management → Apple ID → Trust

## How a build flows

1. F5 in VS triggers MSBuild on Windows.
2. MSBuild's `SayHello` task SSH-connects to `192.168.1.139:22` and starts a build agent over a TCP port the Mac picks dynamically.
3. The Mac's build agent invokes `dotnet build` on the project files (synced via the agent), runs `illink` (trimmer), AOT-compiles to arm64, and bundles the .app.
4. Codesign on the Mac signs the .app using the cert from the Mac keychain + the provisioning profile.
5. The .app gets pushed to the iPad via the Mac's USB connection (the iPad must be USB-connected to the Mac, not Windows).
6. App installs and launches.

**First device build: ~8 minutes** (Intel Mac AOT compile is slow). Incremental builds: 30-90 sec.

## Critical csproj configuration

Located in `HelloWorld_IOS/HelloWorld_IOS.csproj`. Key sections and **why each one is there**:

### TFMs trimmed to iOS + Windows only
```xml
<TargetFrameworks>net10.0-ios</TargetFrameworks>
<TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('windows'))">$(TargetFrameworks);net10.0-windows10.0.19041.0</TargetFrameworks>
```
Removed Android/MacCatalyst/Tizen. Why: avoids needing `maui-android` etc. workloads we don't use. Adding them back would require installing those workloads on Windows.

### Bundle ID + Title (production identity, paid program)
```xml
<ApplicationId>com.accoes.nwd3dviewer</ApplicationId>
<ApplicationTitle>3D Model Viewer</ApplicationTitle>
```
At top-level PropertyGroup. Both Debug and Release inherit (versions still differ per config). Registered at developer.apple.com under Team ID `8RJY4X4Y3H` (Individual enrollment). The previous free-tier `com.orlandohernandez.Bootstrap` workaround is retired — see the "Free-tier provisioning" section below for historical context.

### Xcode version bypass
```xml
<ValidateXcodeVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">false</ValidateXcodeVersion>
```
Why: .NET for iOS workload 26.2.10233 expects Xcode 26.3, but Mac has 26.4.1. The check is a strict major.minor match (not minimum). For Hello World this is fine. Remove this if the app starts using iOS 26.4-only APIs that need real bindings.

### Manual provisioning + explicit cert/profile (current)
```xml
<PropertyGroup Condition="'$(TargetFramework)' == 'net10.0-ios' and '$(Configuration)' == 'Debug'">
    <CodesignKey>Apple Development: orlando2503@gmail.com (97JK54NP2N)</CodesignKey>
    <CodesignProvision>iOS Team Provisioning Profile: com.accoes.nwd3dviewer</CodesignProvision>
</PropertyGroup>
<PropertyGroup Condition="'$(TargetFramework)'=='net10.0-ios'">
    <ProvisioningType>manual</ProvisioningType>
</PropertyGroup>

<PropertyGroup Condition="'$(TargetFramework)' == 'net10.0-ios' and '$(Configuration)' == 'Release'">
    <ProvisioningType>manual</ProvisioningType>
    <CodesignKey>Apple Distribution: Orlando Hernandez (8RJY4X4Y3H)</CodesignKey>
    <CodesignProvision>NwdViewer App Store</CodesignProvision>
    <ArchiveOnBuild>true</ArchiveOnBuild>
    <RuntimeIdentifier>ios-arm64</RuntimeIdentifier>
</PropertyGroup>
```
Why manual mode persists post-paid: VS Automatic Provisioning could now work (paid accounts can generate App Store Connect API keys for Tools → Options → Xamarin → Apple Accounts), but manual is precise, predictable, and the existing pipeline already works this way. No reason to switch.

The Debug cert hash `(97JK54NP2N)` was the Personal Team display ID. After paid upgrade, run `security find-identity -v -p codesigning` on the Mac and update the parenthesized hash if Apple reissued the cert.

The Release Distribution cert + `NwdViewer App Store` profile must be created at developer.apple.com → Certificates / Profiles before the first Release build will sign.

### Info.plist requires CFBundleIdentifier
`Platforms/iOS/Info.plist` has:
```xml
<key>CFBundleIdentifier</key>
<string>com.accoes.nwd3dviewer</string>
```
Why: VS's Provisioning Profile dropdown filters profiles by bundle ID read from Info.plist (not csproj). Without this, the dropdown shows "No matching profiles found" even when the profile exists on Mac. (csproj `<ApplicationId>` *also* sets this at build time, but having both keeps VS's design-time tooling happy.)

## Free-tier Apple ID provisioning (HISTORICAL — superseded by paid program 2026-05-07)

> The section below documents the original free-tier setup that ran from 2026-04-27 through 2026-05-07. Kept for context. The 7-day cert dance no longer applies; with paid Individual enrollment the Development cert is valid 1 year and the App ID was registered explicitly at developer.apple.com.

This is what made the original setup take 6+ hours. **All of this disappears with a paid Apple Developer Program account — and as of 2026-05-07, it has.**

### Why VS can't bootstrap a free-tier cert
- VS Pair-to-Mac's Apple Accounts dialog wants App Store Connect API keys → paid only.
- VS can't directly create the initial development cert + provisioning profile on a free Personal Team.
- **Workaround used:** create a throwaway native iOS project in Xcode on the Mac, deploy it once to the iPad. Xcode auto-creates the cert in the Mac keychain and a `.mobileprovision` file. Then VS reuses them.

### Bootstrap project (deletable, but keep around)
A throwaway Xcode project named `Bootstrap` was created at `~/Desktop/Bootstrap` on the Mac. It uses bundle ID `com.orlandohernandez.Bootstrap` with Personal Team signing. **Don't delete it** — its provisioning profile is what HelloWorld_IOS reuses. If you delete it, you'd need to bootstrap again.

### Cert / profile / team-prefix mismatch is normal on free tier
Looking at `security find-identity -v -p codesigning`:
```
"Apple Development: orlando2503@gmail.com (97JK54NP2N)"
```
But the provisioning profile's TeamIdentifier is `8RJY4X4Y3H`. **These both work together.** The `(97JK54NP2N)` in the cert CN is a display ID, not the actual Team ID. The cert's *real* Team ID is in its `OU=` (organizational unit) field — `OU=8RJY4X4Y3H` — which matches the profile. Apple just confuses the issue with multiple identifiers.

### Profile location — Xcode 26 moved them
- Xcode 26 stores profiles at `~/Library/Developer/Xcode/UserData/Provisioning Profiles/`.
- VS Pair-to-Mac agent looks at the legacy `~/Library/MobileDevice/Provisioning Profiles/`.
- We bridged by copying:
  ```bash
  mkdir -p ~/Library/MobileDevice/Provisioning\ Profiles
  cp ~/Library/Developer/Xcode/UserData/Provisioning\ Profiles/*.mobileprovision ~/Library/MobileDevice/Provisioning\ Profiles/
  ```
- **Re-run this copy if Xcode generates a new profile** (e.g., after revoking a cert, after 7-day cert expiry).

### 7-day cert expiry
Free-tier development certs expire **7 days** after creation. After expiry, the app on the iPad stops launching. To recover:
1. Re-deploy the Bootstrap project from Xcode (regenerates cert + profile).
2. Copy the new profile to the legacy location (see above).
3. F5 from VS again.

If the cert has changed, `security find-identity` will show a new entry. Update the `<CodesignKey>` value in csproj if the SHA or display name changed.

## "Automatic Provisioning is enabled" stale Error List entry
After everything was configured, VS's Error List kept showing `Automatic Provisioning is enabled but no account was selected`. This is a **design-time IntelliSense check** that doesn't refresh after Manual mode is set in Properties. **It does not block actual builds** — Rebuild and F5 work despite it. To clear: right-click project → Reload Project. Or just ignore.

## Deploying & launching on the iPad

1. Run target dropdown → **iOS Remote Devices** → Orlando's iPad.
2. F5.
3. Build runs on Mac (~8 min first time, ~30-90 sec incremental).
4. App installs on iPad. **First launch fails** with "Untrusted Developer."
5. iPad: Settings → General → VPN & Device Management → tap `orlando2503@gmail.com` → **Trust**.
6. Tap app icon on iPad home screen — launches.

## Paid Apple Developer Program — migration (complete 2026-05-07)

Most of the free-tier friction is gone:
- 1-year cert validity (not 7 days).
- Bundle IDs registered explicitly at developer.apple.com.
- TestFlight beta + Ad Hoc + App Store distribution unlocked.
- Custom App via Apple Business Manager: **NOT available on Individual enrollment** — would require Org enrollment ($99/yr separate, requires D-U-N-S). See the "Phase 3 distribution" discussion at the end of this doc.

What was done (all confirmed end-to-end on 2026-05-07):
1. ✅ Purchased Apple Developer Program — Individual, under `orlando2503@gmail.com` (Team ID `8RJY4X4Y3H` preserved from the prior Personal Team).
2. ✅ Registered new App ID at developer.apple.com → Identifiers → `com.accoes.nwd3dviewer` (Explicit, no special capabilities).
3. ✅ Created App Store Connect record (`NWD Viewer v1`, SKU `nwd3dviewer-2026`).
4. ✅ Updated csproj + Info.plist + deploy-ipad.ps1 + custom UTIs (`com.accoes.{ifc,gltf,glb,obj,fbx,stl,nwd,nwc}`) to use the production identity.
5. ✅ Confirmed Xcode → Settings → Accounts shows Role = **Admin**, Certificates+Identifiers+Profiles green check, 2 provisioned devices. Apple preserved the existing Personal Team Development cert through the upgrade — `Apple Development: orlando2503@gmail.com (97JK54NP2N)` is still valid (now 1-year expiry instead of 7-day), so csproj's CodesignKey didn't need a hash update.
6. ✅ Generated Development provisioning profile for the new Bundle ID via Xcode auto-management (used `~/Desktop/Bootstrap` as the host project — set its Bundle ID to `com.accoes.nwd3dviewer`, Team to Orlando Hernandez, "Automatically manage signing" ON, Xcode generated `iOS Team Provisioning Profile: com.accoes.nwd3dviewer` and registered both iPads' UDIDs).
7. ✅ Symlinked legacy MobileDevice path to Xcode's auth-managed profile dir (see "Profile location bridge" below). One-time setup; survives auto-cleanup.
8. ✅ Debug F5 to iPad working again, app appears as **3D Model Viewer**.
9. ✅ Generated Apple Distribution cert via Xcode → Settings → Accounts → Manage Certificates → + → Apple Distribution (one-click; no manual CSR dance needed).
10. ✅ Generated App Store provisioning profile named exactly `NwdViewer App Store` at developer.apple.com → Profiles → + → App Store. Downloaded `.mobileprovision`, scp'd to Mac → auto-picked up by symlinked dir.
11. ✅ First Release `.ipa` built, packaged, uploaded to App Store Connect via Transporter (build `1.0.0 (1)`, ID `6767396471`), processed, installed on iPad via TestFlight Internal Testing.

### Profile location bridge (the symlink — important)

VS Pair-to-Mac reads provisioning profiles from `~/Library/MobileDevice/Provisioning Profiles/`. Xcode 26 writes them to `~/Library/Developer/Xcode/UserData/Provisioning Profiles/`. **macOS now auto-cleans the legacy MobileDevice path** within seconds of any file landing there — so the old `cp` recipe (still present in the historical section above) does not survive on macOS 26.

The fix is a one-time symlink:

```bash
rmdir ~/Library/MobileDevice/Provisioning\ Profiles
ln -s ~/Library/Developer/Xcode/UserData/Provisioning\ Profiles \
      ~/Library/MobileDevice/Provisioning\ Profiles
```

This way every profile Xcode generates (or you scp in) shows up to VS instantly — no copy step ever again, including after cert renewals. Verify with:

```bash
ls -la ~/Library/MobileDevice/Provisioning\ Profiles
# Should show:  -> /Users/orlandohernandez/Library/Developer/Xcode/UserData/Provisioning Profiles
```

## First Release upload — App Store distribution path

Walks through what's now a working end-to-end pipeline. Reuses Pair-to-Mac for the build; uploads via Transporter (or `xcrun altool` once API keys are set up).

### Prerequisites (one-time, all complete as of 2026-05-07)
- Apple Distribution cert in Mac keychain — confirm with `security find-identity -v -p codesigning` (look for `"Apple Distribution: Orlando Hernandez (8RJY4X4Y3H)"`).
- App Store provisioning profile named **exactly** `NwdViewer App Store` (matches csproj `<CodesignProvision>`) at `~/Library/Developer/Xcode/UserData/Provisioning Profiles/`. Profile must show ProvisionedDevices=0 + get-task-allow=false (App Store profile, not Ad Hoc/Development).
- Symlink in place (above).

### Per-release procedure

1. **Bump versions in csproj's Release PropertyGroup:**
   ```xml
   <ApplicationDisplayVersion>1.0.0</ApplicationDisplayVersion>  <!-- semantic, what users see -->
   <ApplicationVersion>2</ApplicationVersion>                     <!-- monotonic, MUST increment per upload -->
   ```
   App Store Connect rejects re-uploads with the same `ApplicationVersion` under the same `ApplicationDisplayVersion`. Convention: bump `ApplicationVersion` per upload (every fresh build), `ApplicationDisplayVersion` per release.

2. **Build Release in VS** (do NOT F5 — see "Don't F5 Release" gotcha below).
   - Switch Configuration → **Release**, target → **net10.0-ios**
   - **Build → Build Solution** (Ctrl+Shift+B)
   - VS will surface a "deployment errors / debug not enabled" dialog at the end if the run target is set to a physical iPad — **ignore it**. The build itself will have succeeded; the deploy step is what fails (correctly — App Store profiles can't be installed via devicectl).

3. **Locate the signed `.app` on the Mac:**
   ```
   ~/Library/Caches/Xamarin/mtbs/builds/HelloWorld_IOS/<hash>/bin/Release/net10.0-ios/ios-arm64/HelloWorld_IOS.app
   ```
   Verify with `codesign -dvvv <path>` — Authority should be `Apple Distribution: Orlando Hernandez (8RJY4X4Y3H)`.

4. **Package the `.ipa` manually** — VS's "Build Solution" runs `dotnet build`, which does NOT honor `<ArchiveOnBuild>true</ArchiveOnBuild>`. That property only fires under `dotnet publish`. So either run `dotnet publish` on the Mac, or just zip the `.app` into a `Payload/` folder by hand (an `.ipa` is literally that):

   ```bash
   APP="$HOME/Library/Caches/Xamarin/mtbs/builds/HelloWorld_IOS/<hash>/bin/Release/net10.0-ios/ios-arm64/HelloWorld_IOS.app"
   OUT="$HOME/Desktop/NwdViewer-1.0.0-build1.ipa"
   WORK="/tmp/ipa-build-$$"
   rm -rf "$WORK" "$OUT"
   mkdir -p "$WORK/Payload"
   cp -R "$APP" "$WORK/Payload/"
   cd "$WORK" && zip -qry "$OUT" Payload
   rm -rf "$WORK"
   ```

   Or just run `pwsh ./deploy-appstore.ps1` (this script automates the above end-to-end from Windows via SSH).

5. **Upload to App Store Connect** — two options:
   - **Transporter.app** (free, Mac App Store): drag the `.ipa` in, click Deliver. Easiest for a first upload.
   - **`xcrun altool`** (CLI, scriptable):
     ```bash
     xcrun altool --upload-app --type ios --file <path-to-ipa> \
       --apiKey <Key-ID> --apiIssuer <Issuer-ID>
     ```
     Requires an App Store Connect API key (App Store Connect → Users and Access → Integrations → App Store Connect API). The `.p8` file goes in `~/.appstoreconnect/private_keys/`.

6. **Wait ~10–30 min** for App Store Connect to process the build (it'll appear under TestFlight tab as "Complete" when ready). Then add to an Internal Testing group → tester accepts the email invite (or enters the redemption code in the TestFlight app on iPad) → app installs.

### Don't F5 Release

Release config signs with the App Store provisioning profile. App Store profiles cannot be installed on devices directly — they're "upload to App Store Connect, then iOS re-signs and installs from there" only. If you F5 a Release build, devicectl returns:

```
0xe800801f (Attempted to install a Beta profile without the proper entitlement.)
```

That's iOS correctly refusing the install. Always **Build** (Ctrl+Shift+B), never **F5/Run**, for Release. Then upload the `.ipa`.

## Useful diagnostic commands

### On Mac
```bash
# List code-signing certs
security find-identity -v -p codesigning

# Decode a provisioning profile
security cms -D -i ~/Library/Developer/Xcode/UserData/Provisioning\ Profiles/<UUID>.mobileprovision

# List iOS simulators
xcrun simctl list devices

# Verify Xcode dev tools path
xcode-select -p   # should be /Applications/Xcode.app/Contents/Developer
```

### On Windows
```powershell
# List installed dotnet workloads
dotnet workload list

# Check VS pair status (in VS): Tools → iOS → Pair to Mac
```

## Known good Pair-to-Mac flow (when things break)

1. Mac powered on, on the network at 192.168.1.139.
2. Remote Login enabled on Mac (System Settings → General → Sharing).
3. From Windows, ping `192.168.1.139` succeeds.
4. VS → Tools → iOS → Pair to Mac → **Orlando's MacBook Pro** → Connect → enter Mac password → green chain icon.
5. If green icon doesn't appear: re-pair, restart VS, verify Mac IP hasn't changed (consider setting static IP on company network).

## File map (toolchain-relevant only)

```
HelloWorld_IOS/
├── HelloWorld_IOS.sln                        # Solution (HelloWorld_IOS + NwdViewer.Aps projects)
├── CLAUDE-TOOLCHAIN.md                       # This file
├── CLAUDE-VIEWER.md                          # Project structure / what the app does (separate doc)
├── deploy-ipad.ps1                           # SSH-based Debug device deploy (workaround for VS deploy bug)
├── deploy-appstore.ps1                       # SSH-based Release build → .ipa packaging (App Store flow)
└── HelloWorld_IOS/
    ├── HelloWorld_IOS.csproj                 # Project (signing config + Bundle ID + version)
    └── Platforms/iOS/Info.plist              # CFBundleIdentifier + UTI declarations + ITSAppUsesNonExemptEncryption
```

For the rest of the project structure (Views, ViewModels, Services, NwdViewer.Aps, etc.), see [CLAUDE-VIEWER.md](CLAUDE-VIEWER.md).

## Phase 3 distribution — pending decision

Original plan (in `plans/my-next-milestone-is-zazzy-peacock.md`) was Custom App via Apple Business Manager → Microsoft Intune. **That path is not available on Individual enrollment** — Custom App requires Organization enrollment ($99/yr separate, requires D-U-N-S, ~1-2 weeks lead time). Pending re-evaluation; in the meantime the v1 build is reachable via TestFlight Internal Testing.

Realistic Individual-enrollment options:
- **TestFlight External Testing** — up to 10K testers, requires brief Beta App Review (~24 hr), each build expires 90 days after upload. Good for 5–500-person internal company rollout if you don't mind the expiry.
- **Ad Hoc** — up to 100 devices/year, manual UDID list, install via Apple Configurator or MDM. Good for a small fixed device list with no auto-update.
- **Public App Store** — full App Review, anyone can install. Permanent listing.
- **Upgrade to Org enrollment** — unlocks original Custom App path. Highest effort but gives the cleanest Intune-managed experience.

## Outside this project, on the Mac
- `~/Desktop/Bootstrap/` — throwaway Xcode project. Originally bootstrapped the free-tier cert; on 2026-05-07 its Bundle ID was changed to `com.accoes.nwd3dviewer` to host the new Development profile generation via Xcode auto-management. Don't delete — re-using it is faster than spinning up a new dummy project the next time a profile needs regenerating.
- `~/Library/Developer/Xcode/UserData/Provisioning Profiles/<UUID>.mobileprovision` — actual profile location (Xcode 26 default). All profiles live here.
- `~/Library/MobileDevice/Provisioning Profiles` — **symlink** to the above (NOT a copy — macOS auto-cleans copies). VS Pair-to-Mac reads from this symlinked path.
- Login keychain — contains both Apple Development and Apple Distribution certs (`security find-identity -v -p codesigning` to inspect).
- `~/Desktop/NwdViewer-<version>-build<n>.ipa` — convention for Release-build artifacts. Created by `deploy-appstore.ps1` (or by hand). Upload via Transporter or `xcrun altool`.
