# Deploys the freshly built PhoneCam media source (windows/vcam) to C:\ProgramData\PhoneCam\vcam.
# Windows' camera services keep the old DLL loaded, so they are stopped while the file is replaced.
# Asks for administrator rights once (UAC). Optionally registers the DLL (first install) with -Register, and
# installs the always-present system camera with -SystemCamera (so apps never lose PhoneCam when the PC app restarts).
param([switch]$Register, [switch]$SystemCamera)

$root = Split-Path $PSScriptRoot -Parent
$built = Join-Path $root 'windows\vcam\x64\Release\VCamSampleSource.dll'
$target = 'C:\ProgramData\PhoneCam\vcam'
if (-not (Test-Path $built)) { throw "build the camera first: $built not found" }

$steps = @(
    "Stop-Service FrameServerMonitor,FrameServer -Force",
    "New-Item -ItemType Directory -Force '$target' | Out-Null",
    "Copy-Item '$built' '$target\VCamSampleSource.dll' -Force",
    # the camera services (Local Service, System) must be able to read the DLL
    "icacls '$target' /grant '*S-1-5-19:(OI)(CI)RX' '*S-1-5-18:(OI)(CI)RX' /T /Q | Out-Null"
)
if ($Register) { $steps += "regsvr32 /s '$target\VCamSampleSource.dll'" }
$steps += "Start-Service FrameServer,FrameServerMonitor"
if ($SystemCamera) {
    $receiver = Join-Path $root 'windows\receiver\x64\Release\StreamstheticReceiver.exe'
    $steps += "& '$receiver' --install-camera | Out-File '$env:TEMP\phonecam-install-camera.txt'"
}

$p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-Command', ($steps -join '; ')
Write-Output "elevated step exit code: $($p.ExitCode)"
Get-Service FrameServer, FrameServerMonitor | ForEach-Object { Write-Output "$($_.Name): $($_.Status)" }
$same = (Get-FileHash $built).Hash -eq (Get-FileHash "$target\VCamSampleSource.dll").Hash
Write-Output "deployed DLL matches build: $same"
if ($SystemCamera) { Get-Content "$env:TEMP\phonecam-install-camera.txt" -ErrorAction SilentlyContinue }
