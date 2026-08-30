[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $WebView2OfflineInstallerPath,

    [Parameter(Mandatory)]
    [string] $WebView2BootstrapperPath,

    [Parameter(Mandatory)]
    [string] $InnoSetupInstallerPath,

    [string] $InnoCompilerPath,

    [Parameter(Mandatory)]
    [string] $SignToolPath,

    [Parameter(Mandatory)]
    [string] $CertificateThumbprint,

    [string] $DotNetPath,

    [string] $OutputDirectory,

    [string] $UninstallerVerificationDirectory,

    [switch] $AllowInstallerExecutionForUninstallerVerification
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-FileHash {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $ExpectedHash,
        [Parameter(Mandatory)]
        [string] $Description
    )

    $actualHash = Get-DshSha256 -Path $Path
    if ($actualHash -ne $ExpectedHash.ToUpperInvariant()) {
        throw "$Description SHA-256 不匹配。实际：$actualHash；预期：$ExpectedHash"
    }
}

function Assert-SignedMicrosoftTool {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $Description
    )

    $evidence = Get-DshAuthenticodeEvidence -Path $Path -RequireTimestamp
    if ($evidence.SignerSubject -notmatch '(?i)Microsoft') {
        throw "$Description 不是 Microsoft 有效签名：$($evidence.SignerSubject)"
    }

    return $evidence
}

function Get-CertificateByThumbprint {
    param(
        [Parameter(Mandatory)]
        [string] $Thumbprint
    )

    $normalized = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    foreach ($store in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
        $certificate = Get-ChildItem -Path $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $normalized } |
            Select-Object -First 1
        if ($null -ne $certificate) {
            return $certificate
        }
    }

    throw "签名证书不存在：$normalized"
}

function Invoke-SignTool {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(Mandatory)]
        [string] $TargetPath,
        [Parameter(Mandatory)]
        [string] $Thumbprint,
        [Parameter(Mandatory)]
        [string] $TimestampUri,
        [Parameter(Mandatory)]
        [string] $ProductName,
        [Parameter(Mandatory)]
        [string] $ReleaseUri,
        [Parameter(Mandatory)]
        [string] $WorkingDirectory
    )

    Invoke-DshNative -FilePath $FilePath -WorkingDirectory $WorkingDirectory -Arguments @(
        'sign',
        '/sha1', $Thumbprint,
        '/fd', 'SHA256',
        '/tr', $TimestampUri,
        '/td', 'SHA256',
        '/d', $ProductName,
        '/du', $ReleaseUri,
        $TargetPath
    )
}

try {
    if (-not (Test-DshSemVer -Version $Version)) {
        throw "-Version 必须是明确 SemVer：$Version"
    }

    if (-not $AllowInstallerExecutionForUninstallerVerification) {
        throw '正式打包必须显式传入 -AllowInstallerExecutionForUninstallerVerification，在干净隔离 Windows runner 安装最终包并验证卸载器签名。'
    }

    if ([string]::IsNullOrWhiteSpace($UninstallerVerificationDirectory)) {
        throw '必须提供隔离固定卷上的 -UninstallerVerificationDirectory。'
    }

    $constants = Get-DshReleaseConstants -RepositoryRoot $repositoryRoot
    Assert-DshReleaseConstants -RepositoryRoot $repositoryRoot -Constants $constants -RequireCandidate
    if ((Compare-DshSemVer -Left $Version -Right $constants.product.version) -ne 0 -or
        $Version -ne $constants.product.version) {
        throw "参数版本必须与发布常量完全一致。参数：$Version；常量：$($constants.product.version)"
    }

    foreach ($path in @($WebView2OfflineInstallerPath, $WebView2BootstrapperPath, $InnoSetupInstallerPath, $SignToolPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "输入文件不存在：$path"
        }
    }

    $WebView2OfflineInstallerPath = (Resolve-Path -LiteralPath $WebView2OfflineInstallerPath).Path
    $WebView2BootstrapperPath = (Resolve-Path -LiteralPath $WebView2BootstrapperPath).Path
    $InnoSetupInstallerPath = (Resolve-Path -LiteralPath $InnoSetupInstallerPath).Path
    $SignToolPath = (Resolve-Path -LiteralPath $SignToolPath).Path

    Assert-FileHash -Path $WebView2OfflineInstallerPath `
        -ExpectedHash $constants.dependencyBaseline.webView2Runtime.offlineInstallerSha256 `
        -Description 'WebView2 Offline Installer'
    $webViewSignature = Assert-SignedMicrosoftTool -Path $WebView2OfflineInstallerPath -Description 'WebView2 Offline Installer'
    $webViewVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo($WebView2OfflineInstallerPath).ProductVersion).Trim()
    if ($webViewVersion -ne $constants.dependencyBaseline.webView2Runtime.offlineInstallerVersion) {
        throw "WebView2 Offline Installer 版本不匹配。实际：$webViewVersion；预期：$($constants.dependencyBaseline.webView2Runtime.offlineInstallerVersion)"
    }

    Assert-FileHash -Path $WebView2BootstrapperPath `
        -ExpectedHash $constants.dependencyBaseline.webView2Runtime.bootstrapper.sha256 `
        -Description 'WebView2 Evergreen Bootstrapper'
    $webViewBootstrapperSignature = Assert-SignedMicrosoftTool `
        -Path $WebView2BootstrapperPath `
        -Description 'WebView2 Evergreen Bootstrapper'
    $webViewBootstrapperVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo($WebView2BootstrapperPath).ProductVersion).Trim()
    if ([string]::IsNullOrWhiteSpace($webViewBootstrapperVersion)) {
        $webViewBootstrapperVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo($WebView2BootstrapperPath).FileVersion).Trim()
    }
    if ($webViewBootstrapperVersion -ne $constants.dependencyBaseline.webView2Runtime.bootstrapper.version) {
        throw "WebView2 Evergreen Bootstrapper 版本不匹配。实际：$webViewBootstrapperVersion；预期：$($constants.dependencyBaseline.webView2Runtime.bootstrapper.version)"
    }

    Assert-FileHash -Path $InnoSetupInstallerPath `
        -ExpectedHash $constants.distribution.innoSetup.sha256 `
        -Description 'Inno Setup 官方安装包'
    $innoInstallerSignature = Get-DshAuthenticodeEvidence -Path $InnoSetupInstallerPath -RequireTimestamp
    if ($innoInstallerSignature.SignerSubject -notmatch '(?i)Pyrsys B\.V\.') {
        throw "Inno Setup 官方安装包签名主体不匹配：$($innoInstallerSignature.SignerSubject)"
    }

    if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
        $InnoCompilerPath = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'
    }
    if (-not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) {
        throw "Inno Setup 编译器不存在：$InnoCompilerPath"
    }
    $InnoCompilerPath = (Resolve-Path -LiteralPath $InnoCompilerPath).Path
    $innoCompilerSignature = Get-DshAuthenticodeEvidence -Path $InnoCompilerPath -RequireTimestamp
    if ($innoCompilerSignature.SignerSubject -notmatch '(?i)Pyrsys B\.V\.') {
        throw "ISCC.exe 签名主体不匹配：$($innoCompilerSignature.SignerSubject)"
    }
    $innoInstallationUninstallerPath = Join-Path (Split-Path -Parent $InnoCompilerPath) 'unins000.exe'
    if (-not (Test-Path -LiteralPath $innoInstallationUninstallerPath -PathType Leaf)) {
        throw "Inno Setup 安装目录缺少版本身份文件：$innoInstallationUninstallerPath"
    }
    $innoInstallationUninstallerSignature = Get-DshAuthenticodeEvidence `
        -Path $innoInstallationUninstallerPath `
        -RequireTimestamp
    if ($innoInstallationUninstallerSignature.SignerSubject -notmatch '(?i)Pyrsys B\.V\.') {
        throw "Inno Setup 安装卸载器签名主体不匹配：$($innoInstallationUninstallerSignature.SignerSubject)"
    }
    $innoVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo($innoInstallationUninstallerPath).ProductVersion).Trim()
    if ($innoVersion -ne $constants.distribution.innoSetup.version) {
        throw "Inno Setup 安装版本不匹配。实际：$innoVersion；预期：$($constants.distribution.innoSetup.version)"
    }

    $signToolEvidence = Assert-SignedMicrosoftTool -Path $SignToolPath -Description 'SignTool'
    $certificate = Get-CertificateByThumbprint -Thumbprint $CertificateThumbprint
    if (-not $certificate.HasPrivateKey) {
        throw '签名证书没有可用私钥。'
    }
    if ($certificate.Subject -ne $constants.distribution.signing.certificateSubject) {
        throw "证书主体与发布常量不一致。实际：$($certificate.Subject)；预期：$($constants.distribution.signing.certificateSubject)"
    }
    if ($certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        throw '签名证书已过期。'
    }
    if ($certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow) {
        throw '签名证书尚未生效。'
    }
    $enhancedKeyUsage = $certificate.Extensions |
        Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        Select-Object -First 1
    if ($null -eq $enhancedKeyUsage -or
        @($enhancedKeyUsage.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) {
        throw '签名证书缺少 Code Signing EKU。'
    }
    $CertificateThumbprint = $certificate.Thumbprint

    $dotnet = Get-DshExactDotNetPath -ExpectedVersion $constants.build.dotnetSdkVersion -DotNetPath $DotNetPath
    $verifyArtifacts = Join-Path $repositoryRoot "artifacts/verify-package-$Version"
    Write-Host '[package] 先执行统一 verify 门禁。'
    $pwshExecutable = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshExecutable -PathType Leaf)) {
        $pwshExecutable = (Get-Command -Name pwsh -CommandType Application -ErrorAction Stop).Source
    }
    Invoke-DshNative -FilePath $pwshExecutable -WorkingDirectory $repositoryRoot -Arguments @(
        '-NoLogo', '-NoProfile', '-NonInteractive',
        '-File', (Join-Path $PSScriptRoot 'verify.ps1'),
        '-DotNetPath', $dotnet,
        '-ArtifactsDirectory', $verifyArtifacts
    )

    $sourceState = Get-DshSourceState -RepositoryRoot $repositoryRoot
    if (-not $sourceState.Available -or
        $sourceState.Commit -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
        $sourceState.WorktreeState -cne 'clean') {
        throw '正式打包只接受可用、提交 ID 有效且工作树为 clean 的 Git 源状态。'
    }
    $verifySummarySource = Join-Path $verifyArtifacts 'verify-summary.json'
    if (-not (Test-Path -LiteralPath $verifySummarySource -PathType Leaf)) {
        throw '统一验证未生成 verify-summary.json。'
    }
    $verifySummary = Get-Content -LiteralPath $verifySummarySource -Raw -Encoding UTF8 |
        ConvertFrom-Json -Depth 20
    if ($verifySummary.result -ne 'PASS') {
        throw '统一验证摘要不是 PASS。'
    }
    if ($null -eq $verifySummary.source -or
        $verifySummary.source.available -ne $sourceState.Available -or
        $verifySummary.source.commit -cne $sourceState.Commit -or
        $verifySummary.source.worktreeState -cne $sourceState.WorktreeState) {
        throw '统一验证摘要的源提交或工作树状态与正式打包源不一致。'
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $repositoryRoot "artifacts/package/$Version"
    }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) {
        if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
            throw "输出目录必须不存在或为空：$OutputDirectory"
        }
    }
    else {
        $null = New-Item -ItemType Directory -Path $OutputDirectory
    }

    $stagingDirectory = Join-Path $OutputDirectory 'staging'
    $publishDirectory = Join-Path $stagingDirectory 'publish'
    $signedUninstallerDirectory = Join-Path $stagingDirectory 'signed-uninstaller'
    $releaseDirectory = Join-Path $OutputDirectory 'release'
    foreach ($directory in @($stagingDirectory, $publishDirectory, $signedUninstallerDirectory, $releaseDirectory)) {
        $null = New-Item -ItemType Directory -Path $directory
    }

    $sbomPath = Join-Path $releaseDirectory 'sbom.spdx.json'
    $licenseInventoryPath = Join-Path $releaseDirectory 'third-party-licenses.json'
    foreach ($supplyChainFile in @(
        @{ Source = Join-Path $verifyArtifacts 'sbom.spdx.json'; Destination = $sbomPath },
        @{ Source = Join-Path $verifyArtifacts 'third-party-licenses.json'; Destination = $licenseInventoryPath }
    )) {
        if (-not (Test-Path -LiteralPath $supplyChainFile.Source -PathType Leaf)) {
            throw "统一验证缺少供应链产物：$($supplyChainFile.Source)"
        }
        Copy-Item -LiteralPath $supplyChainFile.Source -Destination $supplyChainFile.Destination
    }

    $fileVersion = ConvertTo-DshFileVersion -Version $Version
    $desktopProject = Join-Path $repositoryRoot 'src/DshLauncher.Desktop/DshLauncher.Desktop.csproj'
    Write-Host '[package] 发布自包含、多文件、非裁剪 win-x64 应用。'
    Invoke-DshNative -FilePath $dotnet -WorkingDirectory $repositoryRoot -Arguments @(
        'publish', $desktopProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '--output', $publishDirectory,
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        "-p:Version=$Version",
        "-p:FileVersion=$fileVersion",
        "-p:InformationalVersion=$Version"
    )

    $webViewBootstrapperName = 'MicrosoftEdgeWebview2Setup.exe'
    $stagedWebViewBootstrapperPath = Join-Path $publishDirectory $webViewBootstrapperName
    Copy-Item -LiteralPath $WebView2BootstrapperPath -Destination $stagedWebViewBootstrapperPath
    Assert-FileHash `
        -Path $stagedWebViewBootstrapperPath `
        -ExpectedHash $constants.dependencyBaseline.webView2Runtime.bootstrapper.sha256 `
        -Description '暂存 WebView2 Evergreen Bootstrapper'
    $stagedWebViewBootstrapperSignature = Get-DshAuthenticodeEvidence `
        -Path $stagedWebViewBootstrapperPath `
        -ExpectedSubject $webViewBootstrapperSignature.SignerSubject `
        -RequireTimestamp

    $applicationExe = Join-Path $publishDirectory $constants.product.executableName
    if (-not (Test-Path -LiteralPath $applicationExe -PathType Leaf)) {
        throw "发布结果缺少主程序：$applicationExe"
    }
    if (@(Get-ChildItem -LiteralPath $publishDirectory -File).Count -lt 2 -or
        -not (Test-Path -LiteralPath (Join-Path $publishDirectory 'DshWindowsLauncher.dll') -PathType Leaf)) {
        throw '发布结果不是预期的自包含多文件布局。'
    }

    $publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationExe).FileVersion
    if (-not $publishedVersion.StartsWith($fileVersion, [StringComparison]::OrdinalIgnoreCase)) {
        throw "主程序文件版本不匹配。实际：$publishedVersion；预期：$fileVersion"
    }

    Write-Host '[package] 签名并验证主程序。'
    Invoke-SignTool `
        -FilePath $SignToolPath `
        -TargetPath $applicationExe `
        -Thumbprint $CertificateThumbprint `
        -TimestampUri $constants.distribution.signing.timestampServerUri `
        -ProductName $constants.product.name `
        -ReleaseUri $constants.distribution.officialReleaseUri `
        -WorkingDirectory $repositoryRoot
    $applicationSignature = Get-DshAuthenticodeEvidence `
        -Path $applicationExe `
        -ExpectedSubject $constants.distribution.signing.certificateSubject `
        -RequireTimestamp

    $publishBytes = (Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Measure-Object -Property Length -Sum).Sum
    $webViewInstallerBytes = (Get-Item -LiteralPath $WebView2OfflineInstallerPath).Length
    $requiredSpaceBytes = [int64] ($publishBytes * 2 + $webViewInstallerBytes + 268435456)
    $installerName = $constants.distribution.installerFileNameTemplate.
        Replace('{Product}', $constants.product.name, [StringComparison]::Ordinal).
        Replace('{SemVer}', $Version, [StringComparison]::Ordinal)
    if ($installerName -notmatch '^[^\\/:*?"<>|]+\.exe$') {
        throw "安装包文件名不安全：$installerName"
    }
    $outputBaseFilename = [IO.Path]::GetFileNameWithoutExtension($installerName)

    $signCommand = '$q' + $SignToolPath + '$q sign /sha1 ' + $CertificateThumbprint +
        ' /fd SHA256 /tr ' + $constants.distribution.signing.timestampServerUri +
        ' /td SHA256 /d $q' + $constants.product.name + '$q /du ' +
        $constants.distribution.officialReleaseUri + ' $f'

    function New-IsppStringDefine {
        param([string] $Name, [string] $Value)
        if ($Value.Contains('"', [StringComparison]::Ordinal)) {
            throw "ISPP 参数不能包含双引号：$Name"
        }
        return "/D$Name=$Value"
    }

    $innoScript = Join-Path $repositoryRoot 'installer/DshWindowsLauncher.iss'
    $maintenanceHelperPath = Join-Path $repositoryRoot 'installer/maintenance-ipc.ps1'
    $identityHelperPath = Join-Path $repositoryRoot 'installer/validate-installation.ps1'
    $installOwnershipMarkerPath = Join-Path $repositoryRoot 'installer/install-owner.txt'
    $webView2HealthHelperPath = Join-Path $repositoryRoot 'installer/webview2-health-probe.ps1'
    $webView2CoreAssemblyPath = Join-Path $publishDirectory 'Microsoft.Web.WebView2.Core.dll'
    $webView2LoaderPath = Join-Path $publishDirectory 'WebView2Loader.dll'
    foreach ($installerInput in @(
        $innoScript,
        $maintenanceHelperPath,
        $identityHelperPath,
        $installOwnershipMarkerPath,
        $webView2HealthHelperPath,
        $webView2CoreAssemblyPath,
        $webView2LoaderPath
    )) {
        if (-not (Test-Path -LiteralPath $installerInput -PathType Leaf)) {
            throw "安装器输入不存在：$installerInput"
        }
    }
    $maintenanceHelperSha256 = Get-DshSha256 -Path $maintenanceHelperPath
    $identityHelperSha256 = Get-DshSha256 -Path $identityHelperPath
    $installOwnershipMarkerSha256 = Get-DshSha256 -Path $installOwnershipMarkerPath
    $webView2HealthHelperSha256 = Get-DshSha256 -Path $webView2HealthHelperPath
    $webView2CoreAssemblySha256 = Get-DshSha256 -Path $webView2CoreAssemblyPath
    $webView2LoaderSha256 = Get-DshSha256 -Path $webView2LoaderPath
    $webView2CoreAssemblySignature = Assert-SignedMicrosoftTool `
        -Path $webView2CoreAssemblyPath `
        -Description 'WebView2 Core managed assembly'
    $webView2LoaderSignature = Assert-SignedMicrosoftTool `
        -Path $webView2LoaderPath `
        -Description 'WebView2 native loader'
    $compilerArguments = @(
        "/Sdshwl=$signCommand",
        (New-IsppStringDefine -Name 'SourceRoot' -Value $publishDirectory),
        (New-IsppStringDefine -Name 'AppVersion' -Value $Version),
        (New-IsppStringDefine -Name 'FileVersion' -Value $fileVersion),
        (New-IsppStringDefine -Name 'Publisher' -Value $constants.distribution.signing.publisher),
        (New-IsppStringDefine -Name 'CertificateSubject' -Value $constants.distribution.signing.certificateSubject),
        (New-IsppStringDefine -Name 'ReleaseUri' -Value $constants.distribution.officialReleaseUri),
        (New-IsppStringDefine -Name 'WebView2InstallerPath' -Value $WebView2OfflineInstallerPath),
        (New-IsppStringDefine -Name 'WebView2MinimumVersion' -Value $constants.dependencyBaseline.webView2Runtime.minimumVersion),
        "/DRequiredSpaceBytes=$requiredSpaceBytes",
        (New-IsppStringDefine -Name 'OutputDirectory' -Value $releaseDirectory),
        (New-IsppStringDefine -Name 'OutputBaseFilename' -Value $outputBaseFilename),
        (New-IsppStringDefine -Name 'SignedUninstallerDirectory' -Value $signedUninstallerDirectory),
        (New-IsppStringDefine -Name 'MaintenanceHelperSha256' -Value $maintenanceHelperSha256),
        (New-IsppStringDefine -Name 'IdentityHelperSha256' -Value $identityHelperSha256),
        (New-IsppStringDefine -Name 'InstallOwnershipMarkerSha256' -Value $installOwnershipMarkerSha256),
        (New-IsppStringDefine -Name 'WebView2HealthHelperSha256' -Value $webView2HealthHelperSha256),
        (New-IsppStringDefine -Name 'WebView2CoreAssemblySha256' -Value $webView2CoreAssemblySha256),
        (New-IsppStringDefine -Name 'WebView2LoaderSha256' -Value $webView2LoaderSha256),
        $innoScript
    )

    Write-Host '[package] 编译并由 Inno Setup 签名安装器与卸载器。'
    Invoke-DshNative -FilePath $InnoCompilerPath -Arguments $compilerArguments -WorkingDirectory $repositoryRoot
    foreach ($frozenInstallerInput in @(
        @{ Path = $maintenanceHelperPath; Hash = $maintenanceHelperSha256; Description = '当前用户 IPC 辅助程序' },
        @{ Path = $identityHelperPath; Hash = $identityHelperSha256; Description = '安装身份验证辅助程序' },
        @{ Path = $installOwnershipMarkerPath; Hash = $installOwnershipMarkerSha256; Description = '安装所有权标记' },
        @{ Path = $webView2HealthHelperPath; Hash = $webView2HealthHelperSha256; Description = 'WebView2 健康探测辅助程序' },
        @{ Path = $webView2CoreAssemblyPath; Hash = $webView2CoreAssemblySha256; Description = 'WebView2 Core managed assembly' },
        @{ Path = $webView2LoaderPath; Hash = $webView2LoaderSha256; Description = 'WebView2 native loader' }
    )) {
        Assert-FileHash `
            -Path $frozenInstallerInput.Path `
            -ExpectedHash $frozenInstallerInput.Hash `
            -Description $frozenInstallerInput.Description
    }

    $installerPath = Join-Path $releaseDirectory $installerName
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "Inno Setup 未生成预期安装包：$installerPath"
    }
    $installerSignature = Get-DshAuthenticodeEvidence `
        -Path $installerPath `
        -ExpectedSubject $constants.distribution.signing.certificateSubject `
        -RequireTimestamp

    $uninstallerCacheFiles = @(Get-ChildItem -LiteralPath $signedUninstallerDirectory -File)
    if ($uninstallerCacheFiles.Count -eq 0) {
        throw 'Inno Setup 没有生成已签名卸载器缓存。'
    }

    $uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4440FC88-98CA-403E-8E20-3DFEBEF0E609}_is1'
    if (Test-Path -LiteralPath $uninstallRegistryPath) {
        throw '卸载器验证必须在未安装本产品的干净隔离 Windows runner 上执行。'
    }
    $UninstallerVerificationDirectory = [IO.Path]::GetFullPath($UninstallerVerificationDirectory)
    if (Test-Path -LiteralPath $UninstallerVerificationDirectory) {
        if (@(Get-ChildItem -LiteralPath $UninstallerVerificationDirectory -Force).Count -gt 0) {
            throw "卸载器验证目录必须不存在或为空：$UninstallerVerificationDirectory"
        }
    }

    Write-Host '[package] 在显式授权的隔离目录安装最终包并验证真实卸载器。'
    Invoke-DshNative -FilePath $installerPath -WorkingDirectory $releaseDirectory -Arguments @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NORESTARTAPPLICATIONS',
        "/DIR=$UninstallerVerificationDirectory"
    )
    $installedApplicationPath = Join-Path $UninstallerVerificationDirectory $constants.product.executableName
    if (-not (Test-Path -LiteralPath $installedApplicationPath -PathType Leaf)) {
        throw '验证安装后未找到主程序。'
    }
    if ((Get-DshSha256 -Path $installedApplicationPath) -ne (Get-DshSha256 -Path $applicationExe)) {
        throw '已安装主程序与签名发布输入的 SHA-256 不一致。'
    }
    $installedApplicationSignature = Get-DshAuthenticodeEvidence `
        -Path $installedApplicationPath `
        -ExpectedSubject $constants.distribution.signing.certificateSubject `
        -RequireTimestamp

    $installedWebViewBootstrapperPath = Join-Path $UninstallerVerificationDirectory $webViewBootstrapperName
    if (-not (Test-Path -LiteralPath $installedWebViewBootstrapperPath -PathType Leaf) -or
        (Get-DshSha256 -Path $installedWebViewBootstrapperPath) -ne (Get-DshSha256 -Path $stagedWebViewBootstrapperPath)) {
        throw '已安装 WebView2 Bootstrapper 缺失或与冻结暂存输入不一致。'
    }
    $installedWebViewBootstrapperSignature = Get-DshAuthenticodeEvidence `
        -Path $installedWebViewBootstrapperPath `
        -ExpectedSubject $webViewBootstrapperSignature.SignerSubject `
        -RequireTimestamp

    $uninstallerPaths = @(Get-ChildItem -LiteralPath $UninstallerVerificationDirectory -Filter 'unins*.exe' -File)
    if ($uninstallerPaths.Count -ne 1) {
        throw "验证安装后卸载器数量必须为 1，实际为 $($uninstallerPaths.Count)。"
    }
    $uninstallerPath = $uninstallerPaths[0].FullName
    $uninstallerSignature = Get-DshAuthenticodeEvidence `
        -Path $uninstallerPath `
        -ExpectedSubject $constants.distribution.signing.certificateSubject `
        -RequireTimestamp

    Invoke-DshNative -FilePath $uninstallerPath -WorkingDirectory $UninstallerVerificationDirectory -Arguments @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    )
    Start-Sleep -Milliseconds 750
    if (Test-Path -LiteralPath $uninstallRegistryPath) {
        throw "验证卸载未清除卸载登记：$uninstallRegistryPath"
    }
    if (Test-Path -LiteralPath $UninstallerVerificationDirectory) {
        $residual = @(Get-ChildItem -LiteralPath $UninstallerVerificationDirectory -Force)
        if ($residual.Count -gt 0) {
            throw "验证卸载留下程序文件：$UninstallerVerificationDirectory"
        }
    }

    $installerHash = Get-DshSha256 -Path $installerPath
    $hashPath = "$installerPath.sha256"
    "$installerHash *$installerName" | Set-Content -LiteralPath $hashPath -Encoding ascii -NoNewline

    $releaseNotesPath = Join-Path $releaseDirectory 'release-notes-input.md'
    @(
        "# $($constants.product.name) $Version",
        '',
        "- Installer: $installerName",
        "- SHA-256: $installerHash",
        "- Runtime: self-contained $($constants.build.runtimeIdentifier), multi-file, non-trimmed .NET $($constants.build.dotnetSdkVersion)",
        "- WebView2 SDK: $($constants.dependencyBaseline.webView2Sdk.version)",
        "- Minimum WebView2 Runtime: $($constants.dependencyBaseline.webView2Runtime.minimumVersion)",
        "- Included WebView2 offline installer: $webViewVersion",
        "- Included WebView2 repair bootstrapper: $webViewBootstrapperVersion",
        "- Harness baseline: $($constants.dependencyBaseline.harness.version) ($($constants.dependencyBaseline.harness.commit))",
        "- LAN plugin baseline: $($constants.dependencyBaseline.lanPlugin.package) $($constants.dependencyBaseline.lanPlugin.version)",
        '',
        'Review `sbom.spdx.json`, `third-party-licenses.json`, `verify-summary.json`, and the completed `release-evidence.md` before any separately authorized publication.'
    ) | Set-Content -LiteralPath $releaseNotesPath -Encoding utf8NoBOM

    $finalSourceState = Get-DshSourceState -RepositoryRoot $repositoryRoot
    if (-not $finalSourceState.Available -or
        $finalSourceState.Commit -cne $sourceState.Commit -or
        $finalSourceState.WorktreeState -cne $sourceState.WorktreeState) {
        throw '正式打包期间源提交或工作树状态发生变化。'
    }
    $verifySummaryDestination = Join-Path $releaseDirectory 'verify-summary.json'
    Copy-Item -LiteralPath $verifySummarySource -Destination $verifySummaryDestination

    $manifest = [ordered]@{
        schemaVersion = 1
        frozenAtUtc = [DateTime]::UtcNow.ToString('O')
        version = $Version
        source = [ordered]@{
            available = $sourceState.Available
            commit = $sourceState.Commit
            worktreeState = $sourceState.WorktreeState
        }
        installer = [ordered]@{
            path = $installerPath
            size = (Get-Item -LiteralPath $installerPath).Length
            sha256 = $installerHash
            signature = $installerSignature
        }
        application = [ordered]@{
            path = $applicationExe
            sha256 = Get-DshSha256 -Path $applicationExe
            signature = $applicationSignature
            verifiedInstalledPath = $installedApplicationSignature.Path
            installedSignature = $installedApplicationSignature
        }
        uninstaller = [ordered]@{
            verifiedInstalledPath = $uninstallerSignature.Path
            signature = $uninstallerSignature
            verificationDirectory = $UninstallerVerificationDirectory
        }
        inputs = [ordered]@{
            dotnetSdk = $constants.build.dotnetSdkVersion
            innoCompilerVersion = $innoVersion
            innoSetupInstallerSha256 = Get-DshSha256 -Path $InnoSetupInstallerPath
            innoSetupInstallerSignature = $innoInstallerSignature
            innoCompilerSignature = $innoCompilerSignature
            innoInstallationUninstallerSignature = $innoInstallationUninstallerSignature
            webView2OfflineInstallerVersion = $webViewVersion
            webView2OfflineInstallerSha256 = Get-DshSha256 -Path $WebView2OfflineInstallerPath
            webView2OfflineInstallerSignature = $webViewSignature
            webView2BootstrapperVersion = $webViewBootstrapperVersion
            webView2BootstrapperSha256 = Get-DshSha256 -Path $WebView2BootstrapperPath
            webView2BootstrapperSignature = $webViewBootstrapperSignature
            stagedWebView2BootstrapperPath = $stagedWebViewBootstrapperPath
            stagedWebView2BootstrapperSignature = $stagedWebViewBootstrapperSignature
            verifiedInstalledWebView2BootstrapperPath = $installedWebViewBootstrapperSignature.Path
            installedWebView2BootstrapperSignature = $installedWebViewBootstrapperSignature
            signToolSignature = $signToolEvidence
            certificateSubject = $certificate.Subject
            certificateThumbprint = $certificate.Thumbprint
            maintenanceHelperSha256 = $maintenanceHelperSha256
            identityHelperSha256 = $identityHelperSha256
            installOwnershipMarkerSha256 = $installOwnershipMarkerSha256
            webView2HealthHelperSha256 = $webView2HealthHelperSha256
            webView2CoreAssemblySha256 = $webView2CoreAssemblySha256
            webView2CoreAssemblySignature = $webView2CoreAssemblySignature
            webView2LoaderSha256 = $webView2LoaderSha256
            webView2LoaderSignature = $webView2LoaderSignature
        }
        verifySummary = [ordered]@{
            path = $verifySummaryDestination
            sha256 = Get-DshSha256 -Path $verifySummaryDestination
        }
        supplyChain = [ordered]@{
            sbomPath = $sbomPath
            sbomSha256 = Get-DshSha256 -Path $sbomPath
            licenseInventoryPath = $licenseInventoryPath
            licenseInventorySha256 = Get-DshSha256 -Path $licenseInventoryPath
            releaseNotesInputPath = $releaseNotesPath
            releaseNotesInputSha256 = Get-DshSha256 -Path $releaseNotesPath
        }
        signedUninstallerCache = @(
            $uninstallerCacheFiles | ForEach-Object {
                [ordered]@{
                    name = $_.Name
                    sha256 = Get-DshSha256 -Path $_.FullName
                }
            }
        )
    }
    $manifestPath = Join-Path $releaseDirectory 'package-manifest.json'
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    if ((Get-DshSha256 -Path $installerPath) -ne $installerHash) {
        throw '冻结 SHA-256 后安装包字节发生变化。'
    }

    Write-Host "package.ps1: PASS`nInstaller: $installerPath`nSHA-256: $installerHash"
}
catch {
    Write-Error "package.ps1: FAIL`n$($_.Exception.Message)"
    exit 1
}
