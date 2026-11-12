# Uninstall only the verified application folder; settings are kept with -KeepSettings.
param(
    [string]$Target = $PSScriptRoot,
    [string]$StartMenu = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [string]$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Streamsthetic',
    [string]$Desktop = [Environment]::GetFolderPath('Desktop'),
    [switch]$RemoveCamera,
    [switch]$KeepSettings
)
$ErrorActionPreference = 'Stop'
$Target = [IO.Path]::GetFullPath($Target).TrimEnd('\')
if ($Target -eq [IO.Path]::GetPathRoot($Target).TrimEnd('\')) { throw 'Uninstall requires the Streamsthetic installation folder.' }
if (-not (Test-Path -LiteralPath (Join-Path $Target 'Streamsthetic.exe'))) { throw "No Streamsthetic installation at $Target" }
if ((Get-Item -LiteralPath $Target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'This installation path is a folder link. Run uninstall from the original installation folder.' }
$receiptPath = Join-Path $Target 'installation.json'
if (Test-Path -LiteralPath $receiptPath) {
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if (-not [string]::Equals([IO.Path]::GetFullPath($receipt.Target).TrimEnd('\'), $Target, [StringComparison]::OrdinalIgnoreCase)) { throw 'The installation record does not match this folder. Run uninstall from the original Streamsthetic installation.' }
    if (-not $PSBoundParameters.ContainsKey('StartMenu')) { $StartMenu = $receipt.StartMenu }
    if (-not $PSBoundParameters.ContainsKey('Desktop')) { $Desktop = $receipt.Desktop }
    if (-not $PSBoundParameters.ContainsKey('UninstallKey')) { $UninstallKey = $receipt.UninstallKey }
} elseif (-not [string]::Equals($Target, [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run uninstall from the installed Streamsthetic folder. Its installation record could not be found here.'
}
$targetPrefix = $Target + '\'
Get-Process Streamsthetic, StreamstheticReceiver, PhoneCam, PhoneCamReceiver, adb -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force

if ($RemoveCamera) {
    # Remove system devices before unregistering their source; encoded commands preserve literal paths.
    $receiver = Join-Path $Target 'StreamstheticReceiver.exe'
    if (-not (Test-Path -LiteralPath $receiver)) { throw 'The video component is missing, so the camera devices could not be removed. Reinstall Streamsthetic, then try uninstalling again.' }
    $template = @'
$ErrorActionPreference = 'Stop'
try {
    & '{{RECEIVER}}' --uninstall-camera
    if ($LASTEXITCODE) { throw 'Could not remove the virtual camera devices.' }
    Stop-Service FrameServerMonitor,FrameServer -Force -ErrorAction SilentlyContinue
    $cameraRoot = [IO.Path]::GetFullPath('C:\ProgramData\PhoneCam')
    $dll = Join-Path $cameraRoot 'vcam\VCamSampleSource.dll'
    if (Test-Path -LiteralPath $dll) {
        $p = Start-Process regsvr32 -ArgumentList '/u', '/s', $dll -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode) { throw 'Could not unregister the virtual camera source.' }
        Remove-Item -LiteralPath $cameraRoot -Recurse -Force
    }
} catch { Write-Error $_; exit 1 }
finally { Start-Service FrameServer,FrameServerMonitor -ErrorAction SilentlyContinue }
'@
    $steps = $template.Replace('{{RECEIVER}}', $receiver.Replace("'", "''"))
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($steps))
    $remove = Start-Process powershell -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded
    if ($remove.ExitCode) { throw 'Camera removal did not finish. The app files were kept so you can try again.' }
}

$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @($StartMenu, $Desktop)) {
    if (-not $folder) { continue }
    $shortcut = Join-Path $folder 'Streamsthetic.lnk'
    if ((Test-Path -LiteralPath $shortcut) -and $shell.CreateShortcut($shortcut).TargetPath -eq (Join-Path $Target 'Streamsthetic.exe')) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($name in 'Streamsthetic', 'PhoneCam') {
    $value = (Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue).$name
    if ($value -and $value.Trim('"').StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-ItemProperty -LiteralPath $runKey -Name $name -ErrorAction SilentlyContinue
    }
}
$entry = Get-ItemProperty -LiteralPath $UninstallKey -ErrorAction SilentlyContinue
if ($entry -and $entry.InstallLocation -eq $Target) { Remove-Item -LiteralPath $UninstallKey -Recurse -Force }
$defaultTarget = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Streamsthetic')).TrimEnd('\')
if (-not $KeepSettings -and $Target -eq $defaultTarget) {
    foreach ($name in 'Streamsthetic', 'PhoneCam') {
        $settingsPath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA $name))
        if ($settingsPath.StartsWith([IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $settingsPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# A PowerShell helper retries after this script exits; never pass removal paths through cmd.exe.
$cleanup = @'
$ErrorActionPreference = 'Stop'
$target = '{{TARGET}}'
if ($target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\')) { exit 1 }
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    Start-Sleep -Milliseconds 500
    try {
        if (-not (Test-Path -LiteralPath $target)) { exit 0 }
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }
        Remove-Item -LiteralPath $target -Recurse -Force
        exit 0
    } catch { if ($attempt -eq 19) { exit 1 } }
}
'@
$encodedCleanup = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cleanup.Replace('{{TARGET}}', $Target.Replace("'", "''"))))
Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encodedCleanup
Write-Output 'The PC app has been removed. File cleanup is finishing.'
if (-not $RemoveCamera) { Write-Output 'The Streamsthetic Windows camera devices remain installed.' }
Write-Output 'The Android app is unchanged; remove it separately on your phone if needed.'
