# Live-settings test on the real phone: the receiver streams from the phone (USB by default, or -Wifi -Code) and
# sends a series of setting changes over the control channel without restarting anything. Prints the receiver
# output and the phone's log for each step. The phone app must already be streaming.
param([switch]$Wifi, [string]$Code = '', [int]$StepSeconds = 6)

$receiver = "$PSScriptRoot\..\windows\receiver\x64\Release\StreamstheticReceiver.exe"
$steps = @(
    'cam=0;zoom=0.6',          # lens via zoom: same session
    'cam=0;zoom=3',
    'mbps=40',                 # bitrate on the running encoder
    'cam=1;zoom=1',            # front camera: camera reopened, connection kept
    'cam=0;zoom=1',
    'w=1280;h=720',            # new size: encoder rebuilt, viewer reconnects
    'w=1920;h=1080;fps=30',
    'fps=60;mbps=20',
    'w=9999;h=1'               # invalid: must be refused, stream unchanged
)
$seconds = ($steps.Count + 2) * $StepSeconds
$arguments = "--no-camera --seconds $seconds" + $(if ($Wifi) { " --wifi --code $Code" } else { '' })

adb logcat -c
$psi = [Diagnostics.ProcessStartInfo]::new($receiver, $arguments)
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$proc = [Diagnostics.Process]::Start($psi)
$reading = $proc.StandardOutput.ReadToEndAsync()

Start-Sleep -Seconds ($StepSeconds * 2)
foreach ($step in $steps) {
    "{0:HH:mm:ss.fff} set $step" -f (Get-Date)
    $proc.StandardInput.WriteLine("set $step")
    $proc.StandardInput.Flush()
    Start-Sleep -Seconds $StepSeconds
}
$proc.WaitForExit(($StepSeconds + 10) * 1000) | Out-Null
'--- receiver ---'
$reading.Result -split "`r?`n" | Where-Object { $_ -match 'phone settings|fps written|found|receiving|stream ended|waiting|frame packets|video |GPU|not sent|fell' }
'--- phone ---'
adb logcat -d -s PhoneCam | Select-String 'apply live|refused|SESSION STREAMING|CAMERA|client connected|client dropped|settings from PC|encoder [0-9.]+ fps' |
    ForEach-Object { $_.Line -replace '^\S+ (\S+)\s+\d+\s+\d+ \w PhoneCam: ', '$1 ' }
