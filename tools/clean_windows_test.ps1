# GUEST ONLY: all guards precede writes and process launches. Never source this on the host.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Token)
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $env:USERPROFILE -ne 'C:\Users\WDAGUtilityAccount' -or
    [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne 'C:\StreamstheticTest\Input') {
    throw 'Guest guard refused: run only through the prepared Windows Sandbox configuration.'
}
$computer = Get-CimInstance Win32_ComputerSystem
if ($computer.Manufacturer -ne 'Microsoft Corporation' -or $computer.Model -ne 'Virtual Machine' -or -not $computer.HypervisorPresent) {
    throw 'Guest guard refused: expected the Microsoft virtual-machine environment.'
}
$config = Get-Content -LiteralPath 'C:\StreamstheticTest\Input\test-input.json' -Raw | ConvertFrom-Json
if ($Token -notmatch '^[a-f0-9]{32}$' -or $config.Token -cne $Token -or $config.Schema -ne 1) { throw 'Guest guard refused: invalid run marker.' }
$output = 'C:\StreamstheticTest\Output'
if (-not (Test-Path -LiteralPath $output -PathType Container)) { throw 'Guest guard refused: evidence mapping missing.' }
$setup = 'C:\StreamstheticTest\Input\Setup-offline.exe'
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $config.SetupSHA256) { throw 'Guest guard refused: installer hash differs from prepared input.' }
$result = [ordered]@{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); FinishedUtc = $null; Scope = 'Offline fresh Sandbox installation and UI launch only'
    Overall = 'INCOMPLETE'; SetupSHA256 = $config.SetupSHA256; Checks = [ordered]@{}
    Pending = @('Media Foundation camera enumeration and frame delivery', 'Physical Android phone and USB/Wi-Fi streaming', 'Upgrade', 'Uninstall and camera removal', 'Real clean-PC hardware validation')
}
function Save-Result { $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8 }
function Record-Check($name, $status, $detail) {
    $result.Checks[$name] = [ordered]@{ Status = $status; Detail = $detail }; Save-Result
}
function Wait-Bounded($process, [int]$seconds) {
    $until = [DateTime]::UtcNow.AddSeconds($seconds)
    while (-not $process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $until) {
            & "$env:WINDIR\System32\taskkill.exe" /PID $process.Id /T /F 2>&1 | Out-File (Join-Path $output 'timeout-processes.txt') -Append
            throw "Process timed out after $seconds seconds. A setup error dialog or administrator prompt may need attention; see retained evidence."
        }
    }
    return $process.ExitCode
}
function Read-Cameras {
    # PnP inventory is independent evidence, not proof that an MF virtual camera produces frames.
    @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -in @('Camera', 'Image') -or $_.Name -like '*Streamsthetic*' } |
        Select-Object Name, PNPClass, DeviceID, Status, ConfigManagerErrorCode)
}
Save-Result
try {
    $windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $arch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
    $dotnet = Join-Path $env:ProgramFiles 'dotnet'
    $baseline = [ordered]@{
        Product = $windows.ProductName; Build = $windows.CurrentBuildNumber; UBR = $windows.UBR; NativeArchitecture = $arch
        Manufacturer = $computer.Manufacturer; Model = $computer.Model
        DotnetSDKs = @(Get-ChildItem (Join-Path $dotnet 'sdk') -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        DesktopRuntimes = @(Get-ChildItem (Join-Path $dotnet 'shared\Microsoft.WindowsDesktop.App') -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        CoreRuntimes = @(Get-ChildItem (Join-Path $dotnet 'shared\Microsoft.NETCore.App') -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        DotnetOnPath = [bool](Get-Command dotnet.exe -ErrorAction SilentlyContinue)
        MediaFoundation = @('mfplat.dll', 'mfsensorgroup.dll', 'mf.dll' | ForEach-Object { [ordered]@{ Name = $_; Present = (Test-Path (Join-Path "$env:WINDIR\System32" $_)) } })
        Cameras = @(Read-Cameras)
    }
    Record-Check 'Baseline' 'OBSERVED' $baseline
    if ([int]$baseline.Build -lt 22000 -or $arch -ne 'AMD64') { throw 'Requires Windows 11 x64 Sandbox.' }
    $missingMedia = @($baseline.MediaFoundation | Where-Object { -not $_.Present } | ForEach-Object { $_.Name })
    if ($missingMedia.Count -gt 0) {
        $result.Overall = 'UNSUPPORTED_MEDIA_FOUNDATION'
        Record-Check 'MediaFoundationPrerequisite' 'UNSUPPORTED_GUEST' @{ Missing = $missingMedia; InstallerLaunched = $false }
        throw ('Sandbox lacks required Media Foundation components: ' + ($missingMedia -join ', ') + '. This environment cannot establish camera installation compatibility; it is not a product failure.')
    }
    Record-Check 'MediaFoundationPrerequisite' 'PASS' 'Required Media Foundation DLLs are present; camera functionality is still unproven.'
    $clean = $baseline.DotnetSDKs.Count -eq 0 -and $baseline.DesktopRuntimes.Count -eq 0 -and $baseline.CoreRuntimes.Count -eq 0 -and -not $baseline.DotnetOnPath
    Record-Check 'NoPreinstalledDotnet' $(if ($clean) { 'PASS' } else { 'NOT_CLEAN' }) 'Baseline inventories above establish the observed runtime/SDK state.'
    Record-Check 'Installer' 'RUNNING' 'Executing the real offline Setup EXE, without test bypass switches. Four-minute timeout includes any administrator prompt.'
    $installer = Start-Process -FilePath $setup -PassThru -WindowStyle Hidden
    $exit = Wait-Bounded $installer 240
    Record-Check 'Installer' $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' }) @{ ExitCode = $exit }
    if ($exit -ne 0) { throw "Offline installer returned $exit." }
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\Streamsthetic'
    $app = Join-Path $installed 'Streamsthetic.exe'
    $hash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
    $version = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
    $match = $hash -eq $config.ExpectedAppSHA256 -and $version -eq $config.ExpectedAppVersion
    Record-Check 'InstalledAppIdentity' $(if ($match) { 'PASS' } else { 'FAIL' }) @{ SHA256 = $hash; Version = $version; ExpectedSHA256 = $config.ExpectedAppSHA256; ExpectedVersion = $config.ExpectedAppVersion }
    if (-not $match) { throw 'Installed application identity differs from the staged release.' }
    Record-Check 'CameraPnPAfter' 'OBSERVED_NOT_FRAME_PROOF' @(Read-Cameras)
    $adb = Start-Process -FilePath (Join-Path $installed 'adb.exe') -ArgumentList 'version' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'adb-version.txt') -RedirectStandardError (Join-Path $output 'adb-error.txt')
    $adbExit = Wait-Bounded $adb 20
    Record-Check 'BundledAdb' $(if ($adbExit -eq 0) { 'PASS' } else { 'FAIL' }) @{ ExitCode = $adbExit; Output = 'adb-version.txt' }
    # Close only guest-installed app processes so the snapshot measures a fresh launch.
    Get-Process Streamsthetic -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app } | Stop-Process -Force
    $snapshot = Join-Path $output 'installed-app.png'
    $render = Start-Process -FilePath $app -ArgumentList '--snapshot', $snapshot -WorkingDirectory $installed -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'app-output.txt') -RedirectStandardError (Join-Path $output 'app-error.txt')
    $renderExit = Wait-Bounded $render 60
    $imageExists = (Test-Path -LiteralPath $snapshot) -and (Get-Item -LiteralPath $snapshot).Length -gt 1000
    $rendered = $renderExit -eq 0 -and $imageExists
    Record-Check 'InstalledAppSnapshot' $(if ($rendered) { 'PASS' } else { 'FAIL' }) @{ ExitCode = $renderExit; Image = 'installed-app.png'; RequiresVisualReview = $true }
    $result.Overall = if ($clean -and $match -and $adbExit -eq 0 -and $rendered) { 'PARTIAL_PASS_CAMERA_AND_HARDWARE_PENDING' } else { 'INCOMPLETE_OR_FAILED_CHECKS' }
} catch {
    if ($result.Overall -eq 'UNSUPPORTED_MEDIA_FOUNDATION') {
        Record-Check 'EnvironmentLimitation' 'BLOCKED' ($_ | Out-String)
    } else {
        $result.Overall = 'FAILED_OR_BLOCKED'
        Record-Check 'Failure' 'FAIL' ($_ | Out-String)
    }
} finally {
    # Keep installer-generated errors without exporting unrelated guest directories.
    Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'Streamsthetic-setup-*' -ErrorAction SilentlyContinue | ForEach-Object {
        $log = Join-Path $_.FullName 'setup-error.txt'
        if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination (Join-Path $output ($_.Name + '-error.txt')) }
    }
    $result.FinishedUtc = [DateTime]::UtcNow.ToString('o'); Save-Result
}
