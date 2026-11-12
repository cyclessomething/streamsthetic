# Build a fresh Windows + Android release in a new folder. Existing releases are never deleted.
param(
    [string]$OutputRoot,
    [string]$AndroidSdk,
    [string]$UpdateFeedFile,
    [string]$ReportEmailFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $root ('dist\releases\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "Output folder already exists; choose a new folder: $OutputRoot" }
$dist = Join-Path $OutputRoot 'PhoneCam'

# Fail before expensive builds if a complete, redistributable USB toolset is unavailable.
$candidates = @()
foreach ($sdk in @($AndroidSdk, $env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk'))) {
    if ($sdk) { $candidates += Join-Path $sdk 'platform-tools' }
}
$adb = Get-Command adb.exe -ErrorAction SilentlyContinue
if ($adb) { $candidates += Split-Path $adb.Source -Parent }
$platformTools = $candidates | Select-Object -Unique | Where-Object {
    $candidate = $_
    @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt' | Where-Object { -not (Test-Path -LiteralPath (Join-Path $candidate $_)) }).Count -eq 0
} | Select-Object -First 1
if (-not $platformTools) { throw 'Complete Android platform-tools not found. Set ANDROID_HOME, pass -AndroidSdk, or put the platform-tools directory on PATH.' }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools with C++ are required.' }
$vs = & $vswhere -products '*' -latest -requires Microsoft.Component.MSBuild -property installationPath
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw 'MSBuild was not found.' }

# Always ask Gradle to build current sources: never silently copy an old APK after a failed/missing build.
$previousSdk = $env:ANDROID_HOME
try {
    if ($AndroidSdk) { $env:ANDROID_HOME = [IO.Path]::GetFullPath($AndroidSdk) }
    Push-Location (Join-Path $root 'android')
    try {
        & '.\gradlew.bat' --no-daemon assembleDebug
        if ($LASTEXITCODE) { throw 'Android build failed; no release assembled.' }
    } finally { Pop-Location }
} finally { $env:ANDROID_HOME = $previousSdk }
$apk = Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk'
if (-not (Test-Path -LiteralPath $apk)) { throw 'Android build produced no debug APK.' }

& $msbuild "$root\windows\vcam\VCamSample.sln" -t:Restore -p:RestorePackagesConfig=true -v:quiet -nologo
if ($LASTEXITCODE) { throw 'virtual camera dependency restore failed' }
& $msbuild "$root\windows\vcam\VCamSample.sln" -p:Configuration=Release -p:Platform=x64 -p:PlatformToolset=v143 -m -v:quiet -nologo
if ($LASTEXITCODE) { throw 'virtual camera build failed' }
& $msbuild "$root\windows\receiver\PhoneCamReceiver.vcxproj" -p:Configuration=Release -p:Platform=x64 -m -v:quiet -nologo
if ($LASTEXITCODE) { throw 'receiver build failed' }

New-Item -ItemType Directory -Path $dist -Force | Out-Null
& dotnet publish "$root\windows\app\PhoneCam.csproj" -c Release -r win-x64 --self-contained false -o $dist -v quiet -nologo
if ($LASTEXITCODE) { throw 'app publish failed' }
Copy-Item -LiteralPath "$root\windows\receiver\x64\Release\StreamstheticReceiver.exe" -Destination $dist
New-Item -ItemType Directory -Path "$dist\vcam" | Out-Null
Copy-Item -LiteralPath "$root\windows\vcam\x64\Release\VCamSampleSource.dll" -Destination "$dist\vcam"
Copy-Item -LiteralPath "$root\windows\vcam\LICENSE" -Destination "$dist\vcam\LICENSE-VCamSample.txt"
foreach ($file in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'libwinpthread-1.dll') {
    $path = Join-Path $platformTools $file
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $dist }
}
Copy-Item -LiteralPath "$platformTools\NOTICE.txt" -Destination "$dist\adb-NOTICE.txt"
& "$dist\adb.exe" version
if ($LASTEXITCODE) { throw 'Bundled adb could not start.' }
Copy-Item -LiteralPath "$root\tools\installer\install.ps1", "$root\tools\installer\install-preflight.ps1", "$root\tools\installer\install-camera.ps1", "$root\tools\installer\uninstall.ps1", "$root\tools\installer\Install Streamsthetic.cmd", "$root\LICENSE", "$root\THIRD_PARTY_NOTICES.md" -Destination $dist
if ($UpdateFeedFile) { Copy-Item -LiteralPath $UpdateFeedFile -Destination (Join-Path $dist 'update-feed.txt') }
if ($ReportEmailFile) { Copy-Item -LiteralPath $ReportEmailFile -Destination (Join-Path $dist 'report-email.txt') }
& "$PSScriptRoot\sign.ps1" -Folder $dist

$required = 'Streamsthetic.exe', 'Streamsthetic.dll', 'Streamsthetic.runtimeconfig.json', 'Streamsthetic.deps.json', 'QRCoder.dll', 'StreamstheticReceiver.exe', 'vcam\VCamSampleSource.dll', 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'adb-NOTICE.txt', 'install.ps1', 'install-preflight.ps1', 'uninstall.ps1'
foreach ($file in $required) { if (-not (Test-Path -LiteralPath (Join-Path $dist $file))) { throw "Missing package dependency: $file" } }
$zip = Join-Path $OutputRoot 'Streamsthetic-win-x64.zip'
Compress-Archive -Path "$dist\*" -DestinationPath $zip -CompressionLevel Optimal
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$setup = Join-Path $OutputRoot 'Streamsthetic-Setup.exe'
& $csc /nologo /target:winexe /out:$setup /win32icon:"$root\assets\icon\Streamsthetic.ico" /resource:"$zip,payload.zip" /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Windows.Forms.dll "$root\tools\setup\Setup.cs"
if ($LASTEXITCODE) { throw 'Setup.exe build failed' }
Copy-Item -LiteralPath $apk -Destination (Join-Path $OutputRoot 'Streamsthetic-phone.apk')
$manifest = [ordered]@{
    BuiltAtUtc = [DateTime]::UtcNow.ToString('o')
    AndroidSigning = 'debug (testing only)'
    Files = @(Get-ChildItem -LiteralPath $OutputRoot -File | ForEach-Object { [ordered]@{ Name = $_.Name; Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputRoot 'release-manifest.json') -Encoding UTF8
Write-Output "Release built: $OutputRoot"
