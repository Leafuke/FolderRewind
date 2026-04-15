[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$CertificatePath,

    [Parameter(Mandatory = $false)]
    [string]$Thumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($CertificatePath) -and (Test-Path -LiteralPath $CertificatePath)) {
    # The certificate should exist only in the runner temp directory.
    Remove-Item -LiteralPath $CertificatePath -Force
    Write-Host "Temporary certificate removed."
}

if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
    $certificateStorePath = "Cert:\CurrentUser\My\$Thumbprint"

    if (Test-Path -LiteralPath $certificateStorePath) {
        Remove-Item -LiteralPath $certificateStorePath -Force
        Write-Host "Imported certificate removed from CurrentUser\\My."
    }
}
