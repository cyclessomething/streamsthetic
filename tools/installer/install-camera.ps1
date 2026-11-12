# Camera setup (asks for administrator approval): installs or updates the Streamsthetic cameras.
# Safe for any Windows user name, including names with an apostrophe (e.g. "Melissa's Pc"): the elevated steps are passed
# encoded, not as a quoted command line (a quote in the path broke the old version and nothing was installed).
$ErrorActionPreference = 'Continue'
$source = Join-Path $PSScriptRoot 'vcam\VCamSampleSource.dll'
$receiver = Join-Path $PSScriptRoot 'StreamstheticReceiver.exe'
$clsidKeys = 3..6 | ForEach-Object { 'HKLM:\SOFTWARE\Classes\CLSID\{31621596-4208-4D72-9066-1F950707442' + $_ + '}' }
$log = Join-Path $env:TEMP 'streamsthetic-install-camera.txt'
Remove-Item $log -ErrorAction SilentlyContinue

if (-not (Test-Path $source)) { Write-Warning "A camera setup file is missing. Extract the complete package and try again. Missing file: $source"; exit 1 }
if (-not (Test-Path -LiteralPath $receiver)) { Write-Warning "The video component is missing. Extract the complete package and try again. Missing file: $receiver"; exit 1 }

$template = @'
$ErrorActionPreference = 'Stop'
try {
    Stop-Service FrameServerMonitor, FrameServer -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force 'C:\ProgramData\PhoneCam\vcam' | Out-Null
    Copy-Item -LiteralPath '{{SOURCE}}' -Destination 'C:\ProgramData\PhoneCam\vcam' -Force
    icacls 'C:\ProgramData\PhoneCam\vcam' /grant '*S-1-5-19:(OI)(CI)RX' '*S-1-5-18:(OI)(CI)RX' /T /Q | Out-Null
    $p = Start-Process regsvr32 -ArgumentList '/s', 'C:\ProgramData\PhoneCam\vcam\VCamSampleSource.dll' -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "regsvr32 failed with exit code $($p.ExitCode)" }
    Start-Service FrameServer, FrameServerMonitor -ErrorAction SilentlyContinue
    & '{{RECEIVER}}' --install-camera
    if ($LASTEXITCODE) { throw 'Could not install all four virtual camera devices.' }
}
catch { ($_ | Out-String) | Set-Content -LiteralPath '{{LOG}}' }
finally { Start-Service FrameServer, FrameServerMonitor -ErrorAction SilentlyContinue }
'@
$steps = $template.Replace('{{SOURCE}}', $source.Replace("'", "''")).Replace('{{RECEIVER}}', $receiver.Replace("'", "''")).Replace('{{LOG}}', $log.Replace("'", "''"))
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($steps))
Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded

$missing = @($clsidKeys | Where-Object { -not (Test-Path $_) })
$installed = 'C:\ProgramData\PhoneCam\vcam\VCamSampleSource.dll'
$matches = (Test-Path $installed) -and ((Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $installed).Hash)
$markers = @('SystemCamera', 'SystemCamera2', 'SystemCamera3', 'SystemCamera4' | Where-Object {
    (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\PhoneCam' -ErrorAction SilentlyContinue).$_ -ne 1
})
if ($missing.Count -eq 0 -and $matches -and $markers.Count -eq 0 -and -not (Test-Path $log)) {
    Write-Output 'All four Streamsthetic cameras are installed. Open Streamsthetic to connect a phone.'
} else {
    Write-Warning 'Camera setup did not finish. Run setup again and approve the Windows administrator prompt.'
    if (Test-Path $log) { Write-Warning ('Details: ' + (Get-Content $log -Raw)) }
    exit 1
}
exit 0
