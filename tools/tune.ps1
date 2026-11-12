# Runs the encoder/camera tuning experiments on the connected phone and prints one table.
# Usage: powershell -File tools\tune.ps1 [-Names base,hevc] [-Seconds 15]
# Each experiment: restart the app with the launch extras, run tools\probe.py, parse A/B medians.
param(
    [string[]]$Names = @(),
    [int]$Seconds = 15
)

$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "docs\tune-results.txt"

$common = @('--es', 'cam', '0', '--ei', 'w', '1920', '--ei', 'h', '1080', '--ei', 'fps', '60', '--ei', 'mbps', '20', '--es', 'codec', 'h264')
function Cfg($name, $overrides) { [pscustomobject]@{ Name = $name; Args = $overrides } }

# An override replaces the matching default; anything else is appended.
$experiments = @(
    (Cfg 'baseline'      @()),
    (Cfg 'profile=base'  @('--es', 'profile', 'base')),
    (Cfg 'bframes=0'     @('--ei', 'bframes', '0')),
    (Cfg 'oprate=120'    @('--ei', 'oprate', '120')),
    (Cfg 'qlat=1'        @('--ei', 'qlat', '1')),
    (Cfg 'lat=0'         @('--ei', 'lat', '0')),
    (Cfg 'isp=fast'      @('--es', 'isp', 'fast')),
    (Cfg 'isp=off'       @('--es', 'isp', 'off')),
    (Cfg 'br=vbr'        @('--es', 'br', 'vbr')),
    (Cfg 'hevc'          @('--es', 'codec', 'hevc')),
    (Cfg '720p60'        @('--ei', 'w', '1280', '--ei', 'h', '720')),
    (Cfg '1080p30'       @('--ei', 'fps', '30')),
    (Cfg 'baseline-2'    @()),
    (Cfg 'combo'         @('--ei', 'oprate', '120', '--es', 'profile', 'base')),
    # camera-side experiments
    (Cfg 'tmpl=preview'  @('--es', 'tmpl', 'preview')),
    (Cfg 'uc=preview'    @('--es', 'usecase', 'preview')),
    (Cfg 'uc=video_call' @('--es', 'usecase', 'video_call')),
    (Cfg 'uc=record'     @('--es', 'usecase', 'record')),
    (Cfg 'exp=8ms'       @('--ei', 'exp_us', '8000', '--ei', 'iso', '1600')),
    (Cfg 'exp=4ms'       @('--ei', 'exp_us', '4000', '--ei', 'iso', '3200')),
    (Cfg 'exp=2ms'       @('--ei', 'exp_us', '2000', '--ei', 'iso', '6400'))
)
# powershell -File passes "a,b" as one string, so split it here
$Names = @($Names | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Names.Count) { $experiments = $experiments | Where-Object { $Names -contains $_.Name } }

function Merge-Args($defaults, $overrides) {
    $map = [ordered]@{}
    for ($i = 0; $i -lt $defaults.Count; $i += 3) { $map[$defaults[$i + 1]] = @($defaults[$i], $defaults[$i + 1], $defaults[$i + 2]) }
    for ($i = 0; $i -lt $overrides.Count; $i += 3) { $map[$overrides[$i + 1]] = @($overrides[$i], $overrides[$i + 1], $overrides[$i + 2]) }
    $map.Values | ForEach-Object { $_ }
}

function Parse-Stat($text, $label) {
    $line = $text | Where-Object { $_ -like "$label*" } | Select-Object -First 1
    if ($line -match 'min\s+([\d.]+)\s+median\s+([\d.]+)\s+p95\s+([\d.]+)\s+p99\s+([\d.]+)\s+max\s+([\d.]+)') {
        return [pscustomobject]@{ Min = [double]$Matches[1]; Median = [double]$Matches[2]; P95 = [double]$Matches[3]; Max = [double]$Matches[5] }
    }
    return $null
}

$rows = foreach ($e in $experiments) {
    & $adb shell am force-stop com.phonecam | Out-Null
    & $adb shell input keyevent KEYCODE_WAKEUP | Out-Null
    & $adb shell input keyevent KEYCODE_HOME | Out-Null
    Start-Sleep -Milliseconds 800
    & $adb forward tcp:5555 localabstract:phonecam | Out-Null
    & $adb logcat -c
    $launch = Merge-Args $common $e.Args
    & $adb shell am start -n com.phonecam/.MainActivity @launch | Out-Null
    Start-Sleep -Seconds 4
    $text = python -I (Join-Path $PSScriptRoot 'probe.py') --seconds $Seconds 2>&1
    $a = Parse-Stat $text 'A capture'
    $b = Parse-Stat $text 'B encoded'
    $gap = Parse-Stat $text 'capture interval'
    $fps = if (($text -join "`n") -match '\(([\d.]+) fps\)') { $Matches[1] } else { '?' }
    $log = & $adb logcat -d -s PhoneCam:V
    $encoder = ($log | Select-String 'encoder (c2\.\S+|OMX\.\S+) in=' | Select-Object -First 1)
    $encName = if ($encoder -match 'encoder (\S+) in=') { $Matches[1] } else { '?' }
    $cam = $log | Select-String 'camera result latency avg ([\d.]+) ms exposure avg ([\d.]+) ms pipelineDepth max (\d+)'
    $camRes = if ($cam) { [math]::Round(($cam | ForEach-Object { [double]$_.Matches[0].Groups[1].Value } | Measure-Object -Average).Average, 1) } else { '' }
    $camExp = if ($cam) { [math]::Round(($cam | ForEach-Object { [double]$_.Matches[0].Groups[2].Value } | Measure-Object -Average).Average, 2) } else { '' }
    $camDepth = if ($cam) { ($cam | ForEach-Object { [int]$_.Matches[0].Groups[3].Value } | Measure-Object -Maximum).Maximum } else { '' }
    [pscustomobject]@{
        Config   = $e.Name
        Encoder  = $encName
        Fps      = $fps
        A_med    = if ($a) { $a.Median } else { 'FAIL' }
        A_p95    = if ($a) { $a.P95 } else { '' }
        A_max    = if ($a) { $a.Max } else { '' }
        B_med    = if ($b) { $b.Median } else { '' }
        CamRes   = $camRes
        ExpMs    = $camExp
        Depth    = $camDepth
        MaxGapMs = if ($gap) { $gap.Max } else { '' }
    }
}
& $adb shell am force-stop com.phonecam | Out-Null
& $adb forward --remove tcp:5555 2>$null | Out-Null

$table = $rows | Format-Table -AutoSize | Out-String
$table
$table | Set-Content -Path $out -Encoding utf8
