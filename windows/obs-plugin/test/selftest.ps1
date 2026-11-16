# End-to-end check of the phone-microphone path into OBS, without OBS or a phone:
#   test stream (ramp samples) -> real StreamstheticReceiver.exe --replay -> shared memory -> real plugin DLL -> fake obs.dll
# Passes when the plugin hands over the right format, at least most of the samples, and no gaps.
# Run after windows\obs-plugin\build.ps1 and the receiver build.
param([int]$Camera = 4, [int]$Seconds = 3)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$plugin = Join-Path $here '..\x64\Release\streamsthetic-mic.dll'
$receiver = Join-Path $here '..\..\receiver\x64\Release\StreamstheticReceiver.exe'
foreach ($f in $plugin, $receiver) { if (-not (Test-Path $f)) { throw "missing $f (build it first)" } }
$libobs = (Resolve-Path (Join-Path $here '..\..\third_party\obs-studio\libobs')).Path
$work = Join-Path $env:TEMP "streamsthetic-obs-selftest-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    # 1. test doubles: fake obs.dll and the host, next to a copy of the plugin
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -products '*' -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $flags = "/nologo /O2 /W4 /WX /wd4201 /MD /I`"$($here)\..`" /I`"$libobs`""
    Set-Content -LiteralPath "$work\build.cmd" -Encoding ASCII -Value @(
        "@call `"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul",
        "cd /d `"$work`"",
        "cl $flags /LD `"$here\fake_obs.c`" /Fe:obs.dll || exit /b 1",
        "cl $flags `"$here\test_host.c`" /Fe:test_host.exe || exit /b 1")
    & cmd /c "`"$work\build.cmd`"" | Out-Null
    if ($LASTEXITCODE) { throw 'test doubles did not build' }
    Copy-Item -LiteralPath $plugin -Destination $work

    # 2. a recorded stream: PCM1 audio packets, 10 ms each (480 samples), ramp values, pts in microseconds
    $packets = $Seconds * 100
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    function Be([long]$value, [int]$bytes) { for ($b = $bytes - 1; $b -ge 0; $b--) { $writer.Write([byte](($value -shr (8 * $b)) -band 0xFF)) } }
    $sample = 0
    for ($p = 0; $p -lt $packets; $p++) {
        Be 0x50434D31 4; $writer.Write([byte]5); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
        Be 960 4; Be ([long]$p * 10000) 8; Be 0 8
        for ($i = 0; $i -lt 480; $i++) { $writer.Write([int16]$sample); $sample = ($sample + 1) -band 0x7FFF }
    }
    [IO.File]::WriteAllBytes("$work\tone.pcm1", $stream.ToArray())

    # 3. the plugin listens (camera $Camera) while the receiver replays the stream in real time
    $listener = Start-Process -FilePath "$work\test_host.exe" -ArgumentList $Camera, ($Seconds + 3), ([int](48000 * $Seconds * 0.8)) `
        -WorkingDirectory $work -NoNewWindow -PassThru -RedirectStandardOutput "$work\host.txt"
    $null = $listener.Handle # without this PowerShell forgets the exit code
    # a second source set to another camera must stay silent (each phone's sound stays with its own camera)
    $other = $Camera % 4 + 1
    $bystander = Start-Process -FilePath "$work\test_host.exe" -ArgumentList $other, ($Seconds + 3), 0 `
        -WorkingDirectory $work -NoNewWindow -PassThru -RedirectStandardOutput "$work\other.txt"
    $null = $bystander.Handle
    Start-Sleep -Milliseconds 1200
    & $receiver --replay "$work\tone.pcm1" --realtime --no-camera --slot $Camera *> "$work\receiver.txt"
    $listener.WaitForExit()
    $bystander.WaitForExit()
    Get-Content "$work\receiver.txt" | Select-String 'audio:' | ForEach-Object { "receiver: $_" }
    Get-Content "$work\host.txt"
    if ($listener.ExitCode -ne 0) { throw "OBS PLUGIN SELF-TEST FAIL (host exit $('{0:X}' -f $listener.ExitCode))" }
    $quiet = Get-Content "$work\other.txt" | Select-String 'received 0 samples'
    Write-Output "camera ${other}: $(if ($quiet) { 'silent, as expected' } else { 'NOT silent' })"
    if ($bystander.ExitCode -ne 0 -or -not $quiet) { throw 'OBS PLUGIN SELF-TEST FAIL (sound reached another camera)' }
    Write-Output 'OBS PLUGIN SELF-TEST PASS'
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
