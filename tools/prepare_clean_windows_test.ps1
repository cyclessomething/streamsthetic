# Prepare only. This script never launches Sandbox or an installer.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$ExpectedAppPath,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$app = (Resolve-Path -LiteralPath $ExpectedAppPath).Path
if ([IO.Path]::GetExtension($setup) -ne '.exe' -or [IO.Path]::GetFileName($app) -ne 'Streamsthetic.exe') { throw 'Supply the offline Setup EXE and its matching staged Streamsthetic.exe.' }
$root = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $root) { throw 'Choose a new dedicated test directory; existing directories are never mapped or reused.' }
New-Item -ItemType Directory -Path $root | Out-Null
$inputDir = New-Item -ItemType Directory -Path (Join-Path $root 'input')
$outputDir = New-Item -ItemType Directory -Path (Join-Path $root 'evidence')
Copy-Item -LiteralPath $setup -Destination (Join-Path $inputDir 'Setup-offline.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'clean_windows_test.ps1') -Destination $inputDir
$token = [Guid]::NewGuid().ToString('N')
[ordered]@{
    Schema = 1; Token = $token; SetupSHA256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    ExpectedAppSHA256 = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
    ExpectedAppVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputDir 'test-input.json') -Encoding UTF8
$mappedInput = [Security.SecurityElement]::Escape($inputDir.FullName)
$mappedOutput = [Security.SecurityElement]::Escape($outputDir.FullName)
@"
<Configuration>
  <MemoryInMB>4096</MemoryInMB>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$mappedInput</HostFolder><SandboxFolder>C:\StreamstheticTest\Input</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$mappedOutput</HostFolder><SandboxFolder>C:\StreamstheticTest\Output</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\StreamstheticTest\Input\clean_windows_test.ps1 -Token $token</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath (Join-Path $root 'clean-windows.wsb') -Encoding UTF8
@'
Prepared only: no Sandbox or installer has been launched.
Open clean-windows.wsb only after Windows Sandbox is available. Setup runs inside the disposable guest.
Only input (read-only) and evidence (writable) are mapped. No network, clipboard, microphone, video-input or printer redirection.
Camera installation may request administrator approval inside the guest. Approve only that guest prompt. Unattended prompts time out after four minutes.
Keep Sandbox open until evidence/result.json contains FinishedUtc or an explicit failure. Evidence remains after closing Sandbox.
Successful installation and a screenshot are NOT proof of camera frames or hardware compatibility. Streaming, physical phone/USB, upgrade and removal remain pending.
The runner refuses execution outside the dedicated guest account, virtual-machine model and mapped input path. Do not run it manually on the host.
'@ | Set-Content -LiteralPath (Join-Path $root 'READ-ME.txt') -Encoding UTF8
[pscustomobject]@{ Configuration = (Join-Path $root 'clean-windows.wsb'); Evidence = $outputDir.FullName; InputFiles = 3; Launched = $false }
