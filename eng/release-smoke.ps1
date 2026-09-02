[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $InstallerPath,

    [Parameter(Mandatory)]
    [string] $EvidenceDirectory,

    [string] $ExpectedSha256,

    [string] $VerifySummaryPath,

    [string] $CandidateExePath,

    [string] $InstalledExePath,

    [string[]] $Rs13UdfPaths = @(),

    [ValidateSet('FirstRelease', 'RegularPatch')]
    [string] $ReleaseKind = 'FirstRelease',

    [string[]] $TriggerTags = @(),

    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')
$startedAtUtc = [DateTime]::UtcNow

function Read-SafeEvidenceValue {
    param(
        [Parameter(Mandatory)]
        [string] $Prompt,
        [string] $Default = '',
        [switch] $Required
    )

    while ($true) {
        $value = Read-Host $Prompt
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = $Default
        }
        if ($Required -and [string]::IsNullOrWhiteSpace($value)) {
            Write-Warning '此证据项不能为空。请填写不含敏感内容的环境标识或结果摘要。'
            continue
        }
        if (Test-DshEvidenceTextSafe -Text $value) {
            return $value.Trim()
        }

        Write-Warning '输入包含查询字符串、凭据或其他禁止进入持久证据的敏感形式，请改用脱敏摘要。'
    }
}

function Escape-MarkdownCell {
    param([AllowNull()][string] $Value)
    if ($null -eq $Value) {
        return ''
    }
    return $Value.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}

function ConvertTo-DshStructuredSignedArtifact {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $ManifestArtifact
    )

    return [ordered]@{
        fileName = [IO.Path]::GetFileName([string] $ManifestArtifact.path)
        sha256 = ([string] $ManifestArtifact.sha256).ToLowerInvariant()
        signatureStatus = [string] $ManifestArtifact.signature.Status
        signerSubjectSha256 = Get-DshTextSha256 -Text ([string] $ManifestArtifact.signature.SignerSubject)
        timestampStatus = if ([string]::IsNullOrWhiteSpace(
                [string] $ManifestArtifact.signature.TimestampSubject)) {
            'missing'
        }
        else {
            'trusted'
        }
    }
}

function Get-InstalledWebView2RuntimeVersion {
    $clientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $paths = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$clientId",
        "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$clientId"
    )
    $versions = @(
        foreach ($path in $paths) {
            if (Test-Path -LiteralPath $path) {
                (Get-ItemProperty -LiteralPath $path -Name pv -ErrorAction SilentlyContinue).pv
            }
        }
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if ($versions.Count -eq 0) {
        return 'NOT_DETECTED'
    }
    return ($versions -join ', ')
}

function Get-CurrentHostEvidence {
    if (-not $IsWindows) {
        throw '正式发布实机冒烟必须在 Windows 主机运行。'
    }

    $currentVersion = Get-ItemProperty `
        -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' `
        -ErrorAction Stop
    $buildNumber = if (-not [string]::IsNullOrWhiteSpace([string] $currentVersion.CurrentBuildNumber)) {
        [string] $currentVersion.CurrentBuildNumber
    }
    else {
        [string] [Environment]::OSVersion.Version.Build
    }

    return [pscustomobject][ordered]@{
        Platform = [Environment]::OSVersion.Platform.ToString()
        Version = [Environment]::OSVersion.Version.ToString()
        BuildNumber = $buildNumber
        UpdateBuildRevision = [int] $currentVersion.UBR
        DisplayVersion = [string] $currentVersion.DisplayVersion
        ProductName = [string] $currentVersion.ProductName
        EditionId = [string] $currentVersion.EditionID
        Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        LogicalProcessorCount = [Environment]::ProcessorCount
    }
}

function Get-ExecutableIdentityEvidence {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $ExpectedSignerSubject
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "缺少必须实测的 EXE：$Path"
    }
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $item = Get-Item -LiteralPath $resolvedPath
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedPath)
    $signature = Get-DshAuthenticodeEvidence `
        -Path $resolvedPath `
        -ExpectedSubject $ExpectedSignerSubject `
        -RequireTimestamp

    return [pscustomobject][ordered]@{
        Path = $resolvedPath
        FileName = $item.Name
        Size = [int64] $item.Length
        FileVersion = [string] $version.FileVersion
        ProductVersion = [string] $version.ProductVersion
        Sha256 = Get-DshSha256 -Path $resolvedPath
        Signature = $signature
    }
}

function Get-LauncherProcessId {
    param([Parameter(Mandatory)][string] $ExecutablePath)

    $expectedPath = [IO.Path]::GetFullPath($ExecutablePath)
    $processName = [IO.Path]::GetFileNameWithoutExtension($expectedPath)
    $matches = @(
        Get-Process -Name $processName -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [string]::Equals(
                        [IO.Path]::GetFullPath($_.Path),
                        $expectedPath,
                        [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    $false
                }
            }
    )
    if ($matches.Count -ne 1) {
        throw "RS-13 要求已安装 EXE 恰有一个运行实例，实际为 $($matches.Count)：$expectedPath"
    }
    return [int] $matches[0].Id
}

function Get-ProcessTreeSample {
    param([Parameter(Mandatory)][int] $RootProcessId)

    $rows = @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId, Name)
    $treeIds = [System.Collections.Generic.HashSet[int]]::new()
    $null = $treeIds.Add($RootProcessId)
    do {
        $added = $false
        foreach ($row in $rows) {
            if ($treeIds.Contains([int] $row.ParentProcessId) -and
                $treeIds.Add([int] $row.ProcessId)) {
                $added = $true
            }
        }
    } while ($added)

    $rowById = @{}
    foreach ($row in $rows) {
        $rowById[[int] $row.ProcessId] = $row
    }
    $processes = @(
        foreach ($processId in @($treeIds | Sort-Object)) {
            $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($null -eq $process) {
                continue
            }
            try {
                $process.Refresh()
                $row = $rowById[$processId]
                [pscustomobject][ordered]@{
                    ProcessId = $processId
                    ParentProcessId = if ($null -eq $row) { 0 } else { [int] $row.ParentProcessId }
                    Name = if ($null -eq $row) { $process.ProcessName } else { [string] $row.Name }
                    PrivateBytes = [int64] $process.PrivateMemorySize64
                    TotalProcessorSeconds = [double] $process.TotalProcessorTime.TotalSeconds
                }
            }
            finally {
                $process.Dispose()
            }
        }
    )
    if (@($processes | Where-Object ProcessId -eq $RootProcessId).Count -ne 1) {
        throw 'RS-13 采样期间启动器进程已退出。'
    }

    return [pscustomobject][ordered]@{
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        RootProcessId = $RootProcessId
        Processes = $processes
    }
}

function Resolve-Rs13UdfPaths {
    param(
        [Parameter(Mandatory)]
        [string] $ApplicationDataId,

        [string[]] $RequestedPaths = @()
    )

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw '无法确定当前用户 LOCALAPPDATA，不能验证 RS-13 UDF 锁。'
    }
    $targetsRoot = [IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA "$ApplicationDataId\targets"))
    if (-not (Test-Path -LiteralPath $targetsRoot -PathType Container)) {
        throw "RS-13 目标数据根不存在：$targetsRoot"
    }

    $targetRootItem = Get-Item -LiteralPath $targetsRoot -Force
    if (($targetRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'RS-13 拒绝从重解析的 targets 目录采集 UDF 锁证据。'
    }

    $paths = if ($RequestedPaths.Count -gt 0) {
        @($RequestedPaths)
    }
    else {
        @(
            Get-ChildItem -LiteralPath $targetsRoot -Directory -Force |
                ForEach-Object { Join-Path $_.FullName 'udf' } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Container }
        )
    }
    if ($paths.Count -ne 4) {
        throw "RS-13 必须明确对应四个 UDF，实际为 $($paths.Count)。可用 -Rs13UdfPaths 指定。"
    }

    $resolved = @(
        foreach ($path in $paths) {
            if (-not (Test-Path -LiteralPath $path -PathType Container)) {
                throw "RS-13 UDF 不存在：$path"
            }
            $fullPath = (Resolve-Path -LiteralPath $path).Path
            $relative = [IO.Path]::GetRelativePath($targetsRoot, $fullPath)
            $parts = $relative -split '[\\/]'
            $targetId = [Guid]::Empty
            if ($parts.Count -ne 2 -or
                $parts[1] -cne 'udf' -or
                -not [Guid]::TryParseExact($parts[0], 'N', [ref] $targetId)) {
                throw "RS-13 UDF 不属于精确目标目录：$fullPath"
            }

            foreach ($component in @(
                (Join-Path $targetsRoot $parts[0]),
                $fullPath
            )) {
                $item = Get-Item -LiteralPath $component -Force
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "RS-13 拒绝重解析的 UDF 路径：$component"
                }
            }
            $fullPath
        }
    )
    if (@($resolved | Sort-Object -Unique).Count -ne 4) {
        throw 'RS-13 UDF 路径必须互不相同。'
    }
    return $resolved
}

function Test-UdfFilesUnlocked {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "RS-13 UDF 包含重解析路径：$($item.FullName)"
        }
        if ($item.PSIsContainer) {
            continue
        }

        try {
            $stream = [IO.File]::Open(
                $item.FullName,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::None)
            $stream.Dispose()
        }
        catch [IO.IOException] {
            return $false
        }
        catch [UnauthorizedAccessException] {
            return $false
        }
    }
    return $true
}

function Invoke-Rs13Measurement {
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [string] $ApplicationDataId,

        [string[]] $RequestedUdfPaths = @()
    )

    $sampleIntervalSeconds = 5
    $durationSeconds = 30 * 60
    $idleWindowSeconds = 5 * 60
    $udfPaths = @(Resolve-Rs13UdfPaths `
        -ApplicationDataId $ApplicationDataId `
        -RequestedPaths $RequestedUdfPaths)

    Write-Host 'RS-13 自动采样将持续 30 分钟。前 25 分钟混合使用四个目标窗口；最后 5 分钟保持稳定空闲。'
    $null = Read-Host '确认四个不同目标窗口均已打开且可操作后，按 Enter 开始自动采样'
    $rootProcessId = Get-LauncherProcessId -ExecutablePath $ExecutablePath
    $measurementStartedAt = [DateTimeOffset]::UtcNow
    $deadline = $measurementStartedAt.AddSeconds($durationSeconds)
    $idleStartedAt = $deadline.AddSeconds(-$idleWindowSeconds)
    $idleNoticeShown = $false
    $nextSampleAt = $measurementStartedAt
    $samples = [System.Collections.Generic.List[object]]::new()

    while ($true) {
        $now = [DateTimeOffset]::UtcNow
        if ($now -lt $nextSampleAt) {
            $delay = [int] [Math]::Ceiling(($nextSampleAt - $now).TotalMilliseconds)
            Start-Sleep -Milliseconds $delay
        }

        $sample = Get-ProcessTreeSample -RootProcessId $rootProcessId
        $samples.Add($sample)
        $capturedAt = [DateTimeOffset]::Parse($sample.CapturedAtUtc)
        if (-not $idleNoticeShown -and $capturedAt -ge $idleStartedAt) {
            Write-Host 'RS-13 已进入最后五分钟 CPU 稳定空闲采样。请停止交互，但保持四个窗口打开。'
            $idleNoticeShown = $true
        }
        if ($capturedAt -ge $deadline) {
            break
        }

        do {
            $nextSampleAt = $nextSampleAt.AddSeconds($sampleIntervalSeconds)
        } while ($nextSampleAt -le $capturedAt)
    }

    $measurementCompletedAt = [DateTimeOffset]::Parse($samples[$samples.Count - 1].CapturedAtUtc)
    $null = Read-Host '采样完成。请正常关闭四个目标窗口和启动器；发起关闭后按 Enter 开始 60 秒 UDF 锁释放判定'
    $closeRequestedAt = [DateTimeOffset]::UtcNow
    $lockResults = @(
        foreach ($path in $udfPaths) {
            [pscustomobject][ordered]@{
                Path = $path
                Released = $false
                ReleaseSeconds = 60.001
                ReleasedAtUtc = $null
            }
        }
    )

    while (([DateTimeOffset]::UtcNow - $closeRequestedAt).TotalSeconds -le 60) {
        $rootExited = $null -eq (Get-Process -Id $rootProcessId -ErrorAction SilentlyContinue)
        if ($rootExited) {
            foreach ($lockResult in $lockResults | Where-Object { -not $_.Released }) {
                if (Test-UdfFilesUnlocked -Path $lockResult.Path) {
                    $releasedAt = [DateTimeOffset]::UtcNow
                    $lockResult.Released = $true
                    $lockResult.ReleaseSeconds = [Math]::Round(
                        ($releasedAt - $closeRequestedAt).TotalSeconds,
                        3)
                    $lockResult.ReleasedAtUtc = $releasedAt.ToString('O')
                }
            }
        }
        if (@($lockResults | Where-Object { -not $_.Released }).Count -eq 0) {
            break
        }
        Start-Sleep -Seconds 1
    }

    return [pscustomobject][ordered]@{
        SchemaVersion = 1
        StartedAtUtc = $measurementStartedAt.ToString('O')
        CompletedAtUtc = $measurementCompletedAt.ToString('O')
        SampleIntervalSeconds = $sampleIntervalSeconds
        LogicalProcessorCount = [Environment]::ProcessorCount
        Samples = $samples.ToArray()
        CloseRequestedAtUtc = $closeRequestedAt.ToString('O')
        UdfLocks = $lockResults
    }
}

try {
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        throw "安装包不存在：$InstallerPath"
    }
    $InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path

    $constants = Get-DshReleaseConstants -RepositoryRoot $repositoryRoot
    Assert-DshReleaseConstants -RepositoryRoot $repositoryRoot -Constants $constants -RequireCandidate

    $expectedInstallerName = $constants.distribution.installerFileNameTemplate.
        Replace('{Product}', $constants.product.name, [StringComparison]::Ordinal).
        Replace('{SemVer}', $constants.product.version, [StringComparison]::Ordinal)
    if ([IO.Path]::GetFileName($InstallerPath) -ne $expectedInstallerName) {
        throw "安装包文件名不匹配。实际：$([IO.Path]::GetFileName($InstallerPath))；预期：$expectedInstallerName"
    }

    $actualHash = Get-DshSha256 -Path $InstallerPath
    if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        $sidecarPath = "$InstallerPath.sha256"
        if (-not (Test-Path -LiteralPath $sidecarPath -PathType Leaf)) {
            throw '必须通过 -ExpectedSha256 或安装包同名 .sha256 文件提供冻结哈希。'
        }
        $sidecarText = Get-Content -LiteralPath $sidecarPath -Raw -Encoding ASCII
        $hashMatch = [regex]::Match($sidecarText, '(?i)\b[0-9a-f]{64}\b')
        if (-not $hashMatch.Success) {
            throw "无法解析冻结哈希：$sidecarPath"
        }
        $ExpectedSha256 = $hashMatch.Value
    }
    if ($ExpectedSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        $actualHash -ne $ExpectedSha256.ToUpperInvariant()) {
        throw "安装包 SHA-256 与冻结值不一致。实际：$actualHash；冻结值：$ExpectedSha256"
    }

    $signature = Get-DshAuthenticodeEvidence `
        -Path $InstallerPath `
        -ExpectedSubject $constants.distribution.signing.certificateSubject `
        -RequireTimestamp

    $expectedInstallerVersion = ConvertTo-DshFileVersion -Version $constants.product.version
    $installerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath).FileVersion
    if ([string]::IsNullOrWhiteSpace($installerVersion) -or
        -not $installerVersion.StartsWith($expectedInstallerVersion, [StringComparison]::OrdinalIgnoreCase)) {
        throw "安装包文件版本不匹配。实际：$installerVersion；预期：$expectedInstallerVersion"
    }

    $releaseDirectory = Split-Path -Parent $InstallerPath
    $packageManifestPath = Join-Path $releaseDirectory 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $packageManifestPath -PathType Leaf)) {
        throw "缺少 package-manifest.json：$packageManifestPath"
    }
    $packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
    if ($packageManifest.schemaVersion -ne 2 -or
        $packageManifest.installer.sha256 -ne $actualHash -or
        [int64] $packageManifest.installer.size -ne (Get-Item -LiteralPath $InstallerPath).Length -or
        $packageManifest.version -ne $constants.product.version) {
        throw '安装包与 package-manifest.json 的冻结身份不一致。'
    }
    if ($packageManifest.source.commit -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
        $packageManifest.source.worktreeState -cne 'clean') {
        throw 'package-manifest.json 未绑定有效且干净的候选源码提交。'
    }
    if ($packageManifest.inputs.webView2BootstrapperVersion -ne
            '1.3.265.7' -or
        $packageManifest.inputs.webView2BootstrapperSha256 -ne
            '17debf797a6c737959bc588236e897936ffac1af5f7e515e674ab32f9edfe719'.ToUpperInvariant()) {
        throw 'package-manifest.json 中的 WebView2 Bootstrapper 身份与发布常量不一致。'
    }

    $pairingIdentity = $packageManifest.pairingIdentity
    if ($null -eq $pairingIdentity -or
        [string] $pairingIdentity.plugin -cne $constants.pairingBaseline.plugin -or
        [string] $pairingIdentity.referenceVersion -cne [string] $constants.pairingBaseline.referenceVersion -or
        [string] $pairingIdentity.cookieName -cne $constants.pairingBaseline.cookieName) {
        throw 'package-manifest.json 未绑定与发布常量一致的配对基线身份。'
    }
    if ($null -eq $packageManifest.verificationImpact -or
        @($packageManifest.verificationImpact.requiredRs).Count -eq 0) {
        throw 'package-manifest.json 缺少 fail-closed 验证影响证据。'
    }

    $signedApplicationSet = $packageManifest.signedApplicationSet
    foreach ($entry in @(
            @{ Name = 'apphost'; Value = $signedApplicationSet.apphost },
            @{ Name = 'managed entry assembly'; Value = $signedApplicationSet.managedEntryAssembly },
            @{ Name = 'uninstaller'; Value = $signedApplicationSet.uninstaller },
            @{ Name = 'installer'; Value = $signedApplicationSet.installer })) {
        if ($null -eq $entry.Value -or [string] $entry.Value.sha256 -cnotmatch '^[A-F0-9]{64}$') {
            throw "签名应用集缺少冻结身份：$($entry.Name)"
        }
        Assert-DshRecordedAuthenticodeEvidence `
            -Evidence $entry.Value.signature `
            -ExpectedSubject $constants.distribution.signing.certificateSubject `
            -Description $entry.Name
    }
    if ($signedApplicationSet.installer.sha256 -cne $actualHash -or
        $signedApplicationSet.installer.signature.SignerThumbprint -cne $signature.SignerThumbprint -or
        $signedApplicationSet.apphost.sha256 -cne $packageManifest.application.sha256 -or
        $signedApplicationSet.managedEntryAssembly.sha256 -cne $packageManifest.managedEntryAssembly.sha256 -or
        $signedApplicationSet.uninstaller.signature.SignerThumbprint -cne
            $packageManifest.uninstaller.signature.SignerThumbprint) {
        throw '签名应用集与 package-manifest.json 的候选身份不一致。'
    }

    $managedEntryAssemblyPath = [string] $signedApplicationSet.managedEntryAssembly.path
    if (-not (Test-Path -LiteralPath $managedEntryAssemblyPath -PathType Leaf) -or
        (Get-DshSha256 -Path $managedEntryAssemblyPath) -cne $signedApplicationSet.managedEntryAssembly.sha256) {
        throw '托管主程序集缺失或 SHA-256 与签名应用集不一致。'
    }
    $measuredManagedEntryAssemblySignature = Get-DshAuthenticodeEvidence `
        -Path $managedEntryAssemblyPath `
        -ExpectedSubject $constants.distribution.signing.certificateSubject `
        -RequireTimestamp
    if ($measuredManagedEntryAssemblySignature.SignerThumbprint -cne
        $signedApplicationSet.managedEntryAssembly.signature.SignerThumbprint) {
        throw '托管主程序集实测签名与 package-manifest.json 不一致。'
    }

    if ($packageManifest.application.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'package-manifest.json 缺少候选 EXE 的冻结 SHA-256。'
    }
    if ([string]::IsNullOrWhiteSpace($CandidateExePath)) {
        $CandidateExePath = [string] $packageManifest.application.path
    }
    if ([string]::IsNullOrWhiteSpace($InstalledExePath)) {
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            throw '无法确定默认已安装 EXE 路径；请显式传入 -InstalledExePath。'
        }
        $InstalledExePath = Join-Path `
            $env:LOCALAPPDATA `
            "Programs\$($constants.product.applicationDataId)\$($constants.product.executableName)"
    }

    $hostEvidence = [pscustomobject][ordered]@{
        SchemaVersion = 1
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Os = Get-CurrentHostEvidence
        CandidateExecutable = Get-ExecutableIdentityEvidence `
            -Path $CandidateExePath `
            -ExpectedSignerSubject $constants.distribution.signing.certificateSubject
        InstalledExecutable = $null
    }
    if ($hostEvidence.CandidateExecutable.FileName -cne $constants.product.executableName -or
        -not $hostEvidence.CandidateExecutable.FileVersion.StartsWith(
            $expectedInstallerVersion,
            [StringComparison]::OrdinalIgnoreCase) -or
        $hostEvidence.CandidateExecutable.Sha256 -cne
            $packageManifest.application.sha256.ToUpperInvariant()) {
        throw '候选 EXE 的名称、版本或 SHA-256 与冻结清单不一致。'
    }

    $sbomPath = Join-Path $releaseDirectory 'sbom.spdx.json'
    $licenseInventoryPath = Join-Path $releaseDirectory 'third-party-licenses.json'
    $releaseNotesPath = Join-Path $releaseDirectory 'release-notes-input.md'
    foreach ($requiredArtifact in @($sbomPath, $licenseInventoryPath, $releaseNotesPath)) {
        if (-not (Test-Path -LiteralPath $requiredArtifact -PathType Leaf)) {
            throw "缺少候选供应链或发行说明产物：$requiredArtifact"
        }
    }
    if ($packageManifest.supplyChain.sbomSha256 -ne (Get-DshSha256 -Path $sbomPath) -or
        $packageManifest.supplyChain.licenseInventorySha256 -ne (Get-DshSha256 -Path $licenseInventoryPath) -or
        $packageManifest.supplyChain.releaseNotesInputSha256 -ne (Get-DshSha256 -Path $releaseNotesPath)) {
        throw 'SBOM、许可证清单或发行说明输入与 package-manifest.json 的冻结哈希不一致。'
    }
    Assert-DshSbomPairingIdentity `
        -Path $sbomPath `
        -PairingIdentity $pairingIdentity
    $releaseNotes = Get-Content -LiteralPath $releaseNotesPath -Raw -Encoding UTF8
    foreach ($fragment in @(
            "Pairing plugin: $($pairingIdentity.plugin) ($($pairingIdentity.referenceVersion))",
            "Pairing cookie: $($pairingIdentity.cookieName)")) {
        if (-not $releaseNotes.Contains($fragment, [StringComparison]::Ordinal)) {
            throw "发行说明输入未绑定配对基线身份：$fragment"
        }
    }

    if ([string]::IsNullOrWhiteSpace($VerifySummaryPath)) {
        $VerifySummaryPath = Join-Path (Split-Path -Parent $InstallerPath) 'verify-summary.json'
    }
    if (-not (Test-Path -LiteralPath $VerifySummaryPath -PathType Leaf)) {
        throw "缺少 verify-summary.json：$VerifySummaryPath"
    }
    $VerifySummaryPath = (Resolve-Path -LiteralPath $VerifySummaryPath).Path
    $verifySummary = Get-Content -LiteralPath $VerifySummaryPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
    if ($verifySummary.result -ne 'PASS') {
        throw 'verify.ps1 结果不是 PASS，不得进入实机发布冒烟。'
    }
    if (-not $verifySummary.source.available -or
        $verifySummary.source.commit -cne $packageManifest.source.commit -or
        $verifySummary.source.worktreeState -cne 'clean') {
        throw 'verify-summary.json 与 package-manifest.json 未绑定同一干净候选提交。'
    }
    if ($packageManifest.verifySummary.sha256 -ne (Get-DshSha256 -Path $VerifySummaryPath)) {
        throw 'verify-summary.json 与 package-manifest.json 记录的哈希不一致。'
    }
    if ((@($verifySummary.verificationImpact.requiredRs) -join '|') -cne
        (@($packageManifest.verificationImpact.requiredRs) -join '|')) {
        throw 'verify-summary.json 与 package-manifest.json 的验证影响集合不一致。'
    }

    $EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
    $null = New-Item -ItemType Directory -Path $EvidenceDirectory -Force
    $evidencePath = Join-Path $EvidenceDirectory 'release-evidence.md'
    if (Test-Path -LiteralPath $evidencePath) {
        throw "拒绝覆盖既有发布证据：$evidencePath"
    }

    $matrix = @(Get-DshSmokeMatrix)
    $requiredIds = @(@(
            @(Get-DshRequiredSmokeIds -ReleaseKind $ReleaseKind -TriggerTags $TriggerTags)
            @($packageManifest.verificationImpact.requiredRs)
        ) | Sort-Object { [int] ($_ -replace '^RS-', '') } -Unique)
    $selected = @($matrix | Where-Object { $requiredIds -contains $_.Id })
    if ($selected.Count -eq 0) {
        throw '没有选中任何实机冒烟项目。'
    }

    $environmentEvidence = [ordered]@{
        Windows11Vm = 'PENDING'
        Windows11Physical = 'PENDING'
        LinuxABaseline = 'PENDING'
    }
    if (-not $NonInteractive) {
        Write-Host '只填写脱敏环境标识。不得粘贴 token、Cookie、配对 URL、页面内容或剪贴板内容。'
        $environmentEvidence.Windows11Vm = Read-SafeEvidenceValue -Prompt 'Windows 11 VM 快照标识' -Required
        $environmentEvidence.Windows11Physical = Read-SafeEvidenceValue -Prompt 'Windows 11 实体参考机标识' -Required
        $environmentEvidence.LinuxABaseline = Read-SafeEvidenceValue -Prompt 'Linux A/B 基线摘要' -Required
    }

    $rs13Measurement = $null
    $rs13Verdict = $null
    $rs13RawPath = $null
    $hostEvidencePassed = $false
    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $selected) {
        Write-Host ''
        Write-Host "[$($item.Id)] $($item.Frequency)"
        Write-Host "环境：$($item.Environment)"
        Write-Host $item.Instructions
        Write-Host "triggerTags: $($item.TriggerTags -join ', ')"

        $automatedEvidence = 'PASS'
        $automatedChecks = [System.Collections.Generic.List[string]]::new()
        $automatedChecks.Add('HOST-OS')
        if ($item.Id -in @('RS-01', 'RS-02', 'RS-13')) {
            $automatedChecks.Add('CANDIDATE-EXE')
            $automatedChecks.Add('INSTALLED-EXE')
        }
        if ($item.Id -eq 'RS-02' -and -not $NonInteractive) {
            $null = Read-Host '完成从默认目录全新安装并启动真实 EXE 后，按 Enter 自动采集已安装文件身份'
            $hostEvidence.InstalledExecutable = Get-ExecutableIdentityEvidence `
                -Path $InstalledExePath `
                -ExpectedSignerSubject $constants.distribution.signing.certificateSubject
            $hostEvidence.CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            $hostEvidencePassed = Test-DshReleaseHostEvidence `
                -Evidence $hostEvidence `
                -ExpectedVersion $expectedInstallerVersion `
                -ExpectedExecutableName $constants.product.executableName `
                -ExpectedSignerSubject $constants.distribution.signing.certificateSubject `
                -ExpectedSha256 $packageManifest.application.sha256
            if (-not $hostEvidencePassed) {
                throw '候选与已安装 EXE 的路径、版本、大小、哈希或签名结构化证据不完整或不一致。'
            }
        }
        if ($item.Id -eq 'RS-13') {
            $automatedChecks.Add('PROCESS-TREE')
            $automatedChecks.Add('PRIVATE-BYTES')
            $automatedChecks.Add('IDLE-CPU')
            $automatedChecks.Add('UDF-LOCKS')
            if ($NonInteractive -or -not $hostEvidencePassed) {
                $automatedEvidence = 'MISSING'
            }
            else {
                $rs13Measurement = Invoke-Rs13Measurement `
                    -ExecutablePath $hostEvidence.InstalledExecutable.Path `
                    -ApplicationDataId $constants.product.applicationDataId `
                    -RequestedUdfPaths $Rs13UdfPaths
                $rs13Verdict = Get-DshRs13Verdict -Measurement $rs13Measurement
                $automatedEvidence = if ($rs13Verdict.Passed) { 'PASS' } else { 'FAIL' }
                $rs13RawPath = Join-Path `
                    ([IO.Path]::GetTempPath()) `
                    ("DshWindowsLauncher-rs13-{0}-{1}.json" -f `
                        $actualHash.Substring(0, 12),
                        [Guid]::NewGuid().ToString('N'))
                $rs13Json = $rs13Measurement | ConvertTo-Json -Depth 100
                if (-not (Test-DshEvidenceTextSafe -Text $rs13Json)) {
                    throw 'RS-13 结构化采样命中敏感形式，拒绝写盘。'
                }
                $rs13Json | Set-Content -LiteralPath $rs13RawPath -Encoding utf8NoBOM
            }
        }

        if ($NonInteractive) {
            $attestation = 'PENDING'
            $note = '需要在指定实机环境人工执行。'
        }
        else {
            $allowed = if ($item.Success -eq 'RECORD') { @('RECORD', 'FAIL', 'SKIP') } else { @('PASS', 'FAIL', 'SKIP') }
            do {
                $attestation = (Read-Host "输入仅限视觉或人工操作条件的 attestation $($allowed -join '/')").Trim().ToUpperInvariant()
            } until ($allowed -contains $attestation)
            $note = Read-SafeEvidenceValue `
                -Prompt '必填脱敏摘要；不得包含请求参数、页面、图片、剪贴板或凭据' `
                -Required
        }

        $results.Add([pscustomobject]@{
            Id = $item.Id
            Result = 'PENDING'
            ExpectedSuccess = $item.Success
            AutomatedEvidence = $automatedEvidence
            AutomatedChecks = $automatedChecks.ToArray()
            Attestation = $attestation
            TriggerTags = $item.TriggerTags
            Environment = $item.Environment
            Note = $note
        })
    }

    foreach ($result in $results) {
        $automatedEvidence = 'PASS'
        if ($result.Id -in @('RS-01', 'RS-02', 'RS-13') -and
            -not $hostEvidencePassed) {
            $automatedEvidence = 'MISSING'
        }
        if ($result.Id -eq 'RS-13' -and
            ($null -eq $rs13Verdict -or -not $rs13Verdict.Passed)) {
            $automatedEvidence = if ($null -eq $rs13Verdict) { 'MISSING' } else { 'FAIL' }
        }
        $result.AutomatedEvidence = $automatedEvidence
        $result.Result = Resolve-DshSmokeResult `
            -AutomatedEvidence $automatedEvidence `
            -Attestation $result.Attestation
    }

    $sensitiveExclusion = 'NO'
    if (-not $NonInteractive) {
        do {
            $sensitiveExclusion = (Read-Host '是否已人工检查全部持久证据不含 token、Cookie、图片、页面、剪贴板及请求响应体？输入 YES/NO').Trim().ToUpperInvariant()
        } until ($sensitiveExclusion -in @('YES', 'NO'))
    }

    $smokePassed = $sensitiveExclusion -eq 'YES'
    foreach ($result in $results) {
        if ($result.Result -ne $result.ExpectedSuccess) {
            $smokePassed = $false
        }
    }
    $smokeResult = if ($smokePassed) { 'PASS' } else { 'FAIL' }

    $gitCommit = [string] $packageManifest.source.commit
    $worktreeState = [string] $packageManifest.source.worktreeState

    $runtimeVersion = Get-InstalledWebView2RuntimeVersion
    $installerItem = Get-Item -LiteralPath $InstallerPath
    $structuredEvidencePath = Join-Path $EvidenceDirectory 'release-structured-evidence.json'
    if (Test-Path -LiteralPath $structuredEvidencePath) {
        throw "拒绝覆盖既有结构化发布证据：$structuredEvidencePath"
    }
    $structuredEvidence = [ordered]@{
        schemaVersion = 2
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        candidateVersion = $constants.product.version
        commit = $gitCommit
        result = $smokeResult
        sensitiveDataExclusionAttestation = $sensitiveExclusion
        installer = [ordered]@{
            path = $InstallerPath
            size = [int64] $installerItem.Length
            sha256 = $actualHash
            fileVersion = $installerVersion
            signature = $signature
        }
        pairingIdentity = $pairingIdentity
        verificationImpact = $packageManifest.verificationImpact
        signedApplicationSet = [ordered]@{
            apphost = ConvertTo-DshStructuredSignedArtifact $signedApplicationSet.apphost
            managedEntryAssembly = ConvertTo-DshStructuredSignedArtifact $signedApplicationSet.managedEntryAssembly
            uninstaller = ConvertTo-DshStructuredSignedArtifact $signedApplicationSet.uninstaller
            installer = ConvertTo-DshStructuredSignedArtifact $signedApplicationSet.installer
        }
        host = $hostEvidence
        rs13 = if ($null -eq $rs13Verdict) {
            [ordered]@{
                selected = $requiredIds -contains 'RS-13'
                evidence = 'MISSING'
            }
        }
        else {
            [ordered]@{
                selected = $true
                rawEvidencePath = $rs13RawPath
                rawEvidenceSha256 = Get-DshSha256 -Path $rs13RawPath
                verdict = $rs13Verdict
            }
        }
        smokeResults = $results.ToArray()
    }
    $structuredJson = $structuredEvidence | ConvertTo-Json -Depth 100
    if (-not (Test-DshEvidenceTextSafe -Text $structuredJson)) {
        throw '结构化发布证据命中敏感形式，拒绝写盘。'
    }
    $structuredJson | Set-Content -LiteralPath $structuredEvidencePath -Encoding utf8NoBOM

    $automatedFiles = @(
        [pscustomobject]@{ Path = $VerifySummaryPath; Sha256 = Get-DshSha256 -Path $VerifySummaryPath },
        [pscustomobject]@{ Path = $packageManifestPath; Sha256 = Get-DshSha256 -Path $packageManifestPath },
        [pscustomobject]@{ Path = $sbomPath; Sha256 = Get-DshSha256 -Path $sbomPath },
        [pscustomobject]@{ Path = $licenseInventoryPath; Sha256 = Get-DshSha256 -Path $licenseInventoryPath },
        [pscustomobject]@{ Path = $releaseNotesPath; Sha256 = Get-DshSha256 -Path $releaseNotesPath },
        [pscustomobject]@{ Path = $structuredEvidencePath; Sha256 = Get-DshSha256 -Path $structuredEvidencePath }
        if ($null -ne $rs13RawPath) {
            [pscustomobject]@{ Path = $rs13RawPath; Sha256 = Get-DshSha256 -Path $rs13RawPath }
        }
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# Release evidence')
    $lines.Add('')
    $lines.Add("- Version: $($constants.product.version)")
    $lines.Add("- Commit: $gitCommit")
    $lines.Add("- Worktree state: $worktreeState")
    $lines.Add("- Installer file: $InstallerPath")
    $lines.Add("- Installer size: $($installerItem.Length)")
    $lines.Add("- Installer SHA-256: $actualHash")
    $lines.Add("- Pairing plugin: $($pairingIdentity.plugin); $($pairingIdentity.referenceVersion)")
    $lines.Add("- Pairing cookie: $($pairingIdentity.cookieName)")
    $lines.Add("- Required RS: $($requiredIds -join ', ')")
    $lines.Add("- Installer signature subject and timestamp: $($signature.SignerSubject); $($signature.TimestampSubject); timestamp certificate expires $($signature.TimestampNotAfter)")
    $lines.Add("- Current OS/build: $($hostEvidence.Os.ProductName) $($hostEvidence.Os.DisplayVersion); version $($hostEvidence.Os.Version); build $($hostEvidence.Os.BuildNumber).$($hostEvidence.Os.UpdateBuildRevision); $($hostEvidence.Os.Architecture); $($hostEvidence.Os.LogicalProcessorCount) logical processors")
    $lines.Add("- Candidate EXE path, size, version and SHA-256: $($hostEvidence.CandidateExecutable.Path); $($hostEvidence.CandidateExecutable.Size); $($hostEvidence.CandidateExecutable.FileVersion); $($hostEvidence.CandidateExecutable.Sha256)")
    $lines.Add("- Candidate EXE signature subject and timestamp: $($hostEvidence.CandidateExecutable.Signature.SignerSubject); $($hostEvidence.CandidateExecutable.Signature.TimestampSubject); timestamp certificate expires $($hostEvidence.CandidateExecutable.Signature.TimestampNotAfter)")
    if ($null -eq $hostEvidence.InstalledExecutable) {
        $lines.Add('- Installed EXE path, size, version and SHA-256: MISSING')
        $lines.Add('- Installed EXE signature subject and timestamp: MISSING')
    }
    else {
        $lines.Add("- Installed EXE path, size, version and SHA-256: $($hostEvidence.InstalledExecutable.Path); $($hostEvidence.InstalledExecutable.Size); $($hostEvidence.InstalledExecutable.FileVersion); $($hostEvidence.InstalledExecutable.Sha256)")
        $lines.Add("- Installed EXE signature subject and timestamp: $($hostEvidence.InstalledExecutable.Signature.SignerSubject); $($hostEvidence.InstalledExecutable.Signature.TimestampSubject); timestamp certificate expires $($hostEvidence.InstalledExecutable.Signature.TimestampNotAfter)")
    }
    $lines.Add("- Pairing baseline: $($constants.pairingBaseline.plugin) $($constants.pairingBaseline.referenceVersion)")
    $lines.Add("- Actual .NET and Inno versions: .NET SDK $($constants.build.dotnetSdkVersion); Inno $($constants.distribution.innoSetup.version)")
    $lines.Add("- Windows 11 VM snapshot: $($environmentEvidence.Windows11Vm)")
    $lines.Add("- Windows 11 physical reference: $($environmentEvidence.Windows11Physical)")
    $lines.Add("- Linux A/B baseline: $($environmentEvidence.LinuxABaseline)")
    $lines.Add("- verify.ps1 start/end/result: $($verifySummary.startedAtUtc) / $($verifySummary.endedAtUtc) / $($verifySummary.result)")
    $lines.Add('- Automated result files and SHA-256:')
    foreach ($file in $automatedFiles) {
        $lines.Add("  - $($file.Path): $($file.Sha256)")
    }
    $lines.Add("- Executed smoke IDs and PASS/FAIL/RECORD: $(($results | ForEach-Object { \"$($_.Id)=$($_.Result)\" }) -join ', ')")
    $rs13 = $results | Where-Object Id -eq 'RS-13' | Select-Object -First 1
    $rs13Summary = if ($null -eq $rs13) {
        'NOT_SELECTED'
    }
    elseif ($null -eq $rs13Verdict) {
        'FAIL; required structured measurement is missing'
    }
    else {
        "$($rs13Verdict.Passed); peak $($rs13Verdict.PeakPrivateBytes) bytes ($($rs13Verdict.PeakPrivateGiB) GiB); final-five-minute average CPU $($rs13Verdict.IdleAverageCpuPercent)%; all four UDF locks released within 60 seconds $($rs13Verdict.AllUdfLocksReleased); failures $($rs13Verdict.Failures -join ',')"
    }
    $lines.Add("- RS-13 Private Bytes, CPU and UDF lock release: $rs13Summary")
    $lines.Add("- release-smoke.ps1 start/end/result: $($startedAtUtc.ToString('O')) / $([DateTime]::UtcNow.ToString('O')) / $smokeResult")
    $failed = @($results | Where-Object { $_.Result -ne $_.ExpectedSuccess })
    $failureSummary = if ($failed.Count -eq 0) { 'none' } else { ($failed | ForEach-Object { "$($_.Id)=$($_.Result):$($_.Note)" }) -join '; ' }
    $lines.Add("- Failures, root cause, fix commit and rerun link: $failureSummary")
    $rs15 = $results | Where-Object Id -eq 'RS-15' | Select-Object -First 1
    $lines.Add("- Windows 10 informational result: $(if ($null -eq $rs15) { 'NOT_SELECTED' } else { "$($rs15.Result): $($rs15.Note)" })")
    $lines.Add('- Human release confirmation: NO')
    $lines.Add('- Confirmation time:')
    $lines.Add('- Confirmed installer SHA-256:')
    $lines.Add("- Sensitive-data exclusion checked: $sensitiveExclusion")
    $lines.Add('')
    $lines.Add('## Smoke results')
    $lines.Add('')
    $lines.Add('| ID | Automated evidence | Attestation | Result | Expected | triggerTags | Environment | Redacted note |')
    $lines.Add('|---|---|---|---|---|---|---|---|')
    foreach ($result in $results) {
        $lines.Add("| $($result.Id) | $($result.AutomatedEvidence): $(Escape-MarkdownCell ($result.AutomatedChecks -join ', ')) | $($result.Attestation) | $($result.Result) | $($result.ExpectedSuccess) | $(Escape-MarkdownCell ($result.TriggerTags -join ', ')) | $(Escape-MarkdownCell $result.Environment) | $(Escape-MarkdownCell $result.Note) |")
    }

    $report = $lines -join [Environment]::NewLine
    if (-not (Test-DshEvidenceTextSafe -Text $report)) {
        throw '生成的发布证据命中敏感形式，拒绝写盘。'
    }
    $report | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM

    if (-not $smokePassed) {
        Write-Error "release-smoke.ps1: FAIL；已生成未通过证据：$evidencePath"
        exit 1
    }

    Write-Host "release-smoke.ps1: PASS`nEvidence: $evidencePath`nHuman release confirmation remains NO."
}
catch {
    Write-Error "release-smoke.ps1: FAIL`n$($_.Exception.Message)"
    exit 1
}
