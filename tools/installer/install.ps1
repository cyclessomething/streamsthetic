# Streamsthetic installer: the app installs for your account; camera setup needs administrator approval.
#   Right-click > Run with PowerShell, or:  powershell -ExecutionPolicy Bypass -File install.ps1
# Copies the app to %LOCALAPPDATA%\Programs\Streamsthetic, adds a Start Menu shortcut and an entry in Windows'
# "Installed apps" list (Uninstall), then installs the Windows camera component.
# The switches below exist for tests (install into a temp folder without touching the real Start Menu / registry).
param(
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\Streamsthetic'),
    [string]$StartMenu = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [string]$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Streamsthetic',
    [string]$Desktop = [Environment]::GetFolderPath('Desktop'),
    [switch]$NoCamera,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$Target = [IO.Path]::GetFullPath($Target).TrimEnd('\')
if ($Target -eq [IO.Path]::GetPathRoot($Target).TrimEnd('\')) { throw 'Choose a dedicated Streamsthetic installation folder.' }
$targetPrefix = $Target + '\'
$sourcePath = [IO.Path]::GetFullPath($source).TrimEnd('\')
if ($Target.StartsWith($sourcePath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose an installation folder outside the extracted package.' }
if ((Test-Path -LiteralPath $Target) -and ((Get-Item -LiteralPath $Target).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Choose a standard folder for installation. Folder links are not supported.' }
if (-not (Test-Path (Join-Path $source 'Streamsthetic.exe'))) { throw "A setup file is missing: Streamsthetic.exe. Extract the complete package and run setup again. Package folder: $source" }

# Check compatibility and prerequisites before stopping the current app or copying any files.
. (Join-Path $source 'install-preflight.ps1')
$windowsInfo = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$nativeArchitecture = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
$systemDirectory = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { Join-Path $env:WINDIR 'Sysnative' } else { Join-Path $env:WINDIR 'System32' }
$mediaAvailable = (Test-Path (Join-Path $systemDirectory 'mfplat.dll')) -and (Test-Path (Join-Path $systemDirectory 'mfsensorgroup.dll'))
Assert-StreamstheticPlatform -Build ([int]$windowsInfo.CurrentBuildNumber) -Architecture $nativeArchitecture -MediaFoundationAvailable $mediaAvailable -NeedsCamera (-not $NoCamera)
$programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
function Test-DesktopRuntime {
    $desktop = Join-Path $programFiles64 'dotnet\shared\Microsoft.WindowsDesktop.App'
    $core = Join-Path $programFiles64 'dotnet\shared\Microsoft.NETCore.App'
    $desktopFound = @(Get-ChildItem $desktop -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^10\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'PresentationFramework.dll')) }).Count -gt 0
    $coreFound = @(Get-ChildItem $core -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^10\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'coreclr.dll')) }).Count -gt 0
    return $desktopFound -and $coreFound
}
$selfContained = (Test-Path (Join-Path $source 'coreclr.dll')) -and (Test-Path (Join-Path $source 'PresentationFramework.dll'))
$runtimeAvailable = Test-DesktopRuntime
$winget = Get-Command winget.exe -ErrorAction SilentlyContinue
Assert-StreamstheticRuntimeSource -SelfContained $selfContained -RuntimeAvailable $runtimeAvailable -WingetAvailable ($null -ne $winget)
if (-not $selfContained -and -not $runtimeAvailable) {
    Write-Output 'Installing Microsoft .NET 10 Desktop Runtime. Windows may ask for administrator approval.'
    Install-StreamstheticRuntime -Install {
        & $winget.Source install --id Microsoft.DotNet.DesktopRuntime.10 -e --architecture x64 --accept-package-agreements --accept-source-agreements | Out-Host
        return $LASTEXITCODE
    } -IsAvailable { Test-DesktopRuntime }
}

# a running copy would keep its files locked
Get-Process Streamsthetic, StreamstheticReceiver, PhoneCam, PhoneCamReceiver, adb -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $Target | Out-Null
if ((Resolve-Path $source).Path -ne (Resolve-Path $Target).Path) {
    Copy-Item (Join-Path $source '*') $Target -Recurse -Force
}
# The uninstaller verifies this receipt before removing the exact installation folder.
[ordered]@{ Target = $Target; StartMenu = $StartMenu; Desktop = $Desktop; UninstallKey = $UninstallKey } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Target 'installation.json') -Encoding UTF8

# Start Menu shortcut
New-Item -ItemType Directory -Force $StartMenu | Out-Null
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path $StartMenu 'Streamsthetic.lnk'))
$link.TargetPath = Join-Path $Target 'Streamsthetic.exe'
$link.WorkingDirectory = $Target
$link.Description = 'Use your phone as a Windows webcam'
$link.Save()
if ($Desktop -and (Test-Path $Desktop)) {
    $deskLink = $shell.CreateShortcut((Join-Path $Desktop 'Streamsthetic.lnk'))
    $deskLink.TargetPath = Join-Path $Target 'Streamsthetic.exe'
    $deskLink.WorkingDirectory = $Target
    $deskLink.Description = 'Use your phone as a Windows webcam'
    $deskLink.Save()
}

# "Installed apps" entry (per user)
New-Item -Path $UninstallKey -Force | Out-Null
$uninstall = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Target 'uninstall.ps1')`""
$installedInfo = (Get-Item -LiteralPath (Join-Path $Target 'Streamsthetic.exe')).VersionInfo
$installedVersion = $installedInfo.ProductVersion
if (-not $installedVersion) { $installedVersion = $installedInfo.FileVersion }
if ($installedVersion) { $installedVersion = $installedVersion.Split('+')[0] } else { $installedVersion = 'Unknown' }
$props = @{
    DisplayName = 'Streamsthetic'; Publisher = 'Streamsthetic'; DisplayVersion = $installedVersion
    InstallLocation = $Target; UninstallString = $uninstall; DisplayIcon = (Join-Path $Target 'Streamsthetic.exe')
}
foreach ($k in $props.Keys) { Set-ItemProperty -Path $UninstallKey -Name $k -Value $props[$k] }
Set-ItemProperty -Path $UninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name NoRepair -Value 1 -Type DWord

if (-not $NoCamera) {
    # A separate native PowerShell process gives the camera script's exit an unambiguous result.
    & (Join-Path $systemDirectory 'WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Target 'install-camera.ps1')
    Assert-StreamstheticCameraResult -ExitCode $LASTEXITCODE
}
Write-Output "App files and shortcuts are installed in $Target. Complete any setup steps reported above, then open Streamsthetic from the desktop or Start Menu."
if (-not $NoLaunch) { Start-Process (Join-Path $Target 'Streamsthetic.exe') }
