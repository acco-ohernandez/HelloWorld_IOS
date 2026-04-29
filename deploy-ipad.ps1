# Deploy HelloWorld_IOS to the paired iPad over SSH.
#
# WHY THIS EXISTS:
#   In our environment, VS 17.14 reports "Deploy: 1 succeeded" after F5 but
#   never actually installs the .app on the iPad — the run dropdown shows a
#   generic "Remote Device" placeholder because the iOS Device Broker on the
#   Mac doesn't get a GetDevicesMessage from VS, so it never calls
#   `mlaunch --listdev`. The build itself works (signed for ios-arm64), but
#   the install + launch hops are no-ops without a real device handle.
#
#   This script bypasses that by SSHing into the Mac and using
#   `xcrun devicectl` directly — same path Xcode uses, fully reliable.
#
# USAGE:
#   1. Build in VS (Ctrl+Shift+B). The .app gets built and signed on the Mac.
#   2. Run this script:  pwsh ./deploy-ipad.ps1
#   3. App installs and launches on the iPad in ~5 seconds.
#
# Requires:
#   - SSH key auth set up (one-time):  ~/.ssh/id_ed25519_mac on Windows ->
#     ~/.ssh/authorized_keys on the Mac.
#   - Mac at 192.168.1.139, user orlandohernandez (configurable below).
#   - Solution built at least once for ios-arm64 (the .app is read from the
#     Xamarin mtbs build cache).

$ErrorActionPreference = 'Stop'

# --- Config ----------------------------------------------------------------
$MacHost   = '192.168.1.139'
$MacUser   = 'orlandohernandez'
$SshKey    = "$env:USERPROFILE\.ssh\id_ed25519_mac"
$KnownHosts= "$env:TEMP\known_hosts_mac"
$DeviceId  = 'EFEA7DF3-B49A-59DA-8BE7-B257D2C709F2'   # Orlando's iPad (CoreDevice UUID)
$BundleId  = 'com.orlandohernandez.Bootstrap'
$AppName   = 'HelloWorld_IOS'
# --------------------------------------------------------------------------

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
    & $Ssh `
        -i $SshKey `
        -o StrictHostKeyChecking=accept-new `
        -o UserKnownHostsFile=$KnownHosts `
        -o LogLevel=ERROR `
        -o PreferredAuthentications=publickey `
        "$MacUser@$MacHost" $Cmd
    if ($LASTEXITCODE -ne 0) { throw "Mac SSH command failed (exit $LASTEXITCODE): $Cmd" }
}

Write-Host "Deploying $AppName to iPad..." -ForegroundColor Cyan

# 1. Locate latest signed .app on the Mac. The mtbs builds dir has a
#    session-scoped subdir; we pick the most recently modified one.
Write-Host "  Locating latest signed .app on Mac..." -NoNewline
$findApp = "ls -td ~/Library/Caches/Xamarin/mtbs/builds/$AppName/*/bin/Debug/net10.0-ios/ios-arm64/device-builds/*/$AppName.app 2>/dev/null | head -1"
$appPath = Invoke-Mac $findApp
if (-not $appPath) {
    Write-Host " not found." -ForegroundColor Red
    Write-Host "    -> Build the iOS device target in VS first (Ctrl+Shift+B with 'Remote Device' selected)." -ForegroundColor Yellow
    exit 1
}
Write-Host " ok"
Write-Host "    $appPath"

# 2. Install + launch.
Write-Host "  Installing on iPad..."
Invoke-Mac "xcrun devicectl device install app --device $DeviceId '$appPath' 2>&1 | tail -8"

Write-Host "  Launching on iPad..."
Invoke-Mac "xcrun devicectl device process launch --device $DeviceId $BundleId 2>&1 | tail -3"

Write-Host "Done. The app should now be running on Orlando's iPad." -ForegroundColor Green
