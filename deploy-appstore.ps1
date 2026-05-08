# Package a Release-built HelloWorld_IOS.app into an .ipa for App Store Connect upload.
#
# WHY THIS EXISTS:
#   .NET MAUI's <ArchiveOnBuild>true</ArchiveOnBuild> only fires under `dotnet publish`,
#   not under VS's "Build Solution" (which runs `dotnet build`). So a VS Release build
#   produces a correctly-signed .app but no .ipa. This script wraps the .app into a
#   Payload/ directory, zips it, and writes a versioned .ipa to the Mac's Desktop —
#   ready for upload via Transporter or xcrun altool.
#
# USAGE:
#   1. In VS: Configuration -> Release, target -> net10.0-ios, Build -> Build Solution.
#      (The "deployment errors / Debug not enabled" dialogs at the end are EXPECTED;
#      ignore them. The build itself succeeded -- only the post-build deploy step
#      fails, because App Store profiles can't be installed via devicectl.)
#   2. Run this script:    pwsh ./deploy-appstore.ps1
#      Or with explicit version/build override:
#                          pwsh ./deploy-appstore.ps1 -Version 1.0.1 -Build 3
#   3. Open Transporter.app on the Mac -> drag the .ipa -> Deliver.
#
# Requires:
#   - SSH key auth set up (one-time): ~/.ssh/id_ed25519_mac on Windows ->
#     ~/.ssh/authorized_keys on the Mac (same setup deploy-ipad.ps1 uses).
#   - Mac at 192.168.1.139, user orlandohernandez (configurable below).
#   - Solution built once with Release config -- the .app is read from the
#     Xamarin mtbs build cache.
#
# OPTIONAL: --upload flag to call xcrun altool directly (CLI upload). Requires an
# App Store Connect API key (.p8) at ~/.appstoreconnect/private_keys/ on the Mac.
# Generate one at App Store Connect -> Users and Access -> Integrations -> Keys.

param(
    # Semantic version. Used ONLY to name the output .ipa file (App Store Connect
    # reads CFBundleShortVersionString from the embedded Info.plist, which comes
    # from csproj <ApplicationDisplayVersion>). Default: read from csproj's
    # Release-conditional PropertyGroup.
    [string]$Version = '',

    # Build number. Same naming-only role -- App Store Connect reads CFBundleVersion
    # from the embedded Info.plist, which comes from csproj <ApplicationVersion>.
    # MUST be incremented in csproj before each build (App Store Connect rejects
    # re-uploads with the same build number under the same version).
    [string]$Build = '',

    # If set, attempts to upload via `xcrun altool` after packaging. Requires the
    # ASC_API_KEY_ID and ASC_API_ISSUER_ID environment variables on the Mac side,
    # plus the .p8 key file at ~/.appstoreconnect/private_keys/AuthKey_<KeyID>.p8.
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'

# --- Config ----------------------------------------------------------------
$MacHost    = '192.168.1.139'
$MacUser    = 'orlandohernandez'
$SshKey     = "$env:USERPROFILE\.ssh\id_ed25519_mac"
$KnownHosts = "$env:TEMP\known_hosts_mac"
$AppName    = 'HelloWorld_IOS'   # matches csproj name AND the .app folder name
# --------------------------------------------------------------------------

# Locate ssh.exe (same fallback chain deploy-ipad.ps1 uses)
$Ssh = $null
foreach ($candidate in @(
    "$env:WINDIR\System32\OpenSSH\ssh.exe",
    "$env:ProgramFiles\OpenSSH\ssh.exe",
    "$env:ProgramFiles\Git\usr\bin\ssh.exe",
    "${env:ProgramFiles(x86)}\Git\usr\bin\ssh.exe"
)) {
    if ($candidate -and (Test-Path $candidate)) { $Ssh = $candidate; break }
}
if (-not $Ssh) { throw "ssh.exe not found. Install Git for Windows or enable Windows OpenSSH client." }

function Invoke-Mac {
    param([string]$Cmd)
    $out = & $Ssh `
        -i $SshKey `
        -o StrictHostKeyChecking=accept-new `
        -o UserKnownHostsFile=$KnownHosts `
        -o LogLevel=ERROR `
        -o PreferredAuthentications=publickey `
        "$MacUser@$MacHost" $Cmd
    if ($LASTEXITCODE -ne 0) { throw "Mac SSH command failed (exit $LASTEXITCODE): $Cmd" }
    return $out
}

# --- 1. Resolve Version/Build from csproj if not passed --------------------
if (-not $Version -or -not $Build) {
    $csprojPath = Join-Path $PSScriptRoot "$AppName\$AppName.csproj"
    if (-not (Test-Path $csprojPath)) {
        throw "csproj not found at $csprojPath. Pass -Version and -Build explicitly, or run from the solution root."
    }
    $csproj = Get-Content $csprojPath -Raw
    # Find the Release-conditional PropertyGroup
    $relMatch = [regex]::Match($csproj, "Configuration..\s*==\s*'Release'.*?</PropertyGroup>", 'Singleline')
    if (-not $relMatch.Success) {
        throw "Couldn't find Release PropertyGroup in csproj. Pass -Version and -Build explicitly."
    }
    $relText = $relMatch.Value
    if (-not $Version) {
        $m = [regex]::Match($relText, '<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>')
        if ($m.Success) { $Version = $m.Groups[1].Value } else { $Version = 'unknown' }
    }
    if (-not $Build) {
        $m = [regex]::Match($relText, '<ApplicationVersion>([^<]+)</ApplicationVersion>')
        if ($m.Success) { $Build = $m.Groups[1].Value } else { $Build = '0' }
    }
}
Write-Host "Packaging $AppName $Version (build $Build) for App Store distribution" -ForegroundColor Cyan

# --- 2. Locate latest signed Release .app on the Mac -----------------------
Write-Host "  Locating latest signed Release .app on Mac..." -NoNewline
$findApp = "ls -td ~/Library/Caches/Xamarin/mtbs/builds/$AppName/*/bin/Release/net10.0-ios/ios-arm64/$AppName.app 2>/dev/null | head -1"
$appPath = (Invoke-Mac $findApp).Trim()
if ([string]::IsNullOrWhiteSpace($appPath)) {
    Write-Host " not found." -ForegroundColor Red
    Write-Host "    -> Build Release config in VS first:" -ForegroundColor Yellow
    Write-Host "       Configuration: Release, target: net10.0-ios, Build -> Build Solution (Ctrl+Shift+B)." -ForegroundColor Yellow
    exit 1
}
Write-Host " ok"
Write-Host "    $appPath"

# --- 3. Sanity check codesign --------------------------------------------
Write-Host "  Verifying codesign..."
$cs = Invoke-Mac "codesign -dvvv '$appPath' 2>&1 | grep -E 'Authority|TeamIdentifier|Identifier='"
$cs | ForEach-Object { Write-Host "    $_" }
$csText = if ($cs -is [array]) { $cs -join "`n" } else { [string]$cs }
if ($csText -notmatch 'Apple Distribution') {
    Write-Host ""
    Write-Host "  WARNING: .app is NOT signed with an Apple Distribution cert." -ForegroundColor Yellow
    Write-Host "  This usually means VS was built in Debug, not Release. App Store Connect" -ForegroundColor Yellow
    Write-Host "  will REJECT a Debug-signed upload. Switch VS to Release and rebuild." -ForegroundColor Yellow
    Write-Host ""
}

# --- 4. Package the .ipa ---------------------------------------------------
$ipaName = "NwdViewer-$Version-build$Build.ipa"
$macIpa  = "~/Desktop/$ipaName"
Write-Host "  Packaging .ipa: $ipaName"
# Single-line bash with PowerShell-interpolated paths. /tmp work dir uses $$
# (bash PID) for uniqueness. Backticks escape the $ so PowerShell doesn't
# expand them locally.
$pkgCmd = "set -e && WORK=`"/tmp/ipa-build-`$`$`" && rm -rf `"`$WORK`" $macIpa && mkdir -p `"`$WORK/Payload`" && cp -R '$appPath' `"`$WORK/Payload/`" && cd `"`$WORK`" && zip -qry $macIpa Payload && rm -rf `"`$WORK`" && ls -lh $macIpa"
$pkgOut = Invoke-Mac $pkgCmd
$pkgOut | ForEach-Object { Write-Host "    $_" }

# --- 5. Optional: xcrun altool upload --------------------------------------
if ($Upload) {
    Write-Host ""
    Write-Host "  Uploading via xcrun altool..." -ForegroundColor Cyan
    Write-Host "  (Requires ASC_API_KEY_ID + ASC_API_ISSUER_ID env vars on Mac, plus .p8 in ~/.appstoreconnect/private_keys/)"
    $uploadCmd = "[ -n `"`$ASC_API_KEY_ID`" ] && [ -n `"`$ASC_API_ISSUER_ID`" ] || { echo 'ERROR: set ASC_API_KEY_ID and ASC_API_ISSUER_ID on the Mac (e.g., in ~/.zshenv)'; exit 1; }; xcrun altool --upload-app --type ios --file $macIpa --apiKey `"`$ASC_API_KEY_ID`" --apiIssuer `"`$ASC_API_ISSUER_ID`""
    $uploadOut = Invoke-Mac $uploadCmd
    $uploadOut | ForEach-Object { Write-Host "    $_" }
    Write-Host ""
    Write-Host "Upload submitted. Check App Store Connect -> Apps -> NWD Viewer v1 -> TestFlight in ~10-30 min." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Done. .ipa is on the Mac at: $macIpa" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next: upload to App Store Connect." -ForegroundColor Cyan
    Write-Host "  Easy path: Open Transporter.app on the Mac, drag the .ipa, click Deliver."
    Write-Host "  CLI path : Re-run with -Upload after configuring App Store Connect API key on the Mac."
}
