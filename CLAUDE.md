# HelloWorld_IOS — MAUI iPad pipeline test

Proof-of-concept .NET MAUI 10 app validating the Windows-VS → Mac → iPad deployment pipeline. Successfully deployed to both iOS Simulator and physical iPad mini on 2026-04-27 with a free Apple ID. Project exists to validate the toolchain, not as production code.

## Quick orientation

- **Solution:** `HelloWorld_IOS.sln`
- **Project:** `HelloWorld_IOS/HelloWorld_IOS.csproj`
- **TFMs:** `net10.0-ios` (iPad target) + `net10.0-windows10.0.19041.0` (local Windows test target)
- **Bundle ID:** `com.orlandohernandez.Bootstrap` — intentionally reusing the bundle ID from the Xcode "Bootstrap" project (see "Free-tier provisioning" below)
- **Display Name:** HelloWorld_IOS

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
- Apple ID `orlando2503@gmail.com` signed into Xcode (Personal Team = free tier)
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

### Bundle ID reuses Bootstrap
```xml
<ApplicationId>com.orlandohernandez.Bootstrap</ApplicationId>
```
Why: see "Free-tier provisioning" below. Free Apple ID can't auto-create new profiles for new bundle IDs from VS, so we reuse Bootstrap's. **Change this once you have a paid developer account.**

### Xcode version bypass
```xml
<ValidateXcodeVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">false</ValidateXcodeVersion>
```
Why: .NET for iOS workload 26.2.10233 expects Xcode 26.3, but Mac has 26.4.1. The check is a strict major.minor match (not minimum). For Hello World this is fine. Remove this if the app starts using iOS 26.4-only APIs that need real bindings.

### Manual provisioning + explicit cert/profile
```xml
<PropertyGroup Condition="'$(TargetFramework)' == 'net10.0-ios' and '$(Configuration)' == 'Debug'">
    <CodesignKey>Apple Development: orlando2503@gmail.com (97JK54NP2N)</CodesignKey>
    <CodesignProvision>iOS Team Provisioning Profile: com.orlandohernandez.Bootstrap</CodesignProvision>
</PropertyGroup>
<PropertyGroup Condition="'$(TargetFramework)'=='net10.0-ios'">
    <ProvisioningType>manual</ProvisioningType>
</PropertyGroup>
```
Why: VS's "Automatic Provisioning" mode gates on the Apple Accounts dialog (Tools → Options → Xamarin → Apple Accounts), which requires App Store Connect API keys — paid accounts only. Free tier MUST use Manual mode. The `<ProvisioningType>manual</ProvisioningType>` was written by VS when we switched the Properties UI to Manual; the cert and profile names were written by hand.

### Info.plist requires CFBundleIdentifier
`Platforms/iOS/Info.plist` has:
```xml
<key>CFBundleIdentifier</key>
<string>com.orlandohernandez.Bootstrap</string>
```
Why: VS's Provisioning Profile dropdown filters profiles by bundle ID read from Info.plist (not csproj). Without this, the dropdown shows "No matching profiles found" even when the profile exists on Mac.

## Free-tier Apple ID provisioning (the painful part)

This is what made today's setup take 6+ hours. **All of this disappears with a paid Apple Developer Program account.**

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

## When you migrate to a paid Apple Developer account

Most of the friction above disappears:
- VS Apple Accounts dialog accepts the API key — Automatic Provisioning works.
- Profiles auto-create for any bundle ID from VS.
- Cert validity becomes 1 year (not 7 days).
- Can register specific bundle IDs in App Store Connect upfront.
- TestFlight beta + Ad Hoc + App Store distribution all unlocked.

Steps when you make the switch:
1. Buy Apple Developer Program ($99/yr Individual or $299/yr Enterprise — verify Enterprise eligibility before purchasing).
2. Generate App Store Connect API key (App Store Connect → Users and Access → Keys).
3. Add to VS via Tools → Options → Xamarin → Apple Accounts.
4. Switch this project's Bundle Signing Scheme from Manual back to **Automatic Provisioning** in Properties.
5. Change the bundle ID to a real production-style one (e.g., `com.accoes.helloworldios` or whatever naming convention you settle on).
6. Remove the `<CodesignKey>` and `<CodesignProvision>` overrides from csproj — let Automatic mode pick them.
7. Optionally remove the `<ValidateXcodeVersion>false</ValidateXcodeVersion>` override once .NET for iOS workload catches up to Xcode 26.4.

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

## File map (for future-me)

```
HelloWorld_IOS/
├── HelloWorld_IOS.sln                        # Solution
├── CLAUDE.md                                 # This file
└── HelloWorld_IOS/
    ├── HelloWorld_IOS.csproj                 # Project (signing config lives here)
    ├── App.xaml(.cs)                         # MAUI App lifecycle
    ├── AppShell.xaml(.cs)                    # MAUI Shell navigation
    ├── MainPage.xaml(.cs)                    # Default Hello World page
    ├── MauiProgram.cs                        # MAUI host builder
    ├── GlobalXmlns.cs                        # XAML namespace defaults
    ├── Platforms/
    │   ├── iOS/
    │   │   ├── AppDelegate.cs                # iOS UIApplicationDelegate
    │   │   ├── Info.plist                    # CFBundleIdentifier set here
    │   │   └── Program.cs                    # iOS entry
    │   └── MacCatalyst/                      # Unused (TFM removed) but files remain
    └── Resources/                            # Icons, splash, fonts, images
```

## Outside this project, on the Mac
- `~/Desktop/Bootstrap/` — throwaway Xcode project that bootstrapped the cert/profile. **Do not delete.**
- `~/Library/Developer/Xcode/UserData/Provisioning Profiles/<UUID>.mobileprovision` — actual profile location.
- `~/Library/MobileDevice/Provisioning Profiles/<UUID>.mobileprovision` — copy made for VS.
- Login keychain — contains the Apple Development cert.
