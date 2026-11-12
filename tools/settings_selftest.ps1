# Offline test of the live-settings channel: fake phone <-> StreamstheticReceiver.exe.
# The receiver must print the phone's settings on connect, forward a "set ..." line from stdin to the phone, and
# print the phone's updated settings. Exit 0 = PASS.
param([string]$Receiver = "$PSScriptRoot\..\windows\receiver\x64\Release\StreamstheticReceiver.exe")

$recording = "$PSScriptRoot\testdata\phone-1080p60-10s.pcm1"
$fakeLog = Join-Path $env:TEMP 'phonecam-fake-settings.txt'
$fake = Start-Process python -ArgumentList "`"$PSScriptRoot\fake_phone.py`" --file `"$recording`" --port 5598 --code 123456 --seconds 20" `
    -NoNewWindow -PassThru -RedirectStandardOutput $fakeLog
Start-Sleep -Seconds 2

$psi = [Diagnostics.ProcessStartInfo]::new($Receiver, '--host 127.0.0.1 --port 5598 --code 123456 --no-camera --seconds 8')
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$proc = [Diagnostics.Process]::Start($psi)  # not $receiver: PowerShell names are case-insensitive and that one is a [string]
$reading = $proc.StandardOutput.ReadToEndAsync()

Start-Sleep -Seconds 3
$proc.StandardInput.WriteLine('set cam=0;zoom=0.6;mbps=40')
$proc.StandardInput.Flush()
$proc.WaitForExit(15000) | Out-Null
Stop-Process -Id $fake.Id -ErrorAction SilentlyContinue

$out = @($reading.Result -split "`r?`n")
$settings = $out | Where-Object { $_ -like 'phone settings:*' }
$settings | ForEach-Object { "receiver: $_" }
Get-Content $fakeLog | Where-Object { $_ -like 'settings from PC*' } | ForEach-Object { "fake phone: $_" }
$ok = ($settings.Count -ge 2) -and ($settings[0] -like '*zoom=1.00*mbps=20*') -and ($settings[-1] -like '*zoom=0.6*mbps=40*') -and
    (Select-String -Path $fakeLog -Pattern 'settings from PC: cam=0;zoom=0.6;mbps=40' -Quiet)
"frames: " + (($out | Where-Object { $_ -like 'frame packets*' }) -join '')
if ($ok) { 'SETTINGS SELF-TEST PASS'; exit 0 } else { 'SETTINGS SELF-TEST FAIL'; $out | Select-Object -Last 15; exit 2 }
