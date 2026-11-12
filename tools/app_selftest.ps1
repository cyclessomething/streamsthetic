# Headless check of the PhoneCam app: fake phone -> app (--selftest, Wi-Fi discovery + pairing) -> PhoneCam camera
# opened by ffmpeg like an app would. Passes when the app reports live video at >= 50 fps.
param([string]$Recording = "$PSScriptRoot\testdata\phone-1080p60-10s.pcm1")

$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'windows\app\bin\Release\net10.0-windows\Streamsthetic.exe'
$result = Join-Path $env:TEMP 'phonecam-app-selftest.txt'
Remove-Item $result -ErrorAction SilentlyContinue

$fake = Start-Process python -ArgumentList "`"$PSScriptRoot\fake_phone.py`" --file `"$Recording`" --port 5599 --code 123456 --seconds 40" -PassThru -WindowStyle Hidden
Start-Sleep 2
$a = Start-Process $app -ArgumentList "--selftest `"$result`" -- --wifi --discover-to 127.0.0.1 --code 123456" -PassThru
Start-Sleep 3
# the receiver only connects to the phone once an app has opened the camera
$ff = Start-Process ffmpeg -ArgumentList '-hide_banner -loglevel error -f dshow -i "video=Streamsthetic (Windows Virtual Camera)" -t 20 -f null -' -PassThru -WindowStyle Hidden
if (-not $a.WaitForExit(35000)) { $a.Kill(); Write-Output 'app TIMEOUT' }
else { Write-Output "app self-test exit code: $($a.ExitCode)" }
Stop-Process $ff -Force -ErrorAction SilentlyContinue
Stop-Process $fake -Force -ErrorAction SilentlyContinue
Get-Content $result -ErrorAction SilentlyContinue
