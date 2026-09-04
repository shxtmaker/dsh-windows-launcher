[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $WebView2BootstrapperPath,

    [string] $InnoCompilerPath,

    [string] $DotNetPath,

    [string] $OutputDirectory,

    [switch] $OfficialUnsignedRelease
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')

$internalVersionPattern = '^\d+\.\d+\.\d+-internal-test\.[1-9]\d*$'
$internalProductName = 'DSH Windows Launcher (INTERNAL TEST)'
$internalExecutableName = 'DshWindowsLauncher.InternalTest.exe'
$internalApplicationDllName = 'DshWindowsLauncher.dll'
$internalApplicationDataId = 'DshWindowsLauncher.InternalTest'
$internalSingleInstanceBaseName = 'DshWindowsLauncher.InternalTest.SingleInstance'
$internalInstallDirectoryName = 'DshWindowsLauncher.InternalTest'
$internalAppId = 'F3418DD7-58B7-4E0D-B0F7-D77C52FDF91C'
$officialProductName = 'DSH Windows Launcher'
$officialExecutableName = 'DshWindowsLauncher.exe'
$officialApplicationDataId = 'DshWindowsLauncher'
$officialSingleInstanceBaseName = 'DshWindowsLauncher.SingleInstance'
$officialInstallDirectoryName = 'DshWindowsLauncher'
$officialAppId = '4440FC88-98CA-403E-8E20-3DFEBEF0E609'
$allowedMicrosoftSignerSubjects = @(
    'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
)
$allowedPyrsysSignerSubjects = @(
    'CN=Pyrsys B.V., O=Pyrsys B.V., S=Noord-Holland, C=NL'
)

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
    if ($actualHash -cne $ExpectedHash.ToUpperInvariant()) {
        throw "$Description SHA-256 不匹配。"
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
    $subjectAllowed = $false
    foreach ($allowedSubject in $script:allowedMicrosoftSignerSubjects) {
        if ([string]::Equals(
                $evidence.SignerSubject,
                $allowedSubject,
                [StringComparison]::Ordinal)) {
            $subjectAllowed = $true
            break
        }
    }
    if (-not $subjectAllowed) {
        throw "$Description 签名证书主体不在 Microsoft 精确允许列表中。"
    }

    return $evidence
}

function Assert-SignedPyrsysTool {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $Description
    )

    $evidence = Get-DshAuthenticodeEvidence -Path $Path -RequireTimestamp
    $subjectAllowed = $false
    foreach ($allowedSubject in $script:allowedPyrsysSignerSubjects) {
        if ([string]::Equals(
                $evidence.SignerSubject,
                $allowedSubject,
                [StringComparison]::Ordinal)) {
            $subjectAllowed = $true
            break
        }
    }
    if (-not $subjectAllowed) {
        throw "$Description 签名证书主体不在 Pyrsys B.V. 精确允许列表中。"
    }

    return $evidence
}

function Get-InnoCompilerVersion {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Path
    $startInfo.WorkingDirectory = Split-Path -Parent $Path
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add('/O-')
    $startInfo.ArgumentList.Add('-')

    $probeScript = @(
        '#pragma message "DSH_INNO_VERSION=" + DecodeVer(VER, 4)'
        '[Setup]'
        'AppName=DSH Version Probe'
        'AppVersion=1'
        'DefaultDirName={tmp}\DshVersionProbe'
        'Uninstallable=no'
    ) -join "`r`n"

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "无法启动 Inno Setup 编译器版本探针：$Path"
        }
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine($probeScript)
        $process.StandardInput.Close()
        $process.WaitForExit()
        $combinedOutput =
            $standardOutputTask.GetAwaiter().GetResult() + "`n" +
            $standardErrorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Inno Setup 编译器版本探针失败，退出码 $($process.ExitCode)。"
        }

        $versionMatch = [regex]::Match(
            $combinedOutput,
            '(?m)^\s*Line\s+1:\s*DSH_INNO_VERSION=(\d+\.\d+\.\d+(?:\.\d+)?)\s*$')
        if (-not $versionMatch.Success) {
            throw '无法从 Inno Setup 编译器自身读取精确版本。'
        }

        return $versionMatch.Groups[1].Value
    }
    finally {
        $process.Dispose()
    }
}

function Assert-Unsigned {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $Description
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "$Description 必须明确保持未签名，实际状态为 $($signature.Status)。"
    }
}

function New-IsppStringDefine {
    param(
        [Parameter(Mandatory)]
        [string] $Name,
        [Parameter(Mandatory)]
        [string] $Value
    )

    if ($Value.Contains('"', [StringComparison]::Ordinal)) {
        throw "ISPP 参数不能包含双引号：$Name"
    }

    return "/D$Name=$Value"
}

try {
    $constants = Get-DshReleaseConstants -RepositoryRoot $repositoryRoot
    if ($OfficialUnsignedRelease) {
        if (-not (Test-DshSemVer -Version $Version) -or $Version.Contains('-', [StringComparison]::Ordinal)) {
            throw "正式未签名版本必须是无预发布后缀的 SemVer：$Version"
        }
        Assert-DshReleaseConstants -RepositoryRoot $repositoryRoot -Constants $constants -RequireCandidate
        if ($Version -cne $constants.product.version) {
            throw "正式版本必须与发布常量一致。参数：$Version；常量：$($constants.product.version)"
        }
        if ($constants.distribution.signing.policy -cne 'none') {
            throw '正式包要求 distribution.signing.policy=none。'
        }
    }
    else {
        if (-not (Test-DshSemVer -Version $Version) -or
            $Version -cnotmatch $internalVersionPattern) {
            throw "-Version 必须符合 x.y.z-internal-test.n，且 n 必须为正整数：$Version"
        }
        Assert-DshReleaseConstants -RepositoryRoot $repositoryRoot -Constants $constants
        if ($constants.releaseStatus -cne 'development') {
            throw "内部测试包只允许 releaseStatus=development，当前值为 $($constants.releaseStatus)。"
        }

        $baseVersion = $Version.Substring(
            0,
            $Version.IndexOf('-internal-test.', [StringComparison]::Ordinal))
        if ($baseVersion -cne $constants.product.version) {
            throw "内部测试版本的 x.y.z 必须与发布常量一致。参数：$baseVersion；常量：$($constants.product.version)"
        }
    }

    if (-not (Test-Path -LiteralPath $WebView2BootstrapperPath -PathType Leaf)) {
        throw "WebView2 Bootstrapper 不存在：$WebView2BootstrapperPath"
    }
    $WebView2BootstrapperPath = (Resolve-Path -LiteralPath $WebView2BootstrapperPath).Path
    Assert-FileHash `
        -Path $WebView2BootstrapperPath `
        -ExpectedHash '17debf797a6c737959bc588236e897936ffac1af5f7e515e674ab32f9edfe719' `
        -Description 'WebView2 Evergreen Bootstrapper'
    $webViewSignature = Assert-SignedMicrosoftTool `
        -Path $WebView2BootstrapperPath `
        -Description 'WebView2 Evergreen Bootstrapper'
    $webViewVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo(
            $WebView2BootstrapperPath).ProductVersion).Trim()
    if ([string]::IsNullOrWhiteSpace($webViewVersion)) {
        $webViewVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo(
                $WebView2BootstrapperPath).FileVersion).Trim()
    }
    if ($webViewVersion -cne '1.3.265.7') {
        throw 'WebView2 Evergreen Bootstrapper 版本不匹配。'
    }

    if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
        $InnoCompilerPath = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'
    }
    if (-not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) {
        throw "Inno Setup 编译器不存在：$InnoCompilerPath"
    }
    $InnoCompilerPath = (Resolve-Path -LiteralPath $InnoCompilerPath).Path
    $innoCompilerSignature = Assert-SignedPyrsysTool `
        -Path $InnoCompilerPath `
        -Description 'Inno Setup 编译器'
    $innoCompilerVersion = Get-InnoCompilerVersion -Path $InnoCompilerPath
    $innoIdentityPath = Join-Path (Split-Path -Parent $InnoCompilerPath) 'unins000.exe'
    if (-not (Test-Path -LiteralPath $innoIdentityPath -PathType Leaf)) {
        throw 'Inno Setup 安装目录缺少版本身份文件。'
    }
    $innoIdentitySignature = Assert-SignedPyrsysTool `
        -Path $innoIdentityPath `
        -Description 'Inno Setup 安装身份文件'
    $innoIdentityVersion = ([string] [Diagnostics.FileVersionInfo]::GetVersionInfo(
            $innoIdentityPath).ProductVersion).Trim()
    if ($innoIdentityVersion -cnotmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw '无法读取 Inno Setup 实际安装版本。'
    }
    if ($innoIdentityVersion -cne $innoCompilerVersion) {
        throw "Inno Setup 编译器与相邻安装身份版本不一致。编译器：$innoCompilerVersion；安装身份：$innoIdentityVersion"
    }
    if (-not [string]::Equals(
            $innoCompilerSignature.SignerThumbprint,
            $innoIdentitySignature.SignerThumbprint,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Inno Setup 编译器与相邻安装身份的签名证书不一致。'
    }

    $dotnet = Get-DshExactDotNetPath `
        -ExpectedVersion $constants.build.dotnetSdkVersion `
        -DotNetPath $DotNetPath

    $sourceState = Get-DshSourceState -RepositoryRoot $repositoryRoot
    if (-not $sourceState.Available -or
        $sourceState.Commit -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
        $sourceState.WorktreeState -cne 'clean') {
        throw '打包只接受可用、提交 ID 有效且工作树为 clean 的 Git 源状态。'
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $packageRoot = if ($OfficialUnsignedRelease) { 'artifacts/package' } else { 'artifacts/package-internal' }
        $OutputDirectory = Join-Path $repositoryRoot "$packageRoot/$Version"
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

    $verifyDirectory = Join-Path $OutputDirectory 'verify'
    $stagingDirectory = Join-Path $OutputDirectory 'staging'
    $publishDirectory = Join-Path $stagingDirectory 'publish'
    $releaseDirectory = Join-Path $OutputDirectory 'release'
    foreach ($directory in @(
            $verifyDirectory,
            $stagingDirectory,
            $publishDirectory,
            $releaseDirectory)) {
        $null = New-Item -ItemType Directory -Path $directory
    }

    $pwshExecutable = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshExecutable -PathType Leaf)) {
        $pwshExecutable = (Get-Command -Name pwsh -CommandType Application -ErrorAction Stop).Source
    }
    Write-Host '[package] 执行统一 verify 门禁。'
    Invoke-DshNative -FilePath $pwshExecutable -WorkingDirectory $repositoryRoot -Arguments @(
        '-NoLogo', '-NoProfile', '-NonInteractive',
        '-File', (Join-Path $PSScriptRoot 'verify.ps1'),
        '-DotNetPath', $dotnet,
        '-ArtifactsDirectory', $verifyDirectory
    )

    $verifySummarySource = Join-Path $verifyDirectory 'verify-summary.json'
    if (-not (Test-Path -LiteralPath $verifySummarySource -PathType Leaf)) {
        throw '统一验证未生成 verify-summary.json。'
    }
    $verifySummary = Get-Content -LiteralPath $verifySummarySource -Raw -Encoding UTF8 |
        ConvertFrom-Json -Depth 20
    if ($verifySummary.result -cne 'PASS') {
        throw '统一验证摘要不是 PASS。'
    }
    if ($null -eq $verifySummary.source -or
        $verifySummary.source.available -ne $sourceState.Available -or
        $verifySummary.source.commit -cne $sourceState.Commit -or
        $verifySummary.source.worktreeState -cne $sourceState.WorktreeState) {
        throw '统一验证摘要未绑定当前 clean 提交。'
    }
    if ($null -eq $verifySummary.verificationImpact -or
        @($verifySummary.verificationImpact.requiredRs).Count -eq 0) {
        throw '统一验证摘要缺少 fail-closed 验证影响证据。'
    }

    $pairingIdentity = $verifySummary.pairingIdentity
    if ($null -eq $pairingIdentity -or
        [string] $pairingIdentity.plugin -cne $constants.pairingBaseline.plugin -or
        [string] $pairingIdentity.referenceVersion -cne [string] $constants.pairingBaseline.referenceVersion -or
        [string] $pairingIdentity.cookieName -cne $constants.pairingBaseline.cookieName) {
        throw '统一验证摘要缺少与发布常量一致的配对基线身份。'
    }

    $fileVersion = ConvertTo-DshFileVersion -Version $Version
    $desktopProject = Join-Path $repositoryRoot 'src/DshLauncher.Desktop/DshLauncher.Desktop.csproj'
    $buildFlavor = if ($OfficialUnsignedRelease) { 'Official' } else { 'InternalTest' }
    $productName = if ($OfficialUnsignedRelease) { $officialProductName } else { $internalProductName }
    $executableName = if ($OfficialUnsignedRelease) { $officialExecutableName } else { $internalExecutableName }
    $applicationDataId = if ($OfficialUnsignedRelease) { $officialApplicationDataId } else { $internalApplicationDataId }
    $singleInstanceBaseName = if ($OfficialUnsignedRelease) { $officialSingleInstanceBaseName } else { $internalSingleInstanceBaseName }
    $installDirectoryName = if ($OfficialUnsignedRelease) { $officialInstallDirectoryName } else { $internalInstallDirectoryName }
    $appId = if ($OfficialUnsignedRelease) { $officialAppId } else { $internalAppId }
    $description = if ($OfficialUnsignedRelease) {
        'Official release; Authenticode status: NotSigned'
    }
    else {
        'UNSIGNED INTERNAL TEST - NOT FOR PRODUCTION USE'
    }
    Write-Host "[package] 发布 $buildFlavor 构建身份的未签名应用。"
    $publishArguments = @(
        'publish', $desktopProject,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '--output', $publishDirectory,
        "-p:LauncherBuildFlavor=$buildFlavor",
        "-p:Product=$productName",
        "-p:Description=$description",
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        "-p:Version=$Version",
        "-p:FileVersion=$fileVersion",
        "-p:InformationalVersion=$Version"
    )
    Invoke-DshNative -FilePath $dotnet -WorkingDirectory $repositoryRoot -Arguments $publishArguments

    $applicationExe = Join-Path $publishDirectory $executableName
    $applicationDll = Join-Path $publishDirectory $internalApplicationDllName
    if (-not (Test-Path -LiteralPath $applicationExe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $applicationDll -PathType Leaf)) {
        throw '发布结果缺少预期 EXE 或 DLL。'
    }
    $applicationVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationExe)
    if ($applicationVersion.ProductName -cne $productName) {
        throw '主程序 ProductName 不匹配。'
    }
    if ($applicationVersion.FileVersion -cne $fileVersion) {
        throw '主程序 FileVersion 不匹配。'
    }
    $productVersionPattern =
        '^' + [regex]::Escape($Version) +
        '(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
    if ($applicationVersion.ProductVersion -cnotmatch $productVersionPattern) {
        throw '主程序 ProductVersion 不匹配。'
    }
    Assert-Unsigned -Path $applicationExe -Description '主程序'
    Assert-Unsigned -Path $applicationDll -Description '托管主程序集'

    $bootstrapperName = 'MicrosoftEdgeWebview2Setup.exe'
    $stagedBootstrapperPath = Join-Path $publishDirectory $bootstrapperName
    Copy-Item -LiteralPath $WebView2BootstrapperPath -Destination $stagedBootstrapperPath
    Assert-FileHash `
        -Path $stagedBootstrapperPath `
        -ExpectedHash '17debf797a6c737959bc588236e897936ffac1af5f7e515e674ab32f9edfe719' `
        -Description '暂存 WebView2 Evergreen Bootstrapper'
    $stagedWebViewSignature = Assert-SignedMicrosoftTool `
        -Path $stagedBootstrapperPath `
        -Description '暂存 WebView2 Evergreen Bootstrapper'

    $installerName = if ($OfficialUnsignedRelease) {
        $constants.distribution.installerFileNameTemplate.
            Replace('{Product}', $constants.product.name, [StringComparison]::Ordinal).
            Replace('{SemVer}', $Version, [StringComparison]::Ordinal)
    }
    else {
        "DSH-Windows-Launcher-INTERNAL-TEST-UNSIGNED-NOT-FOR-PRODUCTION-USE-$Version-win-x64.exe"
    }
    $outputBaseFilename = [IO.Path]::GetFileNameWithoutExtension($installerName)
    $innoScriptName = if ($OfficialUnsignedRelease) {
        'DshWindowsLauncher.UnsignedRelease.iss'
    }
    else {
        'DshWindowsLauncher.InternalTest.iss'
    }
    $innoScript = Join-Path $repositoryRoot "installer/$innoScriptName"
    if (-not (Test-Path -LiteralPath $innoScript -PathType Leaf)) {
        throw 'Inno Setup 脚本不存在。'
    }
    $compilerArguments = @(
        (New-IsppStringDefine -Name 'SourceRoot' -Value $publishDirectory),
        (New-IsppStringDefine -Name 'AppVersion' -Value $Version),
        (New-IsppStringDefine -Name 'FileVersion' -Value $fileVersion),
        (New-IsppStringDefine -Name 'OutputDirectory' -Value $releaseDirectory),
        (New-IsppStringDefine -Name 'OutputBaseFilename' -Value $outputBaseFilename)
    )
    if ($OfficialUnsignedRelease) {
        $compilerArguments += @(
            (New-IsppStringDefine -Name 'Publisher' -Value $constants.distribution.signing.publisher),
            (New-IsppStringDefine -Name 'ReleaseUri' -Value $constants.distribution.officialReleaseUri)
        )
    }
    $compilerArguments += $innoScript
    Write-Host '[package] 编译未签名安装包。'
    Invoke-DshNative `
        -FilePath $InnoCompilerPath `
        -Arguments $compilerArguments `
        -WorkingDirectory $repositoryRoot

    $installerPath = Join-Path $releaseDirectory $installerName
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw 'Inno Setup 未生成预期安装包。'
    }
    Assert-Unsigned -Path $installerPath -Description '安装包'

    $verifySummaryDestination = Join-Path $releaseDirectory 'verify-summary.json'
    $sbomDestination = Join-Path $releaseDirectory 'sbom.spdx.json'
    $licenseDestination = Join-Path $releaseDirectory 'third-party-licenses.json'
    foreach ($evidence in @(
            @{ Source = $verifySummarySource; Destination = $verifySummaryDestination },
            @{ Source = Join-Path $verifyDirectory 'sbom.spdx.json'; Destination = $sbomDestination },
            @{ Source = Join-Path $verifyDirectory 'third-party-licenses.json'; Destination = $licenseDestination })) {
        if (-not (Test-Path -LiteralPath $evidence.Source -PathType Leaf)) {
            throw '统一验证缺少安装包所需证据文件。'
        }
        Copy-Item -LiteralPath $evidence.Source -Destination $evidence.Destination
    }
    Assert-DshSbomPairingIdentity `
        -Path $sbomDestination `
        -PairingIdentity $pairingIdentity

    $installerHash = Get-DshSha256 -Path $installerPath
    $manifest = [ordered]@{
        schemaVersion = 2
        packageKind = if ($OfficialUnsignedRelease) { 'official' } else { 'internal-test' }
        releaseEligible = [bool] $OfficialUnsignedRelease
        signed = $false
        warning = if ($OfficialUnsignedRelease) {
            'UNSIGNED OPEN SOURCE RELEASE'
        }
        else {
            'UNSIGNED INTERNAL TEST - NOT FOR PRODUCTION USE'
        }
        version = $Version
        source = [ordered]@{
            commit = $sourceState.Commit
            worktreeState = $sourceState.WorktreeState
        }
        pairingIdentity = $pairingIdentity
        verificationImpact = $verifySummary.verificationImpact
        identity = [ordered]@{
            appId = $appId
            productName = $productName
            executableName = $executableName
            applicationDataId = $applicationDataId
            singleInstanceBaseName = $singleInstanceBaseName
            installDirectoryName = $installDirectoryName
            defaultInstallDirectory = if ($OfficialUnsignedRelease) {
                '%LOCALAPPDATA%\Programs\DshWindowsLauncher'
            }
            else {
                '%LOCALAPPDATA%\Programs\DshWindowsLauncher.InternalTest'
            }
        }
        installer = [ordered]@{
            fileName = $installerName
            sha256 = $installerHash
            authenticodeStatus = 'NotSigned'
        }
        application = [ordered]@{
            fileName = $executableName
            sha256 = Get-DshSha256 -Path $applicationExe
            productName = $applicationVersion.ProductName
            productVersion = $applicationVersion.ProductVersion
            fileVersion = $applicationVersion.FileVersion
            authenticodeStatus = 'NotSigned'
        }
        inputs = [ordered]@{
            dotnetSdkVersion = $constants.build.dotnetSdkVersion
            innoCompilerVersion = $innoCompilerVersion
            innoCompilerSha256 = Get-DshSha256 -Path $InnoCompilerPath
            innoCompilerSignatureStatus = $innoCompilerSignature.Status
            innoCompilerSignerSubject = $innoCompilerSignature.SignerSubject
            innoCompilerTimestampSubject = $innoCompilerSignature.TimestampSubject
            innoIdentityVersion = $innoIdentityVersion
            innoIdentitySha256 = Get-DshSha256 -Path $innoIdentityPath
            innoIdentitySignatureStatus = $innoIdentitySignature.Status
            innoIdentitySignerSubject = $innoIdentitySignature.SignerSubject
            webView2BootstrapperVersion = $webViewVersion
            webView2BootstrapperSha256 = Get-DshSha256 -Path $stagedBootstrapperPath
            webView2BootstrapperSignatureStatus = $stagedWebViewSignature.Status
            webView2BootstrapperSignerSubject = $stagedWebViewSignature.SignerSubject
            webView2BootstrapperTimestampSubject = $stagedWebViewSignature.TimestampSubject
        }
        evidence = [ordered]@{
            verifySummary = 'verify-summary.json'
            sbom = 'sbom.spdx.json'
            thirdPartyLicenses = 'third-party-licenses.json'
        }
    }
    $manifestName = if ($OfficialUnsignedRelease) { 'package-manifest.json' } else { 'internal-test-manifest.json' }
    $manifestPath = Join-Path $releaseDirectory $manifestName
    $manifest | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    $checksumEntries = @(
        $installerPath,
        $manifestPath
    ) | Sort-Object { [IO.Path]::GetFileName($_) }
    $checksumLines = @(
        $checksumEntries | ForEach-Object {
            "$(Get-DshSha256 -Path $_) *$([IO.Path]::GetFileName($_))"
        }
    )
    $checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
    $checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii

    if ((Get-DshSha256 -Path $installerPath) -cne $installerHash) {
        throw '安装包在冻结哈希后发生变化。'
    }

    $finalSourceState = Get-DshSourceState -RepositoryRoot $repositoryRoot
    if (-not $finalSourceState.Available -or
        $finalSourceState.Commit -cne $sourceState.Commit -or
        $finalSourceState.WorktreeState -cne 'clean') {
        throw '打包期间源提交或工作树状态发生变化。'
    }

    $commandName = if ($OfficialUnsignedRelease) { 'package-unsigned.ps1' } else { 'package-internal.ps1' }
    Write-Host "$commandName`: PASS`nInstaller: $installerPath`nSHA-256: $installerHash"
}
catch {
    Write-Error "package-internal.ps1: FAIL`n$($_.Exception.Message)"
    exit 1
}
