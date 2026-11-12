# Build a complete offline release in a new folder; preserve all previous packages.
param([string]$OutputRoot, [string]$AndroidSdk, [string]$UpdateFeedFile, [string]$ReportEmailFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $root ('dist\releases\offline-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
& "$PSScriptRoot\make_dist.ps1" -OutputRoot $OutputRoot -AndroidSdk $AndroidSdk -UpdateFeedFile $UpdateFeedFile -ReportEmailFile $ReportEmailFile
$pkg = Join-Path $OutputRoot 'offline-package'
$pc = Join-Path $pkg 'Streamsthetic-PC'
New-Item -ItemType Directory -Path $pc -Force | Out-Null
Copy-Item (Join-Path $OutputRoot 'PhoneCam\*') $pc -Recurse
& dotnet publish "$root\windows\app\PhoneCam.csproj" -c Release -r win-x64 --self-contained true -o $pc -v quiet -nologo
if ($LASTEXITCODE) { throw 'self-contained publish failed' }
foreach ($file in 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'Streamsthetic.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $pc $file))) { throw "Offline runtime missing: $file" }
}
# Resolve notices from the exact runtime packs published above, never a newer cache version.
$runtimeConfig = Get-Content -LiteralPath (Join-Path $pc 'Streamsthetic.runtimeconfig.json') -Raw | ConvertFrom-Json
$dependencies = Get-Content -LiteralPath (Join-Path $pc 'Streamsthetic.deps.json') -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath (Join-Path $root 'windows\app\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
$noticeSpecs = @(
    @{ Framework = 'Microsoft.NETCore.App'; Package = 'microsoft.netcore.app.runtime.win-x64'; Files = @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') },
    @{ Framework = 'Microsoft.WindowsDesktop.App'; Package = 'microsoft.windowsdesktop.app.runtime.win-x64'; Files = @('LICENSE') }
)
foreach ($spec in $noticeSpecs) {
    $framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq $spec.Framework })
    if ($framework.Count -ne 1) { throw "Published runtime version missing or ambiguous: $($spec.Framework)" }
    $version = $framework[0].version
    $library = 'runtimepack.' + $spec.Package + '/' + $version
    if (-not ($dependencies.libraries.PSObject.Properties.Name -contains $library)) { throw "Published runtime dependency differs: $library" }
    $pack = $packageFolders | ForEach-Object { Join-Path $_ ($spec.Package + '\' + $version) } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (-not $pack) { throw "Runtime license source package missing: $($spec.Package)/$version" }
    $noticeDir = Join-Path $pc ('runtime-notices\' + $spec.Package + '\' + $version)
    New-Item -ItemType Directory -Path $noticeDir -Force | Out-Null
    foreach ($notice in $spec.Files) {
        $noticeSource = Join-Path $pack $notice
        if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) { throw "Required runtime notice missing: $noticeSource" }
        Copy-Item -LiteralPath $noticeSource -Destination $noticeDir
    }
}
& "$PSScriptRoot\sign.ps1" -Folder $pc
# The Windows-only download embeds the complete self-contained PC payload, without the APK.
# Compile after publishing/copying/signing so the executable contains the final installer scripts.
$setupWork = Join-Path ([IO.Path]::GetTempPath()) ('Streamsthetic-offline-setup-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $setupWork | Out-Null
$pcZip = Join-Path $setupWork 'payload.zip'
Compress-Archive -Path "$pc\*" -DestinationPath $pcZip -CompressionLevel Optimal
$offlineSetup = Join-Path $OutputRoot 'Streamsthetic-Setup-offline.exe'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /out:$offlineSetup /win32icon:"$root\assets\icon\Streamsthetic.ico" /resource:"$pcZip,payload.zip" /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Windows.Forms.dll "$root\tools\setup\Setup.cs"
if ($LASTEXITCODE) { throw 'Offline Setup.exe build failed' }
# Run only extraction, never installation. Compare names and every byte via SHA-256.
$extracted = Join-Path $setupWork 'extracted'
$check = Start-Process -FilePath $offlineSetup -ArgumentList '--extract-only', ('"' + $extracted + '"') -PassThru -WindowStyle Hidden
if (-not $check.WaitForExit(60000)) { $check.Kill(); throw "Offline setup extraction timed out; evidence retained at $setupWork" }
if ($check.ExitCode) { throw "Offline setup extraction failed; evidence retained at $setupWork" }
$expectedFiles = @(Get-ChildItem -LiteralPath $pc -Recurse -File)
$actualFiles = @(Get-ChildItem -LiteralPath $extracted -Recurse -File)
if ($expectedFiles.Count -ne $actualFiles.Count) { throw 'Offline setup payload file count differs from PC staging' }
$pcPrefix = [IO.Path]::GetFullPath($pc).TrimEnd('\') + '\'
foreach ($file in $expectedFiles) {
    $relative = $file.FullName.Substring($pcPrefix.Length)
    $copy = Join-Path $extracted $relative
    if (-not (Test-Path -LiteralPath $copy -PathType Leaf)) { throw "Offline setup payload missing: $relative" }
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) { throw "Offline setup payload differs: $relative" }
}
Write-Output "Offline setup extraction verified: $($expectedFiles.Count) files match PC staging."
# Only remove this run's verified, generated temporary workspace after successful validation.
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not ([IO.Path]::GetFullPath($setupWork).StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Unexpected offline setup temporary path' }
Remove-Item -LiteralPath $setupWork -Recurse -Force
Copy-Item -LiteralPath (Join-Path $OutputRoot 'Streamsthetic-phone.apk') -Destination $pkg
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'offline-readme.txt') -Destination (Join-Path $pkg 'READ ME FIRST.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'collect_problem_info.ps1'), (Join-Path $PSScriptRoot 'Collect problem info.cmd') -Destination $pkg
$zip = Join-Path $OutputRoot 'Streamsthetic-offline-package.zip'
Compress-Archive -Path "$pkg\*" -DestinationPath $zip -CompressionLevel Optimal
$manifestPath = Join-Path $OutputRoot 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($artifact in @($zip, $offlineSetup)) {
    $manifest.Files += [pscustomobject]@{ Name = [IO.Path]::GetFileName($artifact); Bytes = (Get-Item -LiteralPath $artifact).Length; SHA256 = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Output ('Offline package: {0:N1} MB -> {1}' -f ((Get-Item -LiteralPath $zip).Length / 1MB), $zip)
Write-Output ('Offline Windows setup: {0:N1} MB -> {1}' -f ((Get-Item -LiteralPath $offlineSetup).Length / 1MB), $offlineSetup)
