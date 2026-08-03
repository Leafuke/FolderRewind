[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $CertificatePath)) {
    throw "Certificate file not found: $CertificatePath"
}

$securePassword = ConvertTo-SecureString -String $CertificatePassword -AsPlainText -Force
$importedCertificate = Import-PfxCertificate `
    -FilePath $CertificatePath `
    -Password $securePassword `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -Exportable

if ($null -eq $importedCertificate) {
    throw "Failed to import the sideload certificate into Cert:\\CurrentUser\\My."
}

if ($env:GITHUB_OUTPUT) {
    "imported_certificate_thumbprint=$($importedCertificate.Thumbprint)" >> $env:GITHUB_OUTPUT
}

Write-Host "Imported certificate into CurrentUser\\My: $($importedCertificate.Thumbprint)"
