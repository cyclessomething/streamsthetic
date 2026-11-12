# Picture-controls test on the real phone (USB): each step sends a control over the live-settings channel, then
# measures a decoded frame (mean luma Y, chroma U/V) so the effect is checked, not assumed. Uses the receiver's own
# frame dump (--dump), so it works even while OBS or another app has the PhoneCam camera open.
# The phone app must be streaming. Keep the phone still while it runs.
$receiver = "$PSScriptRoot\..\windows\receiver\x64\Release\StreamstheticReceiver.exe"
$dump = Join-Path $env:TEMP 'phonecam-picture-test.nv12'

function Step($set) {
    Remove-Item $dump -ErrorAction SilentlyContinue
    $psi = [Diagnostics.ProcessStartInfo]::new($receiver, "--no-camera --seconds 7 --dump `"$dump`"")
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true
    $proc = [Diagnostics.Process]::Start($psi)
    $reading = $proc.StandardOutput.ReadToEndAsync()
    Start-Sleep -Milliseconds 1500
    if ($set) { $proc.StandardInput.WriteLine("set $set"); $proc.StandardInput.Flush() }
    $proc.WaitForExit(15000) | Out-Null
    $settings = ($reading.Result -split "`r?`n" | Where-Object { $_ -like 'phone settings*' } | Select-Object -Last 1)
    # frame 300 (~5 s in, ~3.5 s after the change): NV12 1920x1080
    $stats = python -I -c @"
import sys
d = open(sys.argv[1], 'rb').read(); n = 1920 * 1080
y = d[:n]; uv = d[n:n + n // 2]
print('Y %6.1f U %6.1f V %6.1f' % (sum(y[::7]) / len(y[::7]), sum(uv[0::14]) / len(uv[0::14]), sum(uv[1::14]) / len(uv[1::14])))
"@ $dump 2>&1
    "{0,-14} {1}   {2}" -f $(if ($set) { $set } else { 'baseline' }), $stats, ($settings -replace '.*(ev=-?\d+).*(focus=\w+;wb=\w+;flicker=\w+).*(zoom=)?.*', '$1 $2')
}

adb logcat -c
Step $null
Step 'ev=-20'
Step 'ev=20'
Step 'ev=0'
Step 'wb=tungsten'
Step 'wb=daylight'
Step 'wb=auto'
Step 'flicker=50'
Step 'zoom=2'
Step 'zoom=7'
Step 'zoom=1'
Step 'focus=lock'
Step 'focus=auto'
Step 'wb=pink'
'--- phone ---'
adb logcat -d -s PhoneCam | Select-String -CaseSensitive 'apply live|refused|CAMERA ERROR|rebuilding' | ForEach-Object { $_.Line -replace '^\S+ (\S+)\s+\d+\s+\d+ \w PhoneCam: ', '$1 ' }
