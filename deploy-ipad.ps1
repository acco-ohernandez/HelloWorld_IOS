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

param(
    # Optional override. If empty (default), the script auto-picks the iPad whose
    # devicectl state is "connected" — i.e., the one currently USB-tethered to the
    # Mac. Pass a UUID explicitly when multiple iPads are connected and you want
    # a specific one.
    [string]$DeviceId = ''
)

$ErrorActionPreference = 'Stop'

# --- Config ----------------------------------------------------------------
$MacHost    = '192.168.4.58' #When Working from the home
#$MacHost    = '172.31.29.122' # When Working from the office
$MacUser    = 'orlandohernandez'
$SshKey     = "$env:USERPROFILE\.ssh\id_ed25519_mac"
$KnownHosts = "$env:TEMP\known_hosts_mac"
$BundleId   = 'com.accoes.nwd3dviewer'
$AppName    = 'HelloWorld_IOS'   # MSBuild .app folder name = csproj name; not the home-screen title
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

# 1. Pick a device. If $DeviceId wasn't passed, query devicectl for paired
#    iPads and pick one. Prefer "connected" (USB-tethered) over
#    "available (paired)" (Wi-Fi only), since devicectl install works either
#    way but USB is faster and what VS would use too.
if ([string]::IsNullOrEmpty($DeviceId)) {
    Write-Host "  Discovering paired iPad..." -NoNewline
    # awk extracts UUID + state for every iPad row. State is "connected" if the
    # row contains the literal word " connected " between fixed-width columns,
    # otherwise it's a Wi-Fi pairing.
    $listCmd = @'
xcrun devicectl list devices 2>/dev/null | awk '/iPad/ {
  uuid="";
  for (i=1;i<=NF;i++) if ($i ~ /^[A-F0-9]{8}-/) uuid=$i;
  if (uuid=="") next;
  state = ($0 ~ /[ \t]connected[ \t]/) ? "connected" : "paired";
  print state " " uuid
}'
'@
    $rawList = Invoke-Mac $listCmd
    if ([string]::IsNullOrEmpty($rawList)) {
        Write-Host " none found." -ForegroundColor Red
        Write-Host "    -> Pair an iPad to the Mac (Xcode -> Window -> Devices and Simulators)." -ForegroundColor Yellow
        exit 1
    }
    $devices = $rawList -split "`r?`n" | Where-Object { $_ -match '^(connected|paired)\s+\S+' } | ForEach-Object {
        $parts = $_ -split '\s+'
        [PSCustomObject]@{ State = $parts[0]; Uuid = $parts[1] }
    }
    $connected = @($devices | Where-Object { $_.State -eq 'connected' })
    $paired    = @($devices | Where-Object { $_.State -eq 'paired' })
    if ($connected.Count -ge 1) { $picked = $connected }
    else                        { $picked = $paired }
    if ($picked.Count -gt 1) {
        Write-Host " multiple devices:" -ForegroundColor Yellow
        $picked | ForEach-Object { Write-Host "      $($_.State)  $($_.Uuid)" }
        Write-Host "    -> Pass one explicitly:  pwsh ./deploy-ipad.ps1 -DeviceId <UUID>" -ForegroundColor Yellow
        exit 1
    }
    $DeviceId = $picked[0].Uuid
    Write-Host " $($picked[0].State)  $DeviceId"
} else {
    Write-Host "  Using device $DeviceId (override)"
}

# 2. Locate latest signed .app on the Mac. The mtbs builds dir has a
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

# 3. Install + launch.
Write-Host "  Installing on iPad..."
$installOut = Invoke-Mac "xcrun devicectl device install app --device $DeviceId '$appPath' 2>&1"
Write-Host $installOut
if ($installOut -match 'ApplicationVerificationFailed|0xe800801[02]|cannot be installed on this device') {
    Write-Host ""
    Write-Host "Install failed: the .app's embedded provisioning profile doesn't" -ForegroundColor Red
    Write-Host "include this iPad's UDID. Rebuild in VS so the freshly-copied" -ForegroundColor Red
    Write-Host "profile (which includes the new iPad) gets embedded:" -ForegroundColor Red
    Write-Host "  1. In VS: Build -> Build Solution (Ctrl+Shift+B)." -ForegroundColor Yellow
    Write-Host "  2. Re-run this script." -ForegroundColor Yellow
    exit 1
}

Write-Host "  Launching on iPad..."
Invoke-Mac "xcrun devicectl device process launch --device $DeviceId $BundleId 2>&1 | tail -3"

Write-Host "Done. The app should now be running on the iPad." -ForegroundColor Green
