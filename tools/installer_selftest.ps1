# Isolated install, upgrade, uninstall, and setup archive path tests. No camera/admin/live app changes.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'installer/install-preflight.ps1')
function Expect-Failure([scriptblock]$Action, [string]$Expected) {
    $failed = $false
    try { & $Action } catch { if ($_.Exception.Message -notlike "*$Expected*") { throw }; $failed = $true }
    if (-not $failed) { throw "Expected failure containing: $Expected" }
}
Assert-StreamstheticPlatform 22000 AMD64 $true
Expect-Failure { Assert-StreamstheticPlatform 19045 AMD64 $true } 'Windows 11'
Expect-Failure { Assert-StreamstheticPlatform 26100 ARM64 $true } 'Windows on ARM'
Expect-Failure { Assert-StreamstheticPlatform 26100 x86 $true } 'x64 Windows'
Expect-Failure { Assert-StreamstheticPlatform 26100 AMD64 $false } 'Media Feature Pack'
Assert-StreamstheticPlatform 26100 AMD64 $false $false
Assert-StreamstheticRuntimeSource $true $false $false
Assert-StreamstheticRuntimeSource $false $true $false
Assert-StreamstheticRuntimeSource $false $false $true
Expect-Failure { Assert-StreamstheticRuntimeSource $false $false $false } 'offline installer'
Expect-Failure { Install-StreamstheticRuntime { 1603 } { $true } } 'exit code 1603'
Expect-Failure { Install-StreamstheticRuntime { 0 } { $false } } 'still unavailable'
Install-StreamstheticRuntime { 0 } { $true }
Assert-StreamstheticCameraResult 0
Expect-Failure { Assert-StreamstheticCameraResult 7 } 'camera setup did not finish'
$canceled = Get-StreamstheticElevationFailure ([ComponentModel.Win32Exception]::new(1223))
$denied = Get-StreamstheticElevationFailure ([ComponentModel.Win32Exception]::new(5))
if ($canceled -notlike '*canceled at*' -or $denied -like '*approve*') { throw 'Elevation cancellation must not be confused with other Windows failures' }
try { Assert-StreamstheticCameraResult 5 } catch { if ($_.Exception.Message -like '*Approve*') { throw 'Registration failure incorrectly blames administrator approval' } }
Write-Output 'PASS: platform, media, runtime source, runtime exit/verification and camera failure decisions (no external actions)'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Streamsthetic-installer-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $testRoot 'package'
$target = Join-Path $testRoot "Owner's app & test"
$neighbor = $target + '-neighbor'
$menu = Join-Path $testRoot 'menu'
$desktop = Join-Path $testRoot 'desktop'
$key = 'HKCU:\Software\StreamstheticInstallerTests\' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $package, $neighbor, $desktop -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'installer\*') -Destination $package
Set-Content -LiteralPath (Join-Path $package 'Streamsthetic.exe') -Value 'first version'
# Dummy runtime markers ensure this isolated file test can never install a host runtime.
Set-Content -LiteralPath (Join-Path $package 'coreclr.dll') -Value 'test marker'
Set-Content -LiteralPath (Join-Path $package 'PresentationFramework.dll') -Value 'test marker'
Set-Content -LiteralPath (Join-Path $neighbor 'keep.txt') -Value 'neighbor'
$passed = $false
try {
    & (Join-Path $package 'install.ps1') -Target $target -StartMenu $menu -Desktop $desktop -UninstallKey $key -NoCamera -NoLaunch
    if (-not (Test-Path -LiteralPath (Join-Path $target 'installation.json'))) { throw 'Missing install receipt' }
    if (-not (Test-Path -LiteralPath (Join-Path $target 'install-preflight.ps1'))) { throw 'Installed package is missing the shared native capture helper' }
    if ((Get-ItemProperty -LiteralPath $key).InstallLocation -ne $target) { throw 'Incorrect install registration' }
    if (-not (Test-Path -LiteralPath (Join-Path $desktop 'Streamsthetic.lnk'))) { throw 'Missing shortcut' }
    Set-Content -LiteralPath (Join-Path $package 'Streamsthetic.exe') -Value 'second version'
    & (Join-Path $package 'install.ps1') -Target $target -StartMenu $menu -Desktop $desktop -UninstallKey $key -NoCamera -NoLaunch
    if ((Get-Content -LiteralPath (Join-Path $target 'Streamsthetic.exe') -Raw).Trim() -ne 'second version') { throw 'Upgrade did not replace application' }
    Write-Output 'PASS: install and upgrade with apostrophes, spaces and ampersand in path'
    # Replace only the test package's camera script: exercise child exit propagation without UAC or a camera.
    Set-Content -LiteralPath (Join-Path $package 'install-camera.ps1') -Value 'exit 7'
    Expect-Failure { & (Join-Path $package 'install.ps1') -Target $target -StartMenu $menu -Desktop $desktop -UninstallKey $key -NoLaunch } 'camera setup did not finish'
    Write-Output 'PASS: isolated installer rejects failed camera child before success/launch'
    # Exercise saved receipt paths instead of passing the alternate registry/shortcut locations again.
    & (Join-Path $target 'uninstall.ps1') -KeepSettings
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Test-Path -LiteralPath $target) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath $target) { throw 'Uninstaller did not remove its exact folder' }
    if (Test-Path -LiteralPath $key) { throw 'Uninstaller left its registration' }
    if (Test-Path -LiteralPath (Join-Path $desktop 'Streamsthetic.lnk')) { throw 'Uninstaller left its shortcut' }
    if (-not (Test-Path -LiteralPath (Join-Path $neighbor 'keep.txt'))) { throw 'Uninstaller damaged neighboring folder' }
    Write-Output 'PASS: uninstall removes only its verified target and its own shortcuts/registration'

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    foreach ($malicious in @($false, $true)) {
        $suffix = if ($malicious) { 'bad' } else { 'good' }
        $zip = Join-Path $testRoot "$suffix.zip"
        $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
        $entry = $archive.CreateEntry($(if ($malicious) { '../extract-bad-sibling/escape.txt' } else { 'nested/valid.txt' }))
        $writer = New-Object IO.StreamWriter($entry.Open())
        $writer.Write('test payload'); $writer.Dispose(); $archive.Dispose()
        $exe = Join-Path $testRoot "$suffix.exe"
        & $csc /nologo /target:winexe /out:$exe /resource:"$zip,payload.zip" /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Windows.Forms.dll "$root\tools\setup\Setup.cs"
        if ($LASTEXITCODE) { throw 'Setup test compilation failed' }
        $extract = Join-Path $testRoot "extract-$suffix"
        $process = Start-Process -FilePath $exe -ArgumentList '--extract-only', ('"' + $extract + '"') -PassThru -Wait -WindowStyle Hidden
        if ($malicious) {
            if ($process.ExitCode -eq 0 -or (Test-Path -LiteralPath (Join-Path $testRoot 'extract-bad-sibling/escape.txt'))) { throw 'Setup allowed archive path escape' }
        } elseif ($process.ExitCode -ne 0 -or (Get-Content -LiteralPath (Join-Path $extract 'nested/valid.txt')) -ne 'test payload') { throw 'Valid archive failed to extract' }
    }
    Write-Output 'PASS: setup extracts valid nested files and rejects sibling directory traversal'
    $failureFolder = Join-Path $testRoot 'failed install'
    New-Item -ItemType Directory $failureFolder | Out-Null
    Set-Content -LiteralPath (Join-Path $failureFolder 'install.ps1') -Value "throw 'Deliberate dependency failure for bootstrap test'"
    $testSource = Join-Path $testRoot 'SetupFailureTest.cs'
@'
using System;
using System.IO;
static class SetupFailureTest {
 static int Main(string[] args) {
  try { Setup.RunInstaller(args[0]); return 1; }
  catch (InvalidOperationException ex) {
   return ex.Message.Contains("Deliberate dependency failure") && ex.Message.Contains("exit code")
    && File.ReadAllText(Path.Combine(args[0], "setup-error.txt")).Contains("Deliberate dependency failure") ? 0 : 2;
  }
 }
}
'@ | Set-Content -LiteralPath $testSource
    $failureExe = Join-Path $testRoot 'SetupFailureTest.exe'
    & $csc /nologo /target:exe /main:SetupFailureTest /out:$failureExe /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Windows.Forms.dll "$root\tools\setup\Setup.cs" $testSource
    if ($LASTEXITCODE) { throw 'Bootstrap failure test compilation failed' }
    $process = Start-Process -FilePath $failureExe -ArgumentList ('"' + $failureFolder + '"') -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode) { throw 'Bootstrapper did not preserve failed-script details and non-success' }
    Write-Output 'PASS: bootstrapper captures failed-script error details and writes the diagnostic log'

    # Real native stdout/stderr and exit propagation, without camera APIs or elevation.
    $nativeSource = Join-Path $testRoot 'NativeFixture.cs'
@'
using System;
static class NativeFixture {
 static int Main(string[] args) {
  Console.WriteLine("NATIVE_STDOUT_BEGIN");
  Console.WriteLine(new string('x', 100000));
  Console.Error.WriteLine("NATIVE_STDERR_MARKER");
  if (args[0] == "hang") { Console.Error.WriteLine("PRE_TIMEOUT_HRESULT_0x80070005"); Console.Out.Flush(); Console.Error.Flush(); System.Threading.Thread.Sleep(30000); return 0; }
  if (args[0] == "success") { Console.Error.WriteLine("Harmless warning"); return 0; }
  Console.Error.WriteLine("Registration HRESULT 0x80070005"); return 5;
 }
}
'@ | Set-Content -LiteralPath $nativeSource
    $nativeExe = Join-Path $failureFolder 'NativeFixture.exe'
    & $csc /nologo /target:exe /out:$nativeExe $nativeSource
    if ($LASTEXITCODE) { throw 'Native fixture compilation failed' }
    $nativeLog = Join-Path $failureFolder 'camera.log'
    Invoke-StreamstheticNativeStep -FilePath $nativeExe -Arguments @('success') -Stage 'fixture registration' -LogPath $nativeLog
    $warningLog = Get-Content -LiteralPath $nativeLog -Raw
    if ($warningLog -notlike '*Harmless warning*' -or $warningLog -notlike '*Native exit code: 0*') { throw 'Successful stderr was lost or treated as failure' }
    Expect-Failure { Invoke-StreamstheticNativeStep -FilePath $nativeExe -Arguments @('failure') -Stage 'fixture registration' -LogPath $nativeLog } 'native exit code 5'
    $nativeDetails = Get-Content -LiteralPath $nativeLog -Raw
    foreach ($expected in @('NATIVE_STDOUT_BEGIN', 'NATIVE_STDERR_MARKER', '0x80070005', 'Native exit code: 5', 'Stage: fixture registration')) {
        if (-not $nativeDetails.Contains($expected)) { throw "Native log lost $expected" }
    }
    $timeoutLog = Join-Path $failureFolder 'timeout.log'
    $timeoutWatch = [Diagnostics.Stopwatch]::StartNew()
    Expect-Failure { Invoke-StreamstheticNativeStep -FilePath $nativeExe -Arguments @('hang') -Stage 'timeout fixture' -LogPath $timeoutLog -TimeoutMilliseconds 1000 } 'timed out'
    $timeoutDetails = Get-Content -LiteralPath $timeoutLog -Raw
    foreach ($expected in @('NATIVE_STDOUT_BEGIN', 'PRE_TIMEOUT_HRESULT_0x80070005', 'Timed out after 1000 ms', 'timed out=True')) {
        if (-not $timeoutDetails.Contains($expected)) { throw "Timeout log lost $expected" }
    }
    if ($timeoutWatch.Elapsed.TotalSeconds -gt 10) { throw 'Timeout fixture exceeded its bounded wait' }
    Write-Output 'PASS: timed-out native process preserves both output streams and timeout evidence'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'installer/install-preflight.ps1') -Destination $failureFolder
@'
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-preflight.ps1')
$log = Join-Path $PSScriptRoot 'camera.log'
try {
 Invoke-StreamstheticNativeStep -FilePath (Join-Path $PSScriptRoot 'NativeFixture.exe') -Arguments @('failure') -Stage 'virtual camera registration' -LogPath $log
} catch {
 Write-Warning (Get-Content -LiteralPath $log -Raw)
 Assert-StreamstheticCameraResult 1
}
'@ | Set-Content -LiteralPath (Join-Path $failureFolder 'install.ps1')
    $nativeHarnessSource = Join-Path $testRoot 'NativeSetupTest.cs'
@'
using System;
using System.IO;
static class NativeSetupTest {
 static int Main(string[] args) {
  try { Setup.RunInstaller(args[0]); return 1; }
  catch (InvalidOperationException ex) {
   var log = File.ReadAllText(Path.Combine(args[0], "setup-error.txt"));
   foreach (var token in new[] { "0x80070005", "NATIVE_STDOUT_BEGIN", "NATIVE_STDERR_MARKER", "Native exit code: 5", "virtual camera registration", "camera.log" })
    if (!log.Contains(token)) return 2;
   return ex.Message.Contains("0x80070005") && !log.Contains("Approve the Windows administrator prompt") ? 0 : 3;
  }
 }
}
'@ | Set-Content -LiteralPath $nativeHarnessSource
    $nativeHarness = Join-Path $testRoot 'NativeSetupTest.exe'
    & $csc /nologo /target:exe /main:NativeSetupTest /out:$nativeHarness /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Windows.Forms.dll "$root\tools\setup\Setup.cs" $nativeHarnessSource
    if ($LASTEXITCODE) { throw 'Native setup harness compilation failed' }
    $process = Start-Process -FilePath $nativeHarness -ArgumentList ('"' + $failureFolder + '"') -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Native capture propagation timed out' }
    if ($process.ExitCode) { throw "Native camera failure did not reach setup log and message (test exit $($process.ExitCode))" }
    Write-Output 'PASS: native stdout/stderr, HRESULT, stage, exit code and log path reach setup; successful stderr is not a failure'
    # A long-running child must not keep redirected installer pipes (and setup) alive.
    Set-Content -LiteralPath (Join-Path $failureFolder 'install.ps1') -Value "Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 8'; exit 0"
    $successSource = Join-Path $testRoot 'SetupSuccessTest.cs'
    Set-Content -LiteralPath $successSource -Value 'using System; using System.Diagnostics; static class SetupSuccessTest { static int Main(string[] args) { var time = Stopwatch.StartNew(); var result = Setup.RunInstaller(args[0]); return result == 0 && time.Elapsed.TotalSeconds < 6 ? 0 : 3; } }'
    $successExe = Join-Path $testRoot 'SetupSuccessTest.exe'
    & $csc /nologo /target:exe /main:SetupSuccessTest /out:$successExe /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Windows.Forms.dll "$root\tools\setup\Setup.cs" $successSource
    if ($LASTEXITCODE) { throw 'Bootstrap success test compilation failed' }
    $process = Start-Process -FilePath $successExe -ArgumentList ('"' + $failureFolder + '"') -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode) { throw 'Bootstrapper waited for a child launched by the installer or failed success' }
    Write-Output 'PASS: bootstrapper completes without waiting for the installed app lifetime'


    $passed = $true
} finally {
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
    # This absolute path was created above under TEMP; never remove an inferred installation location.
    if ($passed -and $testRoot.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    } else { Write-Output "Test files retained at $testRoot" }
}
Write-Output 'INSTALLER SELF-TEST PASS'
