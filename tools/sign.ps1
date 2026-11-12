# Signs the programs in dist\PhoneCam so Windows can show who made them and SmartScreen warns less.
#   $env:STREAMSTHETIC_PFX = path to the code-signing certificate (.pfx)   (bought from a certificate authority)
#   $env:STREAMSTHETIC_PFX_PASSWORD = its password
# make_dist.ps1 calls this automatically when STREAMSTHETIC_PFX is set. Without a certificate nothing is signed.
# For a local test only: tools\make_test_cert.ps1 makes a self-signed certificate (Windows will NOT trust it on other PCs).
param([string]$Folder = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\PhoneCam'))
$ErrorActionPreference = 'Stop'
if (-not $env:STREAMSTHETIC_PFX) { Write-Output 'No certificate set (STREAMSTHETIC_PFX): not signing.'; return }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -match 'x64$' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK).' }
$targets = @('Streamsthetic.exe', 'StreamstheticReceiver.exe', 'vcam\VCamSampleSource.dll') | ForEach-Object { Join-Path $Folder $_ } | Where-Object { Test-Path $_ }
foreach ($file in $targets) {
    & $signtool.FullName sign /fd SHA256 /f $env:STREAMSTHETIC_PFX /p $env:STREAMSTHETIC_PFX_PASSWORD /tr http://timestamp.digicert.com /td SHA256 $file
    if ($LASTEXITCODE) { throw "signing failed for $file" }
    $sig = Get-AuthenticodeSignature $file
    Write-Output ("{0}: {1}" -f (Split-Path $file -Leaf), $sig.Status)
}
