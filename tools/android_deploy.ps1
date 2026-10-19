<#
.SYNOPSIS
  Android device preflight and deploy for the WirelessScanner Android port (DR-0015; lesson LL-2026-10-19-ANDROID-DEVICE-AUTH).

.DESCRIPTION
  Gate order (reference lessons A-LRN-016/017/043 and the reference BUILD_LESSONS_PLAYBOOK §1 and §6):
    1. PREFLIGHT  - adb state of the target must be "device". "unauthorized" / "offline" / missing is reported with the fix steps
                    and is never turned into install, launch or visual evidence.
    2. DEPLOY     - copy the APK to the owner's install folder (This PC\<device>\SD card\DEPLOY) over MTP, then copy it BACK and
                    compare SHA-256 (an MTP "copy succeeded" is not proof - playbook §6.4).
    3. INSTALL    - only with -Install and an authorized target: adb -s <serial> install -r, cold launch, FATAL EXCEPTION check,
                    foreground check and a screenshot for visual review (launch alone is not visual evidence).
  Every run writes a JSON result bound to the exact APK SHA-256. The device serial is masked in the output (privacy rule).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\android_deploy.ps1 -PreflightOnly
  powershell -ExecutionPolicy Bypass -File tools\android_deploy.ps1 -ApkPath AndroidApp\artifacts\deploy\WirelessScanner-Android-1.5.0-preview1.apk
  powershell -ExecutionPolicy Bypass -File tools\android_deploy.ps1 -ApkPath <apk> -Install
#>
param(
    [string]$ApkPath,
    [string]$DeviceName = 'Galaxy8NOMAD',
    [string]$StorageName = 'SD card',
    [string]$DeployFolder = 'DEPLOY',
    [string]$PackageName = 'com.wirelessscanner.android',
    [switch]$PreflightOnly,
    [switch]$Install,
    [switch]$SkipCopy
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
$outDir = Join-Path $root 'AndroidApp\artifacts\deploy'
New-Item -ItemType Directory -Force $outDir | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$result = [ordered]@{ utc = (Get-Date).ToUniversalTime().ToString('o'); tool = 'tools/android_deploy.ps1'; lesson = 'LL-2026-10-19-ANDROID-DEVICE-AUTH' }

function Mask([string]$s) { if ([string]::IsNullOrEmpty($s) -or $s.Length -le 4) { return $s } ; return ('*' * ($s.Length - 4)) + $s.Substring($s.Length - 4) }

# ---------------------------------------------------------------- 1. preflight
$pre = [ordered]@{ adb = (Test-Path $adb) }
$pub = Join-Path $env:USERPROFILE '.android\adbkey.pub'
if (Test-Path $pub) { $pre.hostKeySha256 = (Get-FileHash $pub -Algorithm SHA256).Hash }
$rows = @()
if ($pre.adb) {
    # Windows PowerShell 5.1 turns native stderr into terminating errors under 'Stop'; adb prints "daemon not running;
    # starting now" to stderr on the first call of a session. Start the server with errors relaxed, then list devices.
    $ErrorActionPreference = 'Continue'
    & $adb start-server *> $null
    $lines = & $adb devices -l 2>$null | Where-Object { $_ -is [string] } | Select-Object -Skip 1 | Where-Object { $_.Trim() -ne '' -and $_ -notmatch '^\* daemon' }
    $ErrorActionPreference = 'Stop'
    foreach ($l in $lines) {
        $parts = $l -split '\s+'
        $rows += [ordered]@{ serial = $parts[0]; state = $parts[1]; detail = ($parts[2..($parts.Length - 1)] -join ' ') }
    }
}
$pre.targets = @($rows | ForEach-Object { [ordered]@{ serial = (Mask $_.serial); state = $_.state } })
$ready = @($rows | Where-Object { $_.state -eq 'device' })
if ($ready.Count -eq 1) {
    $serial = $ready[0].serial
    $pre.state = 'device'
    $pre.model = (& $adb -s $serial shell getprop ro.product.model).Trim()
    $pre.api = (& $adb -s $serial shell getprop ro.build.version.sdk).Trim()
    $pre.abi = (& $adb -s $serial shell getprop ro.product.cpu.abi).Trim()
    # Android 11+ revokes ADB authorizations unused for 7 days unless this is set (hypothesis H1 in the lesson record).
    $pre.adbAuthTimeoutDisabled = (& $adb -s $serial shell settings get global adb_allowed_connection_time).Trim()
} elseif ($rows.Count -eq 0) {
    $pre.state = 'no-target'
} else {
    $pre.state = ($rows | Select-Object -First 1).state
}
$result.preflight = $pre

$fix = @'
DEVICE NOT AUTHORIZED - fix on the TABLET (reference R3 section 5; the reference projects hit this same tablet repeatedly):
  1. Unlock the tablet screen and keep it unlocked.
  2. Settings > Developer options > "Revoke USB debugging authorizations" (or switch USB debugging OFF, then ON).
  3. Unplug and replug the USB cable (data cable; USB mode File transfer).
  4. Accept "Allow USB debugging?" ON THE TABLET with "Always allow from this computer" ticked.
  5. Recommended once authorized: Developer options > "Disable adb authorization timeout" ON
     (Android 11+ otherwise drops this computer's key after 7 days without use).
  6. Re-run this preflight. "adb kill-server" alone does not fix it.
'@

Write-Host "Preflight: $($pre.state) | targets: $(@($pre.targets | ForEach-Object { "$($_.serial)=$($_.state)" }) -join ', ') | host key SHA-256 $($pre.hostKeySha256)"
if ($pre.state -ne 'device') { Write-Host $fix }

if ($PreflightOnly) {
    $path = Join-Path $outDir "preflight-$stamp.json"
    $result | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 $path
    Write-Host "Result: $path"
    if ($pre.state -ne 'device') { exit 2 } else { exit 0 }
}

# ---------------------------------------------------------------- 2. deploy copy (MTP) with read-back hash
if (-not $ApkPath) { throw 'ApkPath is required unless -PreflightOnly.' }
$apk = Resolve-Path $ApkPath
$apkHash = (Get-FileHash $apk -Algorithm SHA256).Hash
$apkSize = (Get-Item $apk).Length
$result.apk = [ordered]@{ path = $apk.Path.Replace($root + '\', ''); sha256 = $apkHash; bytes = $apkSize }
$bytes = [System.IO.File]::ReadAllBytes($apk)
if ($bytes[0] -ne 0x50 -or $bytes[1] -ne 0x4B) { throw 'Not a ZIP/APK (no PK header).' }

if (-not $SkipCopy) {
    $shell = New-Object -ComObject Shell.Application
    $dev = $shell.Namespace(17).Items() | Where-Object { $_.Name -eq $DeviceName }
    if (-not $dev) { throw "Device '$DeviceName' is not visible in This PC (USB mode must be File transfer)." }
    $store = $dev.GetFolder.Items() | Where-Object { $_.Name -eq $StorageName }
    if (-not $store) { throw "Storage '$StorageName' not found on $DeviceName." }
    $target = $store.GetFolder.Items() | Where-Object { $_.Name -eq $DeployFolder }
    if (-not $target) { throw "Folder '$DeployFolder' not found on $DeviceName\$StorageName (create it on the tablet)." }
    $name = [System.IO.Path]::GetFileNameWithoutExtension($apk)
    $target.GetFolder.CopyHere($apk.Path, 16)
    $deadline = (Get-Date).AddSeconds(180)
    do { Start-Sleep -Seconds 2; $item = $target.GetFolder.Items() | Where-Object { $_.Name -eq $name -or $_.Name -eq "$name.apk" } } while (-not $item -and (Get-Date) -lt $deadline)
    if (-not $item) { throw 'Copy not confirmed within 180 s.' }
    # read back and hash (MTP success is not proof)
    $back = Join-Path $env:TEMP "ws_readback_$stamp"
    New-Item -ItemType Directory -Force $back | Out-Null
    $shell.Namespace($back).CopyHere($item, 16)
    $deadline = (Get-Date).AddSeconds(180)
    do { Start-Sleep -Seconds 2; $copy = Get-ChildItem $back -File | Select-Object -First 1 } while ((-not $copy -or $copy.Length -lt $apkSize) -and (Get-Date) -lt $deadline)
    Start-Sleep -Seconds 1
    $copyHash = if ($copy) { (Get-FileHash $copy.FullName -Algorithm SHA256).Hash } else { '' }
    Remove-Item -Recurse -Force $back -ErrorAction SilentlyContinue
    $result.deployCopy = [ordered]@{ location = "This PC\$DeviceName\$StorageName\$DeployFolder"; readBackSha256 = $copyHash; verified = ($copyHash -eq $apkHash) }
    Write-Host "Deploy copy: $($result.deployCopy.location) | read-back hash match: $($result.deployCopy.verified)"
}

# ---------------------------------------------------------------- 3. install smoke (authorized target only)
$inst = [ordered]@{ requested = [bool]$Install }
if ($Install) {
    if ($pre.state -ne 'device') {
        $inst.status = "BLOCKED BY DEVICE AUTHORIZATION ($($pre.state))"
    } else {
        & $adb -s $serial install -r $apk.Path | Out-Host
        & $adb -s $serial logcat -c
        & $adb -s $serial shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1 | Out-Null
        Start-Sleep -Seconds 4
        $procId = (& $adb -s $serial shell pidof $PackageName).Trim()
        $fatal = & $adb -s $serial logcat -d | Select-String 'FATAL EXCEPTION'
        $inst.status = if ($procId -and -not $fatal) { 'passed' } else { 'failed' }
        $inst.launchPid = $procId
        $inst.fatal = [bool]$fatal
        $inst.installedVersion = ((& $adb -s $serial shell dumpsys package $PackageName) | Select-String 'versionName=' | Select-Object -First 1).ToString().Trim()
        # Launch without a crash is not proof the UI is visible (AP-12: black text on a black window). Capture the screen for review.
        # A cold start can take longer than the 4 s above (first launch after install): poll up to 10 s before deciding.
        $inst.foreground = $false
        for ($i = 0; $i -lt 10 -and -not $inst.foreground; $i++) {
            $top = ((& $adb -s $serial shell dumpsys activity activities) | Select-String 'topResumedActivity' | Select-Object -First 1)
            $inst.foreground = [bool]($top -and $top.ToString().Contains($PackageName))
            if (-not $inst.foreground) { Start-Sleep -Seconds 1 }
        }
        # Screenshots of the real app show nearby SSIDs/BSSIDs: privacy rule -> local-only evidence (never synced or published).
        $shotDir = Join-Path $root 'governance\evidence\local-only\android-screens'
        New-Item -ItemType Directory -Force $shotDir | Out-Null
        $shot = Join-Path $shotDir "launch-$stamp.png"
        $tmp = "/sdcard/ws_launch_$stamp.png"
        & $adb -s $serial shell screencap -p $tmp | Out-Null
        & $adb -s $serial pull $tmp $shot | Out-Null
        & $adb -s $serial shell rm $tmp | Out-Null
        if (Test-Path $shot) { $inst.screenshot = $shot.Replace($root + '\', ''); $inst.screenshotSha256 = (Get-FileHash $shot -Algorithm SHA256).Hash; $inst.visualReview = 'pending: open the screenshot; launch alone is not visual evidence' }
        if (-not $inst.foreground) { $inst.status = 'failed' }
    }
    Write-Host "Install: $($inst.status)"
}
$result.install = $inst
$path = Join-Path $outDir "deploy-$stamp.json"
$result | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 $path
Write-Host "Result: $path"
