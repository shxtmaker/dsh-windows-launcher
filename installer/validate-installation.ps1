[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ApplicationPath,

    [Parameter(Mandatory)]
    [string] $UninstallerPath,

    [Parameter(Mandatory)]
    [string] $ExpectedProductVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Inno may inherit PowerShell 7 module paths while launching Windows PowerShell.
# Load the security module belonging to this host, never another host's manifest.
Import-Module ($PSHOME + '\Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop

function Assert-UnsignedProduct {
    param([Parameter(Mandatory)][string] $Path)

    # 本项目不签名自有产物。就地升级前仍要核对既有安装文件的 Authenticode 状态，
    # 目的是拒绝一个被替换成其他来源（他人签名、HashMismatch、Unknown）的 EXE/卸载器。
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
        throw [Security.SecurityException]::new(
            'The installed product is not the expected unsigned artifact.')
    }
}

Assert-UnsignedProduct -Path $ApplicationPath
Assert-UnsignedProduct -Path $UninstallerPath

$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    $ApplicationPath).ProductVersion
# SDK builds append the source revision as SemVer build metadata, while
# Inno records the release version without that suffix. Preserve the exact
# release/prerelease identity and allow only a valid metadata suffix.
$expectedPattern = '\A' + [regex]::Escape($ExpectedProductVersion) +
    '(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z'
if ($productVersion -cnotmatch $expectedPattern) {
    throw [IO.InvalidDataException]::new('The registered version does not match the installed executable.')
}
