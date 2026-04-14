[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Base64Certificate,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $false)]
    [string]$OutputName = "sideload-signing.pfx"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Base64Certificate)) {
    throw "SIDELOAD_CERT_BASE64 is missing."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$certificatePath = Join-Path $OutputDirectory $OutputName
[System.IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($Base64Certificate))

# Expose the temporary certificate path only to the current workflow run.
if ($env:GITHUB_OUTPUT) {
    "certificate_path=$certificatePath" >> $env:GITHUB_OUTPUT
}

Write-Host "Temporary certificate restored."
