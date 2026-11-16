# Builds the OBS plugin: windows\obs-plugin\x64\Release\streamsthetic-mic.dll.
# Needs the Visual Studio C++ build tools and the libobs headers of the OBS version it targets (fetched once into
# windows\third_party\obs-studio, headers only). The import library comes from obs.def, so no OBS build is needed.
param([string]$ObsTag = '30.0.0')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$headers = Join-Path $here '..\third_party\obs-studio'
if (-not (Test-Path (Join-Path $headers 'libobs\obs-module.h'))) {
    git clone -q --depth 1 --branch $ObsTag --filter=blob:none --sparse https://github.com/obsproject/obs-studio.git $headers
    if ($LASTEXITCODE) { throw 'could not fetch the libobs headers' }
    git -C $headers sparse-checkout set libobs
    if ($LASTEXITCODE) { throw 'could not fetch the libobs headers' }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -products '*' -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { throw 'The Visual Studio C++ build tools were not found.' }

$out = Join-Path $here 'x64\Release'
New-Item -ItemType Directory -Force $out | Out-Null
$libobs = (Resolve-Path (Join-Path $headers 'libobs')).Path
$steps = @(
    "cl /nologo /c /O2 /W4 /WX /wd4201 /MD /I`"$here`" /I`"$libobs`" `"$here\streamsthetic-mic.c`" /Fo`"$out\streamsthetic-mic.obj`"",
    "lib /nologo /def:`"$here\obs.def`" /machine:x64 /out:`"$out\obs.lib`"",
    "link /nologo /dll /out:`"$out\streamsthetic-mic.dll`" `"$out\streamsthetic-mic.obj`" `"$out\obs.lib`" kernel32.lib advapi32.lib"
)
$script = Join-Path $out 'build.cmd'
Set-Content -LiteralPath $script -Encoding ASCII -Value (@("@call `"$vcvars`" >nul") + ($steps | ForEach-Object { "$_ || exit /b 1" }))
& cmd /c "`"$script`""
if ($LASTEXITCODE) { throw 'OBS plugin build failed' }
Write-Output "Built $out\streamsthetic-mic.dll"
