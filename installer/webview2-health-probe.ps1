[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CoreAssemblyPath,

    [Parameter(Mandatory)]
    [string] $UserDataFolder,

    [Parameter(Mandatory)]
    [string] $MinimumVersion,

    [ValidateRange(1, 120)]
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Remove-ProbeTreeWithoutFollowingLinks {
    param([Parameter(Mandatory)][string] $Path)

    if (-not [IO.Directory]::Exists($Path)) {
        return
    }
    $attributes = [IO.File]::GetAttributes($Path)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        [IO.Directory]::Delete($Path, $false)
        return
    }

    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Path)) {
        $entryAttributes = [IO.File]::GetAttributes($entry)
        if (($entryAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            if (($entryAttributes -band [IO.FileAttributes]::Directory) -ne 0) {
                [IO.Directory]::Delete($entry, $false)
            }
            else {
                [IO.File]::Delete($entry)
            }
        }
        elseif (($entryAttributes -band [IO.FileAttributes]::Directory) -ne 0) {
            Remove-ProbeTreeWithoutFollowingLinks -Path $entry
        }
        else {
            [IO.File]::SetAttributes($entry, [IO.FileAttributes]::Normal)
            [IO.File]::Delete($entry)
        }
    }
    [IO.Directory]::Delete($Path, $false)
}

$overrides = @(
    'WEBVIEW2_BROWSER_EXECUTABLE_FOLDER',
    'WEBVIEW2_USER_DATA_FOLDER',
    'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS',
    'WEBVIEW2_RELEASE_CHANNEL_PREFERENCE',
    'WEBVIEW2_CHANNEL_SEARCH_KIND',
    'WEBVIEW2_RELEASE_CHANNELS'
)
foreach ($name in $overrides) {
    if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name))) {
        throw [InvalidOperationException]::new('A WebView2 environment override prevents a canonical runtime probe.')
    }
}

$fullCoreAssemblyPath = [IO.Path]::GetFullPath($CoreAssemblyPath)
$fullUserDataFolder = [IO.Path]::GetFullPath($UserDataFolder)
$environment = $null
try {
    Set-Location -LiteralPath ([IO.Path]::GetDirectoryName($fullCoreAssemblyPath))
    Add-Type -Path $fullCoreAssemblyPath
    $options = [Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions]::new()
    $options.ReleaseChannels =
        [Microsoft.Web.WebView2.Core.CoreWebView2ReleaseChannels]::Stable

    $availableText =
        [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::GetAvailableBrowserVersionString(
            $null,
            $options)
    $availableVersion = [Version] ($availableText -split '\s+', 2)[0]
    if ($availableVersion -lt [Version] $MinimumVersion) {
        throw [InvalidOperationException]::new('The stable WebView2 Runtime is below the required version.')
    }

    $creation = [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::CreateAsync(
        $null,
        $fullUserDataFolder,
        $options)
    if (-not $creation.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        throw [TimeoutException]::new('WebView2 environment creation timed out.')
    }
    $environment = $creation.GetAwaiter().GetResult()
    if ($null -eq $environment -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($environment.UserDataFolder),
            $fullUserDataFolder,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw [InvalidOperationException]::new('WebView2 environment creation did not use the isolated user data folder.')
    }
}
finally {
    $environment = $null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    [GC]::Collect()

    for ($attempt = 0; $attempt -lt 20 -and [IO.Directory]::Exists($fullUserDataFolder); $attempt++) {
        try {
            Remove-ProbeTreeWithoutFollowingLinks -Path $fullUserDataFolder
        }
        catch [IO.IOException] {
            Start-Sleep -Milliseconds 250
        }
        catch [UnauthorizedAccessException] {
            Start-Sleep -Milliseconds 250
        }
    }
    if ([IO.Directory]::Exists($fullUserDataFolder)) {
        throw [IO.IOException]::new('The WebView2 runtime probe did not release its isolated user data folder.')
    }
}
