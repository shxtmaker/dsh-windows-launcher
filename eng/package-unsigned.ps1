[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $WebView2BootstrapperPath,

    [string] $InnoCompilerPath,

    [string] $DotNetPath,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$arguments = @{
    Version = $Version
    WebView2BootstrapperPath = $WebView2BootstrapperPath
    OfficialUnsignedRelease = $true
}
foreach ($entry in @(
        @{ Name = 'InnoCompilerPath'; Value = $InnoCompilerPath },
        @{ Name = 'DotNetPath'; Value = $DotNetPath },
        @{ Name = 'OutputDirectory'; Value = $OutputDirectory })) {
    if (-not [string]::IsNullOrWhiteSpace($entry.Value)) {
        $arguments[$entry.Name] = $entry.Value
    }
}

& (Join-Path $PSScriptRoot 'package-internal.ps1') @arguments
if (-not $?) {
    exit 1
}
exit 0
