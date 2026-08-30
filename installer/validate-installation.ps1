[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ApplicationPath,

    [Parameter(Mandatory)]
    [string] $UninstallerPath,

    [Parameter(Mandatory)]
    [string] $ExpectedSubject,

    [Parameter(Mandatory)]
    [string] $ExpectedProductVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ProductSignature {
    param([Parameter(Mandatory)][string] $Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        -not [string]::Equals(
            $signature.SignerCertificate.Subject,
            $ExpectedSubject,
            [StringComparison]::Ordinal) -or
        $null -eq $signature.TimeStamperCertificate) {
        throw [Security.SecurityException]::new('The installed product signature is invalid.')
    }
}

Assert-ProductSignature -Path $ApplicationPath
Assert-ProductSignature -Path $UninstallerPath

$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    $ApplicationPath).ProductVersion
if (-not [string]::Equals(
    $productVersion,
    $ExpectedProductVersion,
    [StringComparison]::Ordinal)) {
    throw [InvalidDataException]::new('The registered version does not match the signed executable.')
}
