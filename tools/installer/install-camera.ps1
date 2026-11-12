# Camera setup (asks for administrator approval): installs or updates the Streamsthetic cameras.
# Safe for any Windows user name, including names with an apostrophe (e.g. "Melissa's Pc"): the elevated steps are passed
# encoded, not as a quoted command line (a quote in the path broke the old version and nothing was installed).
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'install-preflight.ps1')
$source = Join-Path $PSScriptRoot 'vcam\VCamSampleSource.dll'
$receiver = Join-Path $PSScriptRoot 'StreamstheticReceiver.exe'
$clsidKeys = 3..6 | ForEach-Object { 'HKLM:\SOFTWARE\Classes\CLSID\{31621596-4208-4D72-9066-1F950707442' + $_ + '}' }
$log = Join-Path $env:TEMP 'streamsthetic-install-camera.txt'
Remove-Item $log -ErrorAction SilentlyContinue

if (-not (Test-Path $source)) { Write-Warning "A camera setup file is missing. Extract the complete package and try again. Missing file: $source"; exit 1 }
if (-not (Test-Path -LiteralPath $receiver)) { Write-Warning "The video component is missing. Extract the complete package and try again. Missing file: $receiver"; exit 1 }

$template = @'
$ErrorActionPreference = 'Stop'
$exitCode = 0
try {
    . '{{HELPER}}'
    $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    "Camera setup worker; elevated=$elevated" | Set-Content -LiteralPath '{{LOG}}' -Encoding UTF8
    Add-Content -LiteralPath '{{LOG}}' -Value 'Stage: copy camera component' -Encoding UTF8
    Stop-Service FrameServerMonitor, FrameServer -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force 'C:\ProgramData\PhoneCam\vcam' | Out-Null
    Copy-Item -LiteralPath '{{SOURCE}}' -Destination 'C:\ProgramData\PhoneCam\vcam' -Force
    Invoke-StreamstheticNativeStep -FilePath "$env:WINDIR\System32\icacls.exe" -Arguments @('C:\ProgramData\PhoneCam\vcam', '/grant', '*S-1-5-19:(OI)(CI)RX', '*S-1-5-18:(OI)(CI)RX', '/T', '/Q') -Stage 'camera file permissions' -LogPath '{{LOG}}'
    Invoke-StreamstheticNativeStep -FilePath "$env:WINDIR\System32\regsvr32.exe" -Arguments @('/s', 'C:\ProgramData\PhoneCam\vcam\VCamSampleSource.dll') -Stage 'camera COM registration' -LogPath '{{LOG}}'
    Start-Service FrameServer, FrameServerMonitor -ErrorAction SilentlyContinue
    Invoke-StreamstheticNativeStep -FilePath '{{RECEIVER}}' -Arguments @('--install-camera') -Stage 'virtual camera registration' -LogPath '{{LOG}}'
}
catch { $exitCode = 1; ($_ | Out-String) | Add-Content -LiteralPath '{{LOG}}' -Encoding UTF8 }
finally { Start-Service FrameServer, FrameServerMonitor -ErrorAction SilentlyContinue }
exit $exitCode
'@
$helper = Join-Path $PSScriptRoot 'install-preflight.ps1'
$steps = $template.Replace('{{SOURCE}}', $source.Replace("'", "''")).Replace('{{RECEIVER}}', $receiver.Replace("'", "''")).Replace('{{LOG}}', $log.Replace("'", "''")).Replace('{{HELPER}}', $helper.Replace("'", "''"))
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($steps))
try {
    $worker = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded -ErrorAction Stop
} catch {
    $message = Get-StreamstheticElevationFailure $_.Exception
    $message | Set-Content -LiteralPath $log -Encoding UTF8
    Write-Warning $message
    exit 1
}

$missing = @($clsidKeys | Where-Object { -not (Test-Path $_) })
$installed = 'C:\ProgramData\PhoneCam\vcam\VCamSampleSource.dll'
$matches = (Test-Path $installed) -and ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $installed).Hash)
$markers = @('SystemCamera', 'SystemCamera2', 'SystemCamera3', 'SystemCamera4' | Where-Object {
    (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\PhoneCam' -ErrorAction SilentlyContinue).$_ -ne 1
})
if ($worker.ExitCode -eq 0 -and $missing.Count -eq 0 -and $matches -and $markers.Count -eq 0) {
    Write-Output 'All four Streamsthetic cameras are installed. Open Streamsthetic to connect a phone.'
} else {
    Add-Content -LiteralPath $log -Value "Verification: worker exit=$($worker.ExitCode); missing COM registrations=$($missing.Count); component matches=$matches; missing camera markers=$($markers.Count)" -Encoding UTF8
    Write-Warning "Camera setup did not finish. Review the Windows error in $log before retrying."
    if (Test-Path $log) { Write-Warning ('Details: ' + (Get-Content $log -Raw)) }
    exit 1
}
exit 0
