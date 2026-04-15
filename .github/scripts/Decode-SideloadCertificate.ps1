[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Base64Certificate,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $false)]
    [string]$OutputName = "sideload-signing.pfx",

    [Parameter(Mandatory = $false)]
    [string]$CertificatePassword,

    [Parameter(Mandatory = $false)]
    [string]$ExpectedPublisher
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Base64Certificate)) {
    throw "SIDELOAD_CERT_BASE64 is missing."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$certificatePath = Join-Path $OutputDirectory $OutputName
$normalizedBase64 = ($Base64Certificate -replace "\s", "")
[System.IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($normalizedBase64))
$certificate = $null

if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
    try {
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $certificatePath,
            $CertificatePassword,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
        )
    }
    catch {
        throw "Failed to open the sideload certificate with SIDELOAD_CERT_PASSWORD. Verify both SIDELOAD_CERT_BASE64 and SIDELOAD_CERT_PASSWORD. Inner error: $($_.Exception.Message)"
    }

    if (-not $certificate.HasPrivateKey) {
        throw "The sideload certificate does not contain a private key. Please provide a .pfx that includes the private key."
    }

    if ($certificate.NotAfter -lt [DateTime]::UtcNow) {
        throw "The sideload certificate is expired (NotAfter: $($certificate.NotAfter.ToString('u')))."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher)) {
        $actualPublisher = $certificate.Subject.Trim()
        $normalizedExpectedPublisher = $ExpectedPublisher.Trim()

        if (-not [string]::Equals($actualPublisher, $normalizedExpectedPublisher, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The sideload certificate subject '$actualPublisher' does not match Package.appxmanifest Publisher '$normalizedExpectedPublisher'."
        }
    }

    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    $ekuExtensions = @($certificate.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] })

    if ($ekuExtensions.Length -gt 0) {
        $hasCodeSigningUsage = $false
        foreach ($ekuExtension in $ekuExtensions) {
            foreach ($oid in $ekuExtension.EnhancedKeyUsages) {
                if ($oid.Value -eq $codeSigningOid) {
                    $hasCodeSigningUsage = $true
                    break
                }
            }

            if ($hasCodeSigningUsage) {
                break
            }
        }

        if (-not $hasCodeSigningUsage) {
            throw "The sideload certificate is not valid for code signing (missing EKU OID $codeSigningOid)."
        }
    }
}

# Expose the temporary certificate path only to the current workflow run.
if ($env:GITHUB_OUTPUT) {
    "certificate_path=$certificatePath" >> $env:GITHUB_OUTPUT

    if ($null -ne $certificate) {
        "certificate_thumbprint=$($certificate.Thumbprint)" >> $env:GITHUB_OUTPUT
    }
}

Write-Host "Temporary certificate restored."
