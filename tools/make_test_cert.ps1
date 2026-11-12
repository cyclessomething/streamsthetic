# Makes a SELF-SIGNED code-signing certificate for trying tools\sign.ps1. Other PCs will not trust it; a real release needs
# a certificate from a certificate authority. Writes test-signing.pfx (password "test") next to this script and prints the variables to set.
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Streamsthetic test signing' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2)
$pwd = ConvertTo-SecureString 'test' -AsPlainText -Force
$pfx = Join-Path $PSScriptRoot 'test-signing.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pwd | Out-Null
Write-Output "`$env:STREAMSTHETIC_PFX = '$pfx'"
Write-Output "`$env:STREAMSTHETIC_PFX_PASSWORD = 'test'"
