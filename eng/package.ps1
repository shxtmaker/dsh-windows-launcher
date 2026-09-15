[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [ValidateSet('Offline', 'Online')]
    [string] $PackageMode = 'Offline',

    [string] $WebView2OfflineInstallerPath,

    [Parameter(Mandatory)]
    [string] $WebView2BootstrapperPath,

    [Parameter(Mandatory)]
    [string] $InnoSetupInstallerPath,

    [string] $InnoCompilerPath,

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

function Assert-Unsigned {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $Description
    )

    # 正式包不再签名：这里反向卡控，确保没有陈旧/意外签名混入发布产物。
    $signature = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "$Description 必须是 NotSigned，实际状态为 $($signature.Status)。"
    }

    return [pscustomobject][ordered]@{
        Status = 'NotSigned'
        Path = $Path
    }
}

try {
    if (-not (Test-DshSemVer -Version $Version)) {
        throw "-Version 必须是明确 SemVer：$Version"
    }

    if (-not $AllowInstallerExecutionForUninstallerVerification) {
        throw '正式打包必须显式传入 -AllowInstallerExecutionForUninstallerVerification，在干净隔离 Windows runner 安装最终包并验证真实卸载器。'
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

    $includeOfflineRuntime = $PackageMode -eq 'Offline'
    $inputPaths = @($WebView2BootstrapperPath, $InnoSetupInstallerPath)
    if ($includeOfflineRuntime) {
        if ([string]::IsNullOrWhiteSpace($WebView2OfflineInstallerPath)) {
            throw '离线包必须提供 -WebView2OfflineInstallerPath。'
        }
        $inputPaths += $WebView2OfflineInstallerPath
    }
    foreach ($path in $inputPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "输入文件不存在：$path"
        }
    }

    $WebView2BootstrapperPath = (Resolve-Path -LiteralPath $WebView2BootstrapperPath).Path
    $InnoSetupInstallerPath = (Resolve-Path -LiteralPath $InnoSetupInstallerPath).Path

    $webViewSignature = $null
    $webViewVersion = $null
    $webViewOfflineHash = $null
    if ($includeOfflineRuntime) {
        $WebView2OfflineInstallerPath = (Resolve-Path -LiteralPath $WebView2OfflineInstallerPath).Path
        # WebView2 Evergreen Standalone x64 冻结哈希。
        # 2026-09-04 供应链基线修正：微软对同一版本 1.3.265.7 重新发布/更换签名渠道，
        # 实得文件（258,510,544 bytes）Microsoft 签名有效且带可信时间戳、FileVersion 仍为 1.3.265.7，
        # 但字节与旧 pin 不同。旧值：987a9d8b3107e84f9b53b4a077d28ae4814fc3d964d5a55c559e7334bbf24d61
        Assert-FileHash -Path $WebView2OfflineInstallerPath `
            -ExpectedHash '1f4638309f3d82c31a3028c3cf7d75998f58e4d1407380f5cb8a8e9172caf17d' `
            -Description 'WebView2 Offline Installer'
        $webViewSignature = Assert-SignedMicrosoftTool -Path $WebView2OfflineInstallerPath -Description 'WebView2 Offline Installer'
        $webViewVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo($WebView2OfflineInstallerPath).ProductVersion).Trim()
        if ($webViewVersion -ne '1.3.265.7') {
            throw "WebView2 Offline Installer 版本不匹配。实际：$webViewVersion；预期：" + '1.3.265.7'
        }
        $webViewOfflineHash = Get-DshSha256 -Path $WebView2OfflineInstallerPath
    }

    Assert-FileHash -Path $WebView2BootstrapperPath `
        -ExpectedHash '17debf797a6c737959bc588236e897936ffac1af5f7e515e674ab32f9edfe719' `
        -Description 'WebView2 Evergreen Bootstrapper'
    $webViewBootstrapperSignature = Assert-SignedMicrosoftTool `
        -Path $WebView2BootstrapperPath `
        -Description 'WebView2 Evergreen Bootstrapper'
    $webViewBootstrapperVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo($WebView2BootstrapperPath).ProductVersion).Trim()
    if ([string]::IsNullOrWhiteSpace($webViewBootstrapperVersion)) {
        $webViewBootstrapperVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo($WebView2BootstrapperPath).FileVersion).Trim()
    }
    if ($webViewBootstrapperVersion -ne '1.3.265.7') {
        throw "WebView2 Evergreen Bootstrapper 版本不匹配。实际：$webViewBootstrapperVersion；预期：" + '1.3.265.7'
    }

    $runtimeInstallerPath = if ($includeOfflineRuntime) { $WebView2OfflineInstallerPath } else { $WebView2BootstrapperPath }
    $runtimeInstallerHash = Get-DshSha256 -Path $runtimeInstallerPath
    $runtimeDescription = if ($includeOfflineRuntime) { '离线包' } else { '联网精简包' }

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

    $dotnet = Get-DshExactDotNetPath -ExpectedVersion $constants.build.dotnetSdkVersion -DotNetPath $DotNetPath
    $verifyArtifacts = Join-Path $repositoryRoot "artifacts/verify-package-$Version-$($PackageMode.ToLowerInvariant())"
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
    # 结构断言：拒绝 eng/verify-portable.ps1 的可移植开发摘要冒充发行验证。
    $null = Assert-DshWindowsVerifySummary -Path $verifySummarySource
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
    $pairingIdentity = $verifySummary.pairingIdentity
    if ($null -eq $pairingIdentity -or
        [string] $pairingIdentity.plugin -cne $constants.pairingBaseline.plugin -or
        [string] $pairingIdentity.referenceVersion -cne [string] $constants.pairingBaseline.referenceVersion -or
        [string] $pairingIdentity.cookieName -cne $constants.pairingBaseline.cookieName) {
        throw '统一验证摘要缺少与发布常量一致的配对基线身份。'
    }
    if ($null -eq $verifySummary.verificationImpact -or
        @($verifySummary.verificationImpact.requiredRs).Count -eq 0) {
        throw '统一验证摘要缺少 fail-closed 验证影响证据。'
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = if ($includeOfflineRuntime) {
            Join-Path $repositoryRoot "artifacts/package/$Version"
        } else {
            Join-Path $repositoryRoot "artifacts/package/$Version-online"
        }
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
    $releaseDirectory = Join-Path $OutputDirectory 'release'
    foreach ($directory in @($stagingDirectory, $publishDirectory, $releaseDirectory)) {
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
    Assert-DshSbomPairingIdentity `
        -Path $sbomPath `
        -PairingIdentity $pairingIdentity

    $fileVersion = ConvertTo-DshFileVersion -Version $Version
    $desktopProject = Join-Path $repositoryRoot 'src/DshLauncher.Desktop/DshLauncher.Desktop.csproj'
    Write-Host '[package] locked restore Official 构建身份。'
    Invoke-DshNative -FilePath $dotnet -WorkingDirectory $repositoryRoot -Arguments @(
        'restore', $desktopProject,
        '--locked-mode',
        '-p:LauncherBuildFlavor=Official'
    )
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
        '-p:LauncherBuildFlavor=Official',
        "-p:Version=$Version",
        "-p:FileVersion=$fileVersion",
        "-p:InformationalVersion=$Version"
    )

    $webViewBootstrapperName = 'MicrosoftEdgeWebview2Setup.exe'
    $stagedWebViewBootstrapperPath = Join-Path $publishDirectory $webViewBootstrapperName
    Copy-Item -LiteralPath $WebView2BootstrapperPath -Destination $stagedWebViewBootstrapperPath
    Assert-FileHash `
        -Path $stagedWebViewBootstrapperPath `
        -ExpectedHash '17debf797a6c737959bc588236e897936ffac1af5f7e515e674ab32f9edfe719' `
        -Description '暂存 WebView2 Evergreen Bootstrapper'
    $stagedWebViewBootstrapperSignature = Get-DshAuthenticodeEvidence `
        -Path $stagedWebViewBootstrapperPath `
        -ExpectedSubject $webViewBootstrapperSignature.SignerSubject `
        -RequireTimestamp

    $applicationExe = Join-Path $publishDirectory $constants.product.executableName
    $managedEntryAssembly = Join-Path $publishDirectory 'DshWindowsLauncher.dll'
    if (-not (Test-Path -LiteralPath $applicationExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $managedEntryAssembly -PathType Leaf)) {
        throw '发布结果缺少 apphost EXE 或托管主程序集。'
    }
    if (@(Get-ChildItem -LiteralPath $publishDirectory -File).Count -lt 2) {
        throw '发布结果不是预期的自包含多文件布局。'
    }

    $publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationExe).FileVersion
    if (-not $publishedVersion.StartsWith($fileVersion, [StringComparison]::OrdinalIgnoreCase)) {
        throw "主程序文件版本不匹配。实际：$publishedVersion；预期：$fileVersion"
    }

    Write-Host '[package] 断言 apphost 与托管主程序集保持未签名。'
    $applicationUnsigned = Assert-Unsigned -Path $applicationExe -Description '主程序 apphost'
    $managedEntryAssemblyUnsigned = Assert-Unsigned -Path $managedEntryAssembly -Description '托管主程序集'

    $publishBytes = (Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Measure-Object -Property Length -Sum).Sum
    # Online repair still needs space for the downloaded runtime, not only the bootstrapper.
    $webViewInstallerBytes = if ($includeOfflineRuntime) {
        (Get-Item -LiteralPath $runtimeInstallerPath).Length
    } else { 268435456 }
    $requiredSpaceBytes = [int64] ($publishBytes * 2 + $webViewInstallerBytes + 268435456)
    $installerName = $constants.distribution.installerFileNameTemplate.
        Replace('{Product}', $constants.product.name, [StringComparison]::Ordinal).
        Replace('{SemVer}', $Version, [StringComparison]::Ordinal)
    if (-not $includeOfflineRuntime) {
        $installerName = [IO.Path]::GetFileNameWithoutExtension($installerName) + '-online.exe'
    }
    if ($installerName -notmatch '^[^\\/:*?"<>|]+\.exe$') {
        throw "安装包文件名不安全：$installerName"
    }
    $outputBaseFilename = [IO.Path]::GetFileNameWithoutExtension($installerName)

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
        (New-IsppStringDefine -Name 'SourceRoot' -Value $publishDirectory),
        (New-IsppStringDefine -Name 'AppVersion' -Value $Version),
        (New-IsppStringDefine -Name 'FileVersion' -Value $fileVersion),
        (New-IsppStringDefine -Name 'Publisher' -Value $constants.distribution.signing.publisher),
        (New-IsppStringDefine -Name 'ReleaseUri' -Value $constants.distribution.officialReleaseUri),
        (New-IsppStringDefine -Name 'WebView2InstallerPath' -Value $runtimeInstallerPath),
        (New-IsppStringDefine -Name 'WebView2InstallerSha256' -Value $runtimeInstallerHash),
        (New-IsppStringDefine -Name 'PackageMode' -Value $PackageMode),
        (New-IsppStringDefine -Name 'WebView2MinimumVersion' -Value '151.0.4129.50'),
        "/DRequiredSpaceBytes=$requiredSpaceBytes",
        (New-IsppStringDefine -Name 'OutputDirectory' -Value $releaseDirectory),
        (New-IsppStringDefine -Name 'OutputBaseFilename' -Value $outputBaseFilename),
        (New-IsppStringDefine -Name 'MaintenanceHelperSha256' -Value $maintenanceHelperSha256),
        (New-IsppStringDefine -Name 'IdentityHelperSha256' -Value $identityHelperSha256),
        (New-IsppStringDefine -Name 'InstallOwnershipMarkerSha256' -Value $installOwnershipMarkerSha256),
        (New-IsppStringDefine -Name 'WebView2HealthHelperSha256' -Value $webView2HealthHelperSha256),
        (New-IsppStringDefine -Name 'WebView2CoreAssemblySha256' -Value $webView2CoreAssemblySha256),
        (New-IsppStringDefine -Name 'WebView2LoaderSha256' -Value $webView2LoaderSha256),
        $innoScript
    )

    Write-Host '[package] 编译未签名安装器与卸载器。'
    Invoke-DshNative -FilePath $InnoCompilerPath -Arguments $compilerArguments -WorkingDirectory $repositoryRoot
    foreach ($frozenInstallerInput in @(
        @{ Path = $runtimeInstallerPath; Hash = $runtimeInstallerHash; Description = 'WebView2 安装程序' },
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
    $installerUnsigned = Assert-Unsigned -Path $installerPath -Description '安装包'

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
        throw '已安装主程序与发布输入的 SHA-256 不一致。'
    }
    $installedApplicationUnsigned = Assert-Unsigned -Path $installedApplicationPath -Description '已安装主程序'

    $installedManagedEntryAssembly = Join-Path $UninstallerVerificationDirectory 'DshWindowsLauncher.dll'
    if (-not (Test-Path -LiteralPath $installedManagedEntryAssembly -PathType Leaf) -or
        (Get-DshSha256 -Path $installedManagedEntryAssembly) -cne (Get-DshSha256 -Path $managedEntryAssembly)) {
        throw '已安装托管主程序集缺失或与发布输入不一致。'
    }
    $installedManagedEntryAssemblyUnsigned = Assert-Unsigned `
        -Path $installedManagedEntryAssembly `
        -Description '已安装托管主程序集'

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
    $uninstallerUnsigned = Assert-Unsigned -Path $uninstallerPath -Description '已安装卸载器'
    $uninstallerHash = Get-DshSha256 -Path $uninstallerPath

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
        "- Authenticode: not signed (internal distribution; verify by the SHA-256 above)",
        "- Minimum WebView2 Runtime: $('151.0.4129.50')",
        "- Package mode: $PackageMode ($runtimeDescription)",
        "- Includes WebView2 offline runtime: $includeOfflineRuntime",
        "- Included WebView2 repair bootstrapper: $webViewBootstrapperVersion",
        "- Pairing baseline: $($constants.pairingBaseline.plugin) $($constants.pairingBaseline.referenceVersion)",
        "- Pairing plugin: $($pairingIdentity.plugin) ($($pairingIdentity.referenceVersion))",
        "- Pairing cookie: $($pairingIdentity.cookieName)",
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
        schemaVersion = 3
        frozenAtUtc = [DateTime]::UtcNow.ToString('O')
        signingPolicy = $constants.distribution.signing.policy
        ownArtifactsSigned = $false
        version = $Version
        packageMode = $PackageMode
        includesOfflineWebView2 = $includeOfflineRuntime
        source = [ordered]@{
            available = $sourceState.Available
            commit = $sourceState.Commit
            worktreeState = $sourceState.WorktreeState
        }
        pairingIdentity = $pairingIdentity
        verificationImpact = $verifySummary.verificationImpact
        installer = [ordered]@{
            path = $installerPath
            size = (Get-Item -LiteralPath $installerPath).Length
            sha256 = $installerHash
            authenticodeStatus = $installerUnsigned.Status
        }
        application = [ordered]@{
            path = $applicationExe
            sha256 = Get-DshSha256 -Path $applicationExe
            authenticodeStatus = $applicationUnsigned.Status
            verifiedInstalledPath = $installedApplicationUnsigned.Path
            installedAuthenticodeStatus = $installedApplicationUnsigned.Status
        }
        managedEntryAssembly = [ordered]@{
            path = $managedEntryAssembly
            sha256 = Get-DshSha256 -Path $managedEntryAssembly
            authenticodeStatus = $managedEntryAssemblyUnsigned.Status
            verifiedInstalledPath = $installedManagedEntryAssemblyUnsigned.Path
            installedAuthenticodeStatus = $installedManagedEntryAssemblyUnsigned.Status
        }
        uninstaller = [ordered]@{
            verifiedInstalledPath = $uninstallerUnsigned.Path
            sha256 = $uninstallerHash
            authenticodeStatus = $uninstallerUnsigned.Status
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
            webView2OfflineInstallerSha256 = $webViewOfflineHash
            webView2OfflineInstallerSignature = $webViewSignature
            webView2BootstrapperVersion = $webViewBootstrapperVersion
            webView2BootstrapperSha256 = Get-DshSha256 -Path $WebView2BootstrapperPath
            webView2BootstrapperSignature = $webViewBootstrapperSignature
            stagedWebView2BootstrapperPath = $stagedWebViewBootstrapperPath
            stagedWebView2BootstrapperSignature = $stagedWebViewBootstrapperSignature
            verifiedInstalledWebView2BootstrapperPath = $installedWebViewBootstrapperSignature.Path
            installedWebView2BootstrapperSignature = $installedWebViewBootstrapperSignature
            ownArtifactsSigned = $false
            signingPolicy = $constants.distribution.signing.policy
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
        unsignedArtifactSet = [ordered]@{
            apphost = [ordered]@{
                path = $applicationExe
                sha256 = Get-DshSha256 -Path $applicationExe
                authenticodeStatus = $applicationUnsigned.Status
            }
            managedEntryAssembly = [ordered]@{
                path = $managedEntryAssembly
                sha256 = Get-DshSha256 -Path $managedEntryAssembly
                authenticodeStatus = $managedEntryAssemblyUnsigned.Status
            }
            uninstaller = [ordered]@{
                path = $uninstallerUnsigned.Path
                sha256 = $uninstallerHash
                authenticodeStatus = $uninstallerUnsigned.Status
            }
            installer = [ordered]@{
                path = $installerPath
                sha256 = $installerHash
                authenticodeStatus = $installerUnsigned.Status
            }
        }
        supplyChain = [ordered]@{
            sbomPath = $sbomPath
            sbomSha256 = Get-DshSha256 -Path $sbomPath
            licenseInventoryPath = $licenseInventoryPath
            licenseInventorySha256 = Get-DshSha256 -Path $licenseInventoryPath
            releaseNotesInputPath = $releaseNotesPath
            releaseNotesInputSha256 = Get-DshSha256 -Path $releaseNotesPath
        }
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
