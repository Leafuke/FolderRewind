[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$CertificatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    Write-Host "No certificate path provided. Skip cleanup."
    exit 0
}

if (Test-Path -LiteralPath $CertificatePath) {
    # The certificate should exist only in the runner temp directory.
    Remove-Item -LiteralPath $CertificatePath -Force
    Write-Host "Temporary certificate removed."
}
