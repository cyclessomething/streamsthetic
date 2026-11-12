# End-to-end check without the phone: replays a recorded PCM1 stream through StreamstheticReceiver.exe into the
# PhoneCam virtual camera while ffmpeg reads the camera like an app would.
# Prints the receiver stats, the camera reader's frame count/fps, and saves one frame read back from the camera.
# Needs: the PhoneCam media source registered (windows/vcam), ffmpeg on PATH.
# By default the receiver registers the camera itself; -UseHelper uses windows/vcam's VCamSample.exe instead.
param(
    [string]$Recording = "$PSScriptRoot\testdata\phone-1080p60-10s.pcm1",
    [string]$Frame = "$env:TEMP\phonecam-e2e-frame.png",
    [int]$CaptureSeconds = 14,
    [switch]$UseHelper
)

$root = Split-Path $PSScriptRoot -Parent
$receiver = Join-Path $root 'windows\receiver\x64\Release\StreamstheticReceiver.exe'
$helper = 'C:\ProgramData\PhoneCam\vcam\VCamSample.exe'
$cameraName = 'Streamsthetic (Windows Virtual Camera)'

if ($UseHelper) {
    if (-not (Get-Process VCamSample -ErrorAction SilentlyContinue)) {
        Start-Process $helper
        Start-Sleep 3
    }
}
else {
    # two registrations of the same camera would show up as two devices
    Get-Process VCamSample -ErrorAction SilentlyContinue | Stop-Process -Force
}
Remove-Item $Frame -ErrorAction SilentlyContinue
$ffErr = Join-Path $env:TEMP 'phonecam-e2e-ffmpeg.txt'
$recvOut = Join-Path $env:TEMP 'phonecam-e2e-receiver.txt'

# the receiver registers the camera (unless -UseHelper) and waits until an app opens it
$recvArgs = "--replay `"$Recording`" --realtime" + $(if ($UseHelper) { ' --no-register' } else { '' })
$r = Start-Process $receiver -ArgumentList $recvArgs -RedirectStandardOutput $recvOut -PassThru -WindowStyle Hidden
Start-Sleep 3

# the camera name contains spaces, so build one argument string with explicit quotes
$ffArgs = "-hide_banner -f dshow -rtbufsize 256M -i `"video=$cameraName`" -t $CaptureSeconds -f null - " +
          "-vf `"select=eq(n\,500)`" -frames:v 1 -y `"$Frame`""
$ff = Start-Process ffmpeg -ArgumentList $ffArgs -RedirectStandardError $ffErr -PassThru -WindowStyle Hidden
if (-not $r.WaitForExit(40000)) { $r.Kill(); Write-Output 'receiver TIMEOUT (camera never opened?)' }
else { Write-Output "receiver exit: $($r.ExitCode)" }
[void]$ff.WaitForExit(30000)

Write-Output '--- receiver:'
Get-Content $recvOut
Write-Output '--- camera reader (ffmpeg):'
Get-Content $ffErr | Select-String 'frame=.*fps=|Error|error' | Select-Object -Last 2 | ForEach-Object { $_.Line }
Write-Output "frame read back from camera: $(Test-Path $Frame) ($Frame)"
