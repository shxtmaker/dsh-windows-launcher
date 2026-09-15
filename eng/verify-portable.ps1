[CmdletBinding()]
param(
    [ValidateSet('Core', 'Development')]
    [string] $Profile = 'Development',

    [string] $ArtifactsDirectory,

    [string] $DotNetPath
)

# Linux 开发门禁入口（方案 2.0 第 2.2、3、6 节）。
# 它只证明非 Windows 开发层级，不替代 eng/verify.ps1 的 Windows 正式门禁，
# 其摘要 portable-verify-summary.json 不得用作发布凭据。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'verification-tests.ps1')

$startedAtUtc = [DateTime]::UtcNow
$failures = [System.Collections.Generic.List[string]]::new()
$checks = [System.Collections.Generic.List[object]]::new()
$testResults = [System.Collections.Generic.List[object]]::new()
$resolvedArtifactsDirectory = $null
# 真实摘要一旦落盘就不得被 catch 的兜底摘要覆盖（否则失败轮次会丢掉用例计数与候选身份）。
$summaryWritten = $false
# D24 检查把聚合结果回传给摘要（Invoke-DshPortableStep 的 Body 在子作用域执行，
# 因此只能写回预先声明的引用类型）。
$d24State = [ordered]@{}

function Add-DshCheckResult {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Status,
        [string] $Detail = ''
    )
    $checks.Add([ordered]@{ id = $Id; status = $Status; detail = $Detail })
    $color = switch ($Status) { 'pass' { 'Green' } 'fail' { 'Red' } 'incomplete' { 'Yellow' } default { 'Gray' } }
    Write-Host ("  [{0,-10}] {1}{2}" -f $Status, $Id, $(if ($Detail) { " — $Detail" } else { '' })) -ForegroundColor $color
}

function Invoke-DshPortableStep {
    <#
    .SYNOPSIS
        运行一个检查步骤；命令失败即抛错，绝不被摘要吞掉。
    .DESCRIPTION
        返回步骤状态（pass/fail）。脚本块自身的输出被丢弃，避免污染状态值；
        需要回传数据的步骤请写入已声明的集合，不要依赖动态作用域赋值。
    #>
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][scriptblock] $Body
    )
    Write-Host "[$Id] $Description"
    try {
        $null = & $Body
        Add-DshCheckResult -Id $Id -Status 'pass'
        return 'pass'
    }
    catch {
        $message = $_.Exception.Message
        $failures.Add("[$Id] $message")
        Add-DshCheckResult -Id $Id -Status 'fail' -Detail $message
        return 'fail'
    }
}

# ===== D24：完整 Linux 开发验证的收敛、零容忍与独立性判定 =====
# 以下判定逻辑全部写成**纯函数**（输入→输出、无副作用），因此可以在门禁内用真实对象与
# 合成变异输入跑负向控制，证明"零用例/跳过/未验/虚报/互相掩盖"确实会让判据失败，而不是恒真。
# 这些函数不读全局状态；调用方负责传入本轮实测值。

function Resolve-DshPortableStatus {
    <#
    .SYNOPSIS
        只按 L01–L11、L13 的可移植层输入推导 portableStatus。
    .DESCRIPTION
        参数表里**没有** crossBuildStatus：交叉构建在结构上不可能抬高或压低 portableStatus。
        零用例、必需项 incomplete/notRun/fail、用例数不符、失败或跳过一律 fail。
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][string[]] $PortableFailureIds = @(),
        [int] $IncompleteCount = 0,
        [int] $RequiredCases = 0,
        [int] $ExecutedCases = 0,
        [int] $FailedCases = 0,
        [int] $SkippedCases = 0
    )

    $zeroTolerance = ($RequiredCases -le 0) -or ($ExecutedCases -le 0)
    if ($PortableFailureIds.Count -gt 0 -or $IncompleteCount -gt 0 -or $zeroTolerance -or
        $ExecutedCases -ne $RequiredCases -or $FailedCases -ne 0 -or $SkippedCases -ne 0) {
        return 'fail'
    }
    return 'pass'
}

function Resolve-DshCrossBuildStatus {
    <#
    .SYNOPSIS
        只按 cross-build-l12 这一个检查的状态推导 crossBuildStatus。
    .DESCRIPTION
        只接受 pass/fail；toolchainUnsupported 只能由带了最小复现证据的调用方显式覆盖，
        普通编译错误在这里永远是 fail。绝不读 portableStatus。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Checks,
        [string] $ToolchainUnsupportedEvidence = ''
    )

    $entry = @($Checks | Where-Object { [string] $_.id -eq 'cross-build-l12' })
    if ($entry.Count -eq 0) { return 'notRun' }
    $status = [string] $entry[0].status
    if ($status -eq 'pass') { return 'pass' }
    if ($status -eq 'fail' -and -not [string]::IsNullOrWhiteSpace($ToolchainUnsupportedEvidence) -and
        (Test-Path -LiteralPath $ToolchainUnsupportedEvidence -PathType Leaf)) {
        # 只有调用方提供了已确认的平台限制最小复现材料时才允许降级。
        return 'toolchainUnsupported'
    }
    if ($status -eq 'fail') { return 'fail' }
    return 'notRun'
}

function Resolve-DshHarnessIntegrationStatus {
    <#
    .SYNOPSIS
        由真实 Harness＋全家桶＋插件各检查的状态推导 harnessIntegration（不再是硬编码 notRun）。
    .DESCRIPTION
        L06/L07 的六项检查全部 pass 才是 pass；缺项或非 pass 一律 notRun，任一 fail 即 fail。
        纯函数，可在负向控制里用变异输入证伪。
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][object[]] $Checks = @()
    )

    $harnessCheckIds = @('host-fixture-bundle', 'addon-bridge-fixture', 'addon-compatibility-fixture', 'upload-hook-fixture', 'host-fixture-minimal-loop', 'provider-fixture')
    $entries = @($Checks | Where-Object { $harnessCheckIds -contains [string] $_.id })
    if ($entries.Count -lt $harnessCheckIds.Count) { return 'notRun' }
    if (@($entries | Where-Object { [string] $_.status -eq 'fail' }).Count -gt 0) { return 'fail' }
    if (@($entries | Where-Object { [string] $_.status -ne 'pass' }).Count -gt 0) { return 'notRun' }
    return 'pass'
}

function Get-DshD24SourceTreeHash {
    <#
    .SYNOPSIS
        按 d19/d20/d21 夹具同一算法对插件 src 树取指纹。
    .DESCRIPTION
        逐文件（相对 pluginRoot 的路径 + NUL + 字节 + NUL）做 SHA-256，路径按序数排序，
        以便与既有证据的 candidate.pluginSourceTree.sha256 逐字比较。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $PluginRoot,
        [Parameter(Mandatory)][string] $RelativeRoot
    )

    $root = Join-Path $PluginRoot $RelativeRoot
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return $null }
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
    $paths = @($files | ForEach-Object { $_.FullName })
    [Array]::Sort($paths, [System.StringComparer]::Ordinal)
    $sha = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($path in $paths) {
            $relative = $path.Substring($PluginRoot.Length + 1).Replace('\', '/')
            $sha.AppendData([Text.Encoding]::UTF8.GetBytes($relative))
            $sha.AppendData([byte[]]@(0))
            $sha.AppendData([IO.File]::ReadAllBytes($path))
            $sha.AppendData([byte[]]@(0))
        }
        return [pscustomobject]@{
            sha256 = [Convert]::ToHexString($sha.GetHashAndReset()).ToLowerInvariant()
            files  = $paths.Count
        }
    }
    finally {
        $sha.Dispose()
    }
}

function Get-DshD24LayerViews {
    <#
    .SYNOPSIS
        把 d24Convergence.layers 配置与本轮检查状态、证据文件合并成覆盖表视图。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $LayerConfig,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Checks,
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][DateTime] $RunStartedAtUtc
    )

    $views = [System.Collections.Generic.List[object]]::new()
    foreach ($layer in @($LayerConfig)) {
        $checkIds = @($layer.checks | ForEach-Object { [string] $_ })
        $checkStatuses = [System.Collections.Generic.List[object]]::new()
        foreach ($checkId in $checkIds) {
            $match = @($Checks | Where-Object { [string] $_.id -eq $checkId })
            $status = if ($match.Count -eq 0) { 'missing' } else { [string] $match[0].status }
            # 交叉构建由 crossBuildStatus 单独判定，不并入层内 other-status 归并。
            $checkStatuses.Add([ordered]@{ id = $checkId; status = $status })
        }
        $statuses = @($checkStatuses | ForEach-Object { $_.status })
        $status = if ($statuses -contains 'fail') { 'fail' }
        elseif ($statuses -contains 'missing') { 'incomplete' }
        elseif ($statuses -contains 'incomplete') { 'incomplete' }
        elseif ($statuses -contains 'notRun') { 'notRun' }
        else { 'pass' }

        $evidenceViews = [System.Collections.Generic.List[object]]::new()
        foreach ($relative in @($layer.evidence | ForEach-Object { [string] $_ })) {
            $full = Join-Path $RepositoryRoot $relative
            $exists = Test-Path -LiteralPath $full -PathType Leaf
            $fresh = $null
            if ($exists -and $relative.StartsWith('artifacts/')) {
                $mtime = (Get-Item -LiteralPath $full).LastWriteTimeUtc
                $fresh = ($mtime -ge $RunStartedAtUtc.AddSeconds(-120))
                $evidenceViews.Add([ordered]@{ path = $relative; exists = $true; modifiedAtUtc = $mtime.ToString('O'); sameRunFresh = $fresh })
            }
            else {
                $evidenceViews.Add([ordered]@{ path = $relative; exists = $exists; modifiedAtUtc = $null; sameRunFresh = $fresh })
            }
        }

        $caseViews = [System.Collections.Generic.List[object]]::new()
        foreach ($caseSpec in @($layer.caseEvidence)) {
            $relative = [string] $caseSpec.path
            $field = [string] $caseSpec.field
            $full = Join-Path $RepositoryRoot $relative
            $exists = Test-Path -LiteralPath $full -PathType Leaf
            $count = $null
            if ($exists) {
                try {
                    $document = Get-Content -LiteralPath $full -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 80
                    if ($document.PSObject.Properties.Name -contains $field) {
                        $count = @($document.$field).Count
                    }
                    else { $count = -1 }
                }
                catch { $count = -1 }
            }
            $caseViews.Add([ordered]@{ path = $relative; field = $field; exists = $exists; count = $count })
        }

        $views.Add([pscustomobject]@{
                layer        = [string] $layer.layer
                title        = [string] $layer.title
                checks       = @($checkStatuses)
                status       = $status
                evidence     = @($evidenceViews)
                caseEvidence = @($caseViews)
            })
    }
    return @($views)
}

function Get-DshD24ZeroToleranceViolations {
    <#
    .SYNOPSIS
        零容忍判定的纯函数：任何 0 用例、跳过、失败、未验（无正当理由）都必须产生违规。
    .DESCRIPTION
        SeparatelyJudgedLayers 里的层（L12）不在这里判状态，由 crossBuildStatus 单独判。
        notRun 默认拒绝：只有 justification=WindowsPending 或 justified-scope-limited 才放行。
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][object[]] $Layers = @(),
        [AllowEmptyCollection()][object[]] $TestResults = @(),
        [AllowEmptyCollection()][object[]] $NotRunInventory = @(),
        [int] $RequiredCases = 0,
        [int] $ExecutedCases = 0,
        [int] $FailedCases = 0,
        [int] $SkippedCases = 0,
        [string[]] $SeparatelyJudgedLayers = @('L12')
    )

    $violations = [System.Collections.Generic.List[string]]::new()
    if ($RequiredCases -le 0) { $violations.Add("零容忍：必需用例集合为 $RequiredCases（不得用零用例成功）") }
    if ($ExecutedCases -le 0) { $violations.Add("零容忍：实际执行用例为 $ExecutedCases") }
    if ($RequiredCases -ne $ExecutedCases) { $violations.Add("零容忍：执行用例数 $ExecutedCases ≠ 必需 $RequiredCases") }
    if ($FailedCases -ne 0) { $violations.Add("零容忍：失败用例 $FailedCases") }
    if ($SkippedCases -ne 0) { $violations.Add("零容忍：跳过/未运行用例计数 $SkippedCases（WindowsPending 只能走显式 notRun 清单，不得混入用例计数）") }

    foreach ($result in @($TestResults)) {
        $name = "$([string] $result.assembly)/$([string] $result.caseSet)"
        if ([int] $result.total -le 0) { $violations.Add("零容忍：$name 执行了 0 个用例") }
        if ([int] $result.failed -ne 0) { $violations.Add("零容忍：$name 有 $($result.failed) 个失败") }
        if ([int] $result.skipped -ne 0) { $violations.Add("零容忍：$name 有 $($result.skipped) 个跳过") }
        if ([int] $result.notRun -ne 0) { $violations.Add("零容忍：$name 有 $($result.notRun) 个未运行") }
    }

    foreach ($layer in @($Layers)) {
        if ($SeparatelyJudgedLayers -contains [string] $layer.layer) { continue }
        if ([string] $layer.status -ne 'pass') {
            $violations.Add("零容忍：必需层 $($layer.layer) 状态为 $($layer.status)（不得 incomplete/notRun/fail）")
        }
        foreach ($caseEvidence in @($layer.caseEvidence)) {
            if ($caseEvidence.exists -ne $true) {
                $violations.Add("零容忍：层 $($layer.layer) 的用例证据 $($caseEvidence.path) 不存在")
            }
            elseif ([int] $caseEvidence.count -le 0) {
                $violations.Add("零容忍：层 $($layer.layer) 的用例证据 $($caseEvidence.path) 字段 $($caseEvidence.field) 计数为 $($caseEvidence.count)")
            }
        }
    }

    foreach ($item in @($NotRunInventory)) {
        if ([string] $item.justification -eq 'unjustified') {
            $violations.Add("零容忍：notRun $($item.id) 无正当理由（来源 $($item.source)）：$($item.detail)")
        }
    }
    return @($violations)
}

function Get-DshD24NotRunInventory {
    <#
    .SYNOPSIS
        汇总所有显式 notRun，并给出 WindowsPending / justified-scope-limited / unjustified 分类。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object] $Config,
        [AllowEmptyCollection()][object[]] $EvidenceNotRunLists = @(),
        [AllowEmptyCollection()][string[]] $AcceptedScopeLimited = @()
    )

    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($project in @($Config.expectedProjects | Where-Object { $_.portable -ne $true })) {
        $reason = if ($project.PSObject.Properties.Name -contains 'notExecutableReason') { [string] $project.notExecutableReason } else { '非可移植 Windows 项目：Linux 只做交叉编译（L12），实机行为属 WindowsPending。' }
        $items.Add([pscustomobject]@{
                id            = [string] $project.name
                source        = 'eng/verification-profiles.json#expectedProjects'
                kind          = 'windows-test-assembly'
                justification = 'WindowsPending'
                detail        = $reason
            })
    }
    $items.Add([pscustomobject]@{
            id            = 'WindowsValidationAndDevelopmentReady'
            source        = 'portable-validation-plan.md §1/§6'
            kind          = 'windows-layer'
            justification = 'WindowsPending'
            detail        = 'Windows Clipboard/STA/WPF/WebView2 实机与正式 DevelopmentReady 属 Windows 的 eng/verify.ps1；Linux profile 的 producesDevelopmentReady=false。'
        })

    foreach ($list in @($EvidenceNotRunLists)) {
        foreach ($entry in @($list.items)) {
            $id = [string] $entry.id
            $platform = if ($entry.PSObject.Properties.Name -contains 'platform') { [string] $entry.platform } else { '' }
            $reason = if ($entry.PSObject.Properties.Name -contains 'reason') { [string] $entry.reason } else { '' }
            $justification = if ($reason -match '^\s*WindowsPending' -or $platform -eq 'windows') { 'WindowsPending' }
            elseif ($AcceptedScopeLimited -contains $id) { 'justified-scope-limited' }
            else { 'unjustified' }
            $items.Add([pscustomobject]@{
                    id            = $id
                    source        = [string] $list.source
                    kind          = 'in-check-notRun'
                    justification = $justification
                    detail        = $reason
                })
        }
    }
    return @($items)
}

function New-DshD24ProbeLayer {
    # 负向控制用的最小层对象。
    [CmdletBinding()]
    param([string] $Name, [string] $Status, [int] $CaseCount)
    return [pscustomobject]@{
        layer        = $Name
        status       = $Status
        caseEvidence = @([pscustomobject]@{ path = "probe/$Name"; field = 'gates'; exists = $true; count = $CaseCount })
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
        $ArtifactsDirectory = Join-Path $repositoryRoot 'artifacts/verify-portable'
    }
    $ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
    $null = New-Item -ItemType Directory -Path $ArtifactsDirectory -Force
    $resolvedArtifactsDirectory = $ArtifactsDirectory

    $config = Get-DshVerificationProfiles -RepositoryRoot $repositoryRoot
    $profileConfig = @($config.profiles | Where-Object { $_.name -eq $Profile })[0]
    $requiredChecks = @($profileConfig.requiredChecks)
    $expectedDataset = Get-DshExpectedTestDataset -RepositoryRoot $repositoryRoot

    Write-Host "verify-portable.ps1：profile=$Profile，platform=$([Runtime.InteropServices.RuntimeInformation]::OSDescription)"
    Write-Host "  artifacts=$ArtifactsDirectory"
    Write-Host ''

    # ---- 环境与工具 ----
    $dotnet = Get-DshExactDotNetPath -ExpectedVersion '10.0.400' -DotNetPath $DotNetPath
    # pnpm 只用于附加插件（Node 侧）；缺失即明确失败，不静默跳过插件检查。
    $pnpmCommand = Get-Command -Name pnpm -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $pnpmCommand) {
        throw '未找到 pnpm；附加插件检查需要 pnpm（精确版本已在 D00 基线登记）。'
    }
    $pnpm = $pnpmCommand.Source
    $nodeCommand = Get-Command -Name node -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $nodeCommand) {
        throw '未找到 node；真实宿主夹具与插件检查需要 node。'
    }
    $node = $nodeCommand.Source
    $portableSolution = Join-Path $repositoryRoot $config.portableSolution.path
    $fullSolution = Join-Path $repositoryRoot $config.fullSolution.path
    $coreTestProject = Join-Path $repositoryRoot 'tests/DshLauncher.Core.Tests/DshLauncher.Core.Tests.csproj'

    Write-Host '== 平台身份与结构 =='
    $null = Invoke-DshPortableStep -Id 'platform-evaluation' -Description '按项目核对最终 MSBuild 求值的 TFM/RID' -Body {
        foreach ($expected in @($config.expectedProjects)) {
            $projectPath = Join-Path $repositoryRoot $expected.path
            if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
                throw "缺少预期项目：$($expected.path)"
            }
            $evaluated = Get-DshProjectPlatform -RepositoryRoot $repositoryRoot -ProjectPath $projectPath
            if ($evaluated.TargetFramework -cne $expected.targetFramework -or
                $evaluated.RuntimeIdentifier -cne $expected.runtimeIdentifier) {
                throw ("$($expected.name) 平台身份偏离配置：" +
                    "TargetFramework='$($evaluated.TargetFramework)'（预期 '$($expected.targetFramework)'），" +
                    "RuntimeIdentifier='$($evaluated.RuntimeIdentifier)'（预期 '$($expected.runtimeIdentifier)'）。")
            }
        }
    }

    $null = Invoke-DshPortableStep -Id 'portable-solution-shape' -Description '可移植子集只含声明过的可移植项目' -Body {
        if (-not (Test-Path -LiteralPath $portableSolution -PathType Leaf)) {
            throw "缺少可移植 solution：$($config.portableSolution.path)"
        }
        [xml] $solutionXml = Get-Content -LiteralPath $portableSolution -Raw -Encoding UTF8
        $actual = @($solutionXml.SelectNodes('//Project') |
                ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.GetAttribute('Path')) } |
                Sort-Object)
        $expectedNames = @($config.portableSolution.projects | Sort-Object)
        if (($actual -join '|') -ne ($expectedNames -join '|')) {
            throw "可移植 solution 项目不符。实际：$($actual -join ', ')；预期：$($expectedNames -join ', ')"
        }
        # 完整 solution 仍须保留全部项目与锁文件，防止可移植化把 Windows 工程删掉。
        if (-not (Test-Path -LiteralPath $fullSolution -PathType Leaf)) {
            throw "缺少完整 solution：$($config.fullSolution.path)"
        }
        [xml] $fullXml = Get-Content -LiteralPath $fullSolution -Raw -Encoding UTF8
        $fullCount = @($fullXml.SelectNodes('//Project')).Count
        if ($fullCount -ne [int] $config.fullSolution.projectCount) {
            throw "完整 solution 项目数必须为 $($config.fullSolution.projectCount)，实际 $fullCount。"
        }
        $lockFiles = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src'), (Join-Path $repositoryRoot 'tests') `
                -Filter 'packages.lock.json' -File -Recurse |
                Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' })
        if ($lockFiles.Count -ne [int] $config.fullSolution.lockFileCount) {
            throw "锁文件数必须为 $($config.fullSolution.lockFileCount)，实际 $($lockFiles.Count)。"
        }
    }

    Write-Host ''
    Write-Host '== Core 阶段 =='
    $null = Invoke-DshPortableStep -Id 'architecture-constraints' -Description 'Core 纯净性与固定项目依赖图' -Body {
        $violations = @(Get-DshArchitectureViolations -RepositoryRoot $repositoryRoot)
        if ($violations.Count -gt 0) {
            throw "架构约束失败：`n$($violations -join [Environment]::NewLine)"
        }
    }

    $null = Invoke-DshPortableStep -Id 'core-locked-restore' -Description 'locked restore（可移植子集）' -Body {
        Invoke-DshNative -FilePath $dotnet -Arguments @('restore', $portableSolution, '--locked-mode') -WorkingDirectory $repositoryRoot
    }

    $null = Invoke-DshPortableStep -Id 'core-format' -Description 'dotnet format --verify-no-changes' -Body {
        Invoke-DshNative -FilePath $dotnet -Arguments @('format', $portableSolution, '--verify-no-changes', '--no-restore') -WorkingDirectory $repositoryRoot
    }

    $null = Invoke-DshPortableStep -Id 'core-build' -Description 'Release 构建（warnaserror）' -Body {
        Invoke-DshNative -FilePath $dotnet -Arguments @('build', $portableSolution, '--configuration', 'Release', '--no-restore', '--warnaserror') -WorkingDirectory $repositoryRoot
    }

    $null = Invoke-DshPortableStep -Id 'core-test-execution' -Description '实际执行 Core 用例集（manifest 的 core 集合）并与固定数据集逐条核对' -Body {
        # Core 与 Interop 是同一程序集里两个显式集合（D18 起）：这里只跑 core，
        # interop 集合需要 Node/Chromium/夹具，由 production-interop-l13 单独执行。
        $result = Invoke-DshTestAssemblyCheck `
            -RepositoryRoot $repositoryRoot `
            -DotNetPath $dotnet `
            -ProjectPath $coreTestProject `
            -RunnerConfig $config.testRunner `
            -ExpectedDataset $expectedDataset `
            -ArtifactsDirectory $ArtifactsDirectory `
            -Failures $failures `
            -CaseSet 'core'
        $testResults.Add($result)
        if ($result.result -ne 'pass') {
            throw "Core 用例集未通过：total=$($result.total) passed=$($result.passed) failed=$($result.failed) skipped=$($result.skipped) notRun=$($result.notRun)。"
        }
    }

    # ---- Development 附加检查 ----
    $crossBuildStatus = 'notRun'
    if ($Profile -eq 'Development') {
        Write-Host ''
        Write-Host '== Development 附加检查 =='
        $crossBuildStatus = Invoke-DshPortableStep -Id 'cross-build-l12' -Description '完整 solution 交叉构建（记录边界）' -Body {
            $crossBuildLog = Join-Path $ArtifactsDirectory 'd12-cross-build.log'
            $buildOutput = ''
            try {
                $buildOutput = Invoke-DshNativeCapture -FilePath $dotnet -Arguments @(
                    'build', $fullSolution, '--configuration', 'Release', '--no-restore', '--warnaserror'
                ) -WorkingDirectory $repositoryRoot
            }
            catch {
                # 失败也必须留下可在 D24 覆盖表里引用的原始日志（否则 L12 只有结论没有证据）。
                $buildOutput = "完整 solution 交叉构建失败：`n" + $_.Exception.Message
                Set-Content -LiteralPath $crossBuildLog -Value $buildOutput -Encoding utf8NoBOM
                throw
            }
            Set-Content -LiteralPath $crossBuildLog -Value $buildOutput -Encoding utf8NoBOM
        }

        $null = Invoke-DshPortableStep -Id 'supply-chain-scan' -Description '漏洞/弃用扫描、SBOM 与秘密扫描' -Body {
            $vulnerableJson = Invoke-DshNativeCapture -FilePath $dotnet -Arguments @(
                'list', $fullSolution, 'package', '--vulnerable', '--include-transitive', '--format', 'json', '--output-version', '1'
            ) -WorkingDirectory $repositoryRoot
            Assert-DshPackageReportClean -Json $vulnerableJson -Kind vulnerable

            $deprecatedJson = Invoke-DshNativeCapture -FilePath $dotnet -Arguments @(
                'list', $fullSolution, 'package', '--deprecated', '--include-transitive', '--format', 'json', '--output-version', '1'
            ) -WorkingDirectory $repositoryRoot
            Assert-DshPackageReportClean -Json $deprecatedJson -Kind deprecated

            $secretFindings = @(Find-DshSecretFindings -RepositoryRoot $repositoryRoot)
            if ($secretFindings.Count -gt 0) {
                throw "秘密扫描命中：`n$($secretFindings -join [Environment]::NewLine)"
            }
        }

        # ---- 附加插件（D03 起逐步接入；未实现的仍记 incomplete） ----
        Write-Host ''
        Write-Host '== 附加插件 =='
        $pluginRoot = Join-Path $repositoryRoot 'plugins/dsh-remote-attachments'

        $null = Invoke-DshPortableStep -Id 'plugin-preflight' -Description '插件骨架、锁定依赖与脚本声明在位' -Body {
            if (-not (Test-Path -LiteralPath $pluginRoot -PathType Container)) {
                throw '缺少附加插件目录：plugins/dsh-remote-attachments'
            }
            $pluginManifestPath = Join-Path $pluginRoot 'package.json'
            if (-not (Test-Path -LiteralPath $pluginManifestPath -PathType Leaf)) {
                throw '缺少插件 package.json'
            }
            $pluginManifest = Get-Content -LiteralPath $pluginManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            # 独立包身份：不得占用全家桶或 remote 插件的包名。
            foreach ($forbidden in @('@linxin666/dsh-web-all', '@linxin666/dsh-remote-web-ui')) {
                if ($pluginManifest.name -eq $forbidden) {
                    throw "插件包名不得占用 $forbidden"
                }
            }
            if ([string]::IsNullOrWhiteSpace([string] $pluginManifest.name)) {
                throw '插件必须有独立的包名'
            }
            # 固定依赖：不得出现浮动范围。StrictMode 下必须按属性名存在性判断，不能直接取属性。
            foreach ($section in @('dependencies', 'devDependencies')) {
                if ($pluginManifest.PSObject.Properties.Name -notcontains $section) { continue }
                foreach ($property in $pluginManifest.$section.PSObject.Properties) {
                    # 精确锁定：允许 semver 预发布后缀（如 0.1.5-rc.1），但不允许 ^ ~ * 或区间。
                    if ([string] $property.Value -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
                        throw "插件依赖 $($property.Name) 未锁定精确版本：$($property.Value)"
                    }
                }
            }
            # 单一包管理器：只允许 pnpm 锁文件，避免两份锁文件漂移。
            if (Test-Path -LiteralPath (Join-Path $pluginRoot 'package-lock.json') -PathType Leaf) {
                throw '插件不得同时保留 package-lock.json；本仓库插件统一使用 pnpm-lock.yaml。'
            }
            foreach ($requiredScript in @('typecheck', 'build', 'test:unit', 'test:pack')) {
                if ($pluginManifest.scripts.PSObject.Properties.Name -notcontains $requiredScript) {
                    throw "插件缺少必需脚本：$requiredScript"
                }
            }
            # 骨架文件与已安装依赖必须在位，避免脚本退化成空壳或被跳过。
            foreach ($requiredFile in @('tsconfig.json', 'pnpm-lock.yaml', 'src/host.ts', 'src/client/index.ts', 'scripts/build-client-bundle.mjs', 'cordis.patch.yml')) {
                if (-not (Test-Path -LiteralPath (Join-Path $pluginRoot $requiredFile) -PathType Leaf)) {
                    throw "插件缺少必需文件：$requiredFile"
                }
            }
            if (-not (Test-Path -LiteralPath (Join-Path $pluginRoot 'node_modules') -PathType Container)) {
                throw '插件依赖未安装（缺少 node_modules）'
            }
        }

        foreach ($pluginCheck in @(
            [pscustomobject]@{ Id = 'plugin-typecheck'; Script = 'typecheck' },
            [pscustomobject]@{ Id = 'plugin-build'; Script = 'build' },
            [pscustomobject]@{ Id = 'plugin-unit-tests'; Script = 'test:unit' },
            [pscustomobject]@{ Id = 'plugin-pack'; Script = 'test:pack' }
        )) {
            $check = $pluginCheck
            $null = Invoke-DshPortableStep -Id $check.Id -Description "pnpm --dir plugins/dsh-remote-attachments run $($check.Script)" -Body {
                # 冻结安装：门禁不得就地改动依赖树。
                Invoke-DshNative -FilePath $pnpm -Arguments @(
                    '--dir', $pluginRoot, 'install', '--frozen-lockfile'
                ) -WorkingDirectory $repositoryRoot
                Invoke-DshNative -FilePath $pnpm -Arguments @(
                    '--dir', $pluginRoot, 'run', $check.Script
                ) -WorkingDirectory $repositoryRoot
            }
        }

        $null = Invoke-DshPortableStep -Id 'plugin-browser-tests' -Description '插件浏览器层资源与背压边界（D21）：真实 Chromium 里最大文件/批次、2 块在途窗口、取消回收、暂停消费者、连续 30 轮与真实可移植 Core 的资源上界' -Body {
            # 接入理由（D21 要求：只在证据**确属该层**时才实现本检查，不得强行映射）：
            # 本检查的实现就是 D21 的浏览器层资源采样，它在**真实 Chromium**里驱动**已构建插件**的
            # 生产接收端（组合依赖由插件真实装配、导入走真实桥 → 生产草稿适配器），逐条断言
            # L04/L05 的关键判据："最多两块在途""超限拒绝"、缓冲/引用释放、连续操作无无界增长，
            # 并附带真实可移植 Core 的慢 ACK/闸门/取消/所有权指标。因此它确属"插件浏览器层测试"。
            # 边界诚实标注：composer/DOM 健康、草稿适配器、桥握手/降级、闭环 upload 各有**自己已实现**
            # 的检查（client-composer-fixture / draft-adapter-fixture / addon-bridge-fixture /
            # host-fixture-minimal-loop），本检查**不**声称覆盖那些维度。
            $resourcesEvidence = Join-Path $ArtifactsDirectory 'd21-resource-gates.json'
            $resourcesReport = Join-Path $ArtifactsDirectory 'd21-resource-report.json'
            $fixtureResourcesEvidence = Join-Path $repositoryRoot 'artifacts/fixture/evidence/d21-resource-gates.json'
            $resourcesScript = Join-Path $pluginRoot 'tests/fixtures/d21-resource-gates.mjs'
            $resourcesCriteria = Join-Path $pluginRoot 'tests/fixtures/d21-resource-criteria.mjs'
            foreach ($requiredResourcePath in @($resourcesScript, $resourcesCriteria)) {
                if (-not (Test-Path -LiteralPath $requiredResourcePath -PathType Leaf)) {
                    throw "缺少 D21 资源判据实现文件：$requiredResourcePath"
                }
            }
            # 陈旧绿灯不得冒充本轮结果。
            foreach ($staleResourceEvidence in @($resourcesEvidence, $resourcesReport, $fixtureResourcesEvidence)) {
                if (Test-Path -LiteralPath $staleResourceEvidence -PathType Leaf) {
                    Remove-Item -LiteralPath $staleResourceEvidence -Force
                }
            }
            # 判据必须跑在**当前**产物与**干净**夹具上（会话/工作区是跨运行残留状态）。
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/setup.sh')) -WorkingDirectory $repositoryRoot
            # Core 侧指标由本检查内的 dotnet 子进程产出：显式传入本脚本解析出的 SDK 路径。
            $previousD21Dotnet = $env:DSH_DOTNET
            $env:DSH_DOTNET = $dotnet
            try {
                Invoke-DshNative -FilePath $node -Arguments @($resourcesScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                if ($null -eq $previousD21Dotnet) { Remove-Item Env:DSH_DOTNET -ErrorAction SilentlyContinue }
                else { $env:DSH_DOTNET = $previousD21Dotnet }
                Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $resourcesEvidence -PathType Leaf)) {
                throw "D21 资源判据未产出证据：$resourcesEvidence"
            }
            if (-not (Test-Path -LiteralPath $resourcesReport -PathType Leaf)) {
                throw "D21 资源判据未产出采样报告：$resourcesReport"
            }
            $resourcesReportJson = Get-Content -LiteralPath $resourcesReport -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            $resourcesGateJson = Get-Content -LiteralPath $resourcesEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            if ($resourcesGateJson.task -ne 'D21' -or $resourcesGateJson.purpose -ne 'backpressure-and-resources') {
                throw "D21 证据身份不符：task=$($resourcesGateJson.task) purpose=$($resourcesGateJson.purpose)"
            }
            if ($resourcesGateJson.result -ne 'pass' -or @($resourcesGateJson.failedGateIds).Count -gt 0) {
                throw "D21 判据未全部通过：failed=$(@($resourcesGateJson.failedGateIds) -join ', ')"
            }
            if (@($resourcesGateJson.failedNegativeIds).Count -gt 0) {
                throw "D21 负向探针未全部成立：failed=$(@($resourcesGateJson.failedNegativeIds) -join ', ')"
            }
            # 每个计量项都必须有单位/采样点/阈值/实测值；一行缺失就不算"有可测上界"。
            $metricRows = @($resourcesReportJson.metrics)
            if ($metricRows.Count -lt 40) {
                throw "D21 采样报告行数不足（应覆盖浏览器+Core+单元三层）：$($metricRows.Count)"
            }
            foreach ($metricRow in $metricRows) {
                foreach ($requiredField in @('unit', 'samplingPoint', 'threshold', 'measured', 'pass', 'scenario')) {
                    if ($null -eq $metricRow.$requiredField -or [string]::IsNullOrWhiteSpace([string] $metricRow.$requiredField)) {
                        throw "D21 计量项 $($metricRow.id) 缺少字段 $requiredField（每个计量项必须有单位、采样点、阈值与实测值）"
                    }
                }
            }
            if (@($metricRows | Where-Object { $_.pass -ne $true }).Count -gt 0) {
                throw "D21 存在越界计量项：$((@($metricRows | Where-Object { $_.pass -ne $true } | ForEach-Object { $_.id })) -join ', ')"
            }
            if (@($metricRows | Where-Object { $_.notRun -eq $true }).Count -gt 0) {
                throw "D21 存在未采样计量项（未验不得算通过）：$((@($metricRows | Where-Object { $_.notRun -eq $true } | ForEach-Object { $_.id })) -join ', ')"
            }
            # 连续 30 轮序列必须是**完整序列**，不能只留首末两点。
            $seriesNames = @($resourcesReportJson.series.PSObject.Properties.Name)
            foreach ($requiredSeries in @('browserHeapBytesPerRound', 'browserLeakControlBytesPerRound', 'browserReceiverOnlyBytesPerRound', 'coreManagedHeapBytesPerRound')) {
                if ($seriesNames -notcontains $requiredSeries) {
                    throw "D21 采样报告缺少序列：$requiredSeries（连续 30 轮必须留下完整序列）"
                }
                if (@($resourcesReportJson.series.$requiredSeries.values).Count -lt 30) {
                    throw "D21 序列 $requiredSeries 点数不足 30：$(@($resourcesReportJson.series.$requiredSeries.values).Count)"
                }
            }
            if ($resourcesReportJson.series.browserLeakControlBytesPerRound.analysis.slopeBytesPerRound -le 0) {
                throw 'D21 无界路径反例控制未体现增长（斜率 ≤ 0）：该控制已失去证明力'
            }
            # 每次运行独立 runId 目录：历史不得被覆写，runId 目录里必须有本轮原始报告。
            $runResourceReport = Join-Path $repositoryRoot ("artifacts/verify-portable/d21-runs/{0}/report.json" -f $resourcesReportJson.runId)
            if (-not (Test-Path -LiteralPath $runResourceReport -PathType Leaf)) {
                throw "D21 缺少 runId 目录内的原始报告：$runResourceReport"
            }
            if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'artifacts/verify-portable/d21-runs/index.json') -PathType Leaf)) {
                throw 'D21 缺少运行索引：artifacts/verify-portable/d21-runs/index.json'
            }
            Write-Host ("D21 指标：{0}/{1} 在阈值内；未验={2}；序列点={3}" -f `
                    (@($metricRows | Where-Object { $_.pass -eq $true }).Count), $metricRows.Count, `
                    (@($resourcesReportJson.notRun).Count), (@($resourcesReportJson.series.browserHeapBytesPerRound.values).Count))
            Write-Host ("D21 未验项：" + ((@($resourcesReportJson.notRun | ForEach-Object { $_.id }) -join ', ')))
            Write-Host ("D21 WindowsPending：" + ((@($resourcesReportJson.windowsPending) -join '；')))
            # 夹具侧副本：down.sh 会删除夹具目录，因此按 D12/D13/D18/D20 同款顺序在收尾后补写。
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $fixtureResourcesEvidence) -Force
            Copy-Item -LiteralPath $resourcesEvidence -Destination $fixtureResourcesEvidence -Force
        }

        # ---- 真实 Harness＋全家桶夹具（D04） ----
        Write-Host ''
        Write-Host '== 真实宿主夹具 =='
        $null = Invoke-DshPortableStep -Id 'wire-contract-goldens' -Description '线协议 v1 冻结：语料↔schema 一致、TS 生产 codec 全语料、SHA-256 交叉验证、三态分离与重放缓存' -Body {
            # 这一项守的是"契约本身"：C# 侧由 core-test-execution 覆盖（同一份 expected.json），
            # TS 侧由 plugin-unit-tests 覆盖；本项额外守住语料/ schema 的一致性与跨语言差分产物，
            # 这些普通单测覆盖不到。W00 还会拒绝"对旧构建给出假绿"。
            $wireEvidence = Join-Path $ArtifactsDirectory 'd10-wire-gates.json'
            $wireScript = Join-Path $pluginRoot 'scripts/wire-contract-gates.mjs'
            if (-not (Test-Path -LiteralPath $wireScript -PathType Leaf)) {
                throw "缺少线协议门禁脚本：$wireScript"
            }
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:wire') -WorkingDirectory $repositoryRoot
            if (-not (Test-Path -LiteralPath $wireEvidence -PathType Leaf)) {
                throw "线协议门禁未产出证据：$wireEvidence"
            }
            $wireReport = Get-Content -LiteralPath $wireEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($wireReport.result -ne 'pass' -or @($wireReport.failedGateIds).Count -gt 0) {
                throw "线协议门禁未全部通过：failed=$(@($wireReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'host-fixture-bundle' -Description '真实 Harness＋all＋本插件启动与挂载校验' -Body {
            $fixtureEvidence = Join-Path $ArtifactsDirectory 'd04-fixture-gates.json'
            # 夹具可能残留上次运行的服务，先按夹具自有规则清理（只动夹具资源）。
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/down.sh')
            ) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
            ) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/run-gates.mjs')
                ) -WorkingDirectory $repositoryRoot
            }
            finally {
                # 无论门禁成败都要收拢夹具资源，避免残留服务。
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $fixtureEvidence -PathType Leaf)) {
                throw "夹具未产出证据：$fixtureEvidence"
            }
            $fixtureReport = Get-Content -LiteralPath $fixtureEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($fixtureReport.result -ne 'pass' -or @($fixtureReport.failedGateIds).Count -gt 0) {
                throw "夹具判据未全部通过：failed=$(@($fixtureReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'pairing-fixture' -Description '测试配对与 /remote 门控（非 loopback，cookie 与免 Cookie 双流）' -Body {
            $pairingEvidence = Join-Path $ArtifactsDirectory 'd05-pairing-gates.json'
            # 夹具可能已由 host-fixture-bundle 建好；只在**profile 真正存在**时复用。
            # 不能用 lan-address.txt 判断：tls-setup.sh 也会写该文件，会导致这里误判为
            # 已建好而跳过安装（实测使服务因缺 profile 直接退出，退出码 1）。
            $fixtureProfileManifest = Join-Path $repositoryRoot 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/package.json'
            if (-not (Test-Path -LiteralPath $fixtureProfileManifest -PathType Leaf)) {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            try {
                Invoke-DshNative -FilePath $node -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/pairing-gates.mjs')
                ) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $pairingEvidence -PathType Leaf)) {
                throw "配对夹具未产出证据：$pairingEvidence"
            }
            $pairingReport = Get-Content -LiteralPath $pairingEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($pairingReport.result -ne 'pass' -or @($pairingReport.failedGateIds).Count -gt 0) {
                throw "配对判据未全部通过：failed=$(@($pairingReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'pairing-fixture-https' -Description 'HTTPS + 受信任测试 CA 下的配对与 /remote' -Body {
            $httpsEvidence = Join-Path $ArtifactsDirectory 'd05-https-gates.json'
            $tlsProxyPath = Join-Path $pluginRoot 'tests/fixtures/tls-proxy.mjs'
            if (-not (Test-Path -LiteralPath $tlsProxyPath -PathType Leaf)) {
                throw "缺少 TLS 代理模块：$tlsProxyPath"
            }
            # 上一步检查的 finally 会收拢夹具，因此这里必须能自行补建，
            # 否则 HTTPS 检查会因为缺 profile 而失败（实测退出码 1）。
            $httpsFixtureProfile = Join-Path $repositoryRoot 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/package.json'
            if (-not (Test-Path -LiteralPath $httpsFixtureProfile -PathType Leaf)) {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/tls-setup.sh')
            ) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/https-gates.mjs')
                ) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $httpsEvidence -PathType Leaf)) {
                throw "HTTPS 夹具未产出证据：$httpsEvidence"
            }
            $httpsReport = Get-Content -LiteralPath $httpsEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($httpsReport.result -ne 'pass' -or @($httpsReport.failedGateIds).Count -gt 0) {
                throw "HTTPS 判据未全部通过：failed=$(@($httpsReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'provider-fixture' -Description '确定性 provider stub 驱动真实 Harness 工具读取与核对' -Body {
            $providerEvidence = Join-Path $ArtifactsDirectory 'd06-provider-gates.json'
            $providerScript = Join-Path $pluginRoot 'tests/fixtures/provider/run-provider-gates.mjs'
            if (-not (Test-Path -LiteralPath $providerScript -PathType Leaf)) {
                throw "缺少 provider 判据脚本：$providerScript"
            }
            # 无头 profile 由 setup.sh 准备；缺失时自行补建（前面的检查会收拢夹具）。
            $headlessMarker = Join-Path $repositoryRoot 'artifacts/fixture/headless-profile.txt'
            if (-not (Test-Path -LiteralPath $headlessMarker -PathType Leaf)) {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            Invoke-DshNative -FilePath $node -Arguments @($providerScript) -WorkingDirectory $repositoryRoot
            if (-not (Test-Path -LiteralPath $providerEvidence -PathType Leaf)) {
                throw "provider 判据未产出证据：$providerEvidence"
            }
            $providerReport = Get-Content -LiteralPath $providerEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($providerReport.result -ne 'pass' -or @($providerReport.failedGateIds).Count -gt 0) {
                throw "provider 判据未全部通过：failed=$(@($providerReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'upload-hook-fixture' -Description '启动前上传承载：注入顺序与真实 Chromium 网络行为' -Body {
            $uploadEvidence = Join-Path $ArtifactsDirectory 'd07-upload-gates.json'
            $uploadScript = Join-Path $pluginRoot 'tests/fixtures/upload-gates.mjs'
            if (-not (Test-Path -LiteralPath $uploadScript -PathType Leaf)) {
                throw "缺少上传承载判据脚本：$uploadScript"
            }
            # 判据要针对最终 bundle：先构建，再确保夹具里装的是当前 tarball。
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            $fixtureProfileManifest = Join-Path $repositoryRoot 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/package.json'
            if (-not (Test-Path -LiteralPath $fixtureProfileManifest -PathType Leaf)) {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/install-plugin.sh')
            ) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($uploadScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $uploadEvidence -PathType Leaf)) {
                throw "上传承载判据未产出证据：$uploadEvidence"
            }
            $uploadReport = Get-Content -LiteralPath $uploadEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($uploadReport.result -ne 'pass' -or @($uploadReport.failedGateIds).Count -gt 0) {
                throw "上传承载判据未全部通过：failed=$(@($uploadReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'client-composer-fixture' -Description 'composer 健康门：真实 Chromium 中客户端插件真实装配、composer 与原生文件入口真实渲染' -Body {
            # 存在理由（D08 教训）：host-fixture-bundle 的 F05 只检查页面骨架，因此
            # "页面 200 + 有骨架" 曾掩盖 "client 模块整批解析失败、composer 根本没渲染"。
            # 本检查断言 UI 真的可用，而不只是文档能取回。
            $composerEvidence = Join-Path $ArtifactsDirectory 'd08-composer-gates.json'
            $composerScript = Join-Path $pluginRoot 'tests/fixtures/composer-gates.mjs'
            if (-not (Test-Path -LiteralPath $composerScript -PathType Leaf)) {
                throw "缺少 composer 健康门脚本：$composerScript"
            }
            # 必须针对最终 bundle：先构建、再打包、再把当前 tarball 装进夹具。顺序不能省，
            # 否则夹具会继续跑 pack/ 里的旧 tarball，得到"通过"的假象。
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            $fixtureProfileManifest = Join-Path $repositoryRoot 'artifacts/fixture/dsh-home/profiles/dsh-attachments-fixture/package.json'
            if (-not (Test-Path -LiteralPath $fixtureProfileManifest -PathType Leaf)) {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/install-plugin.sh')
            ) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($composerScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $composerEvidence -PathType Leaf)) {
                throw "composer 健康门未产出证据：$composerEvidence"
            }
            $composerReport = Get-Content -LiteralPath $composerEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($composerReport.result -ne 'pass' -or @($composerReport.failedGateIds).Count -gt 0) {
                throw "composer 健康门未全部通过：failed=$(@($composerReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'draft-adapter-fixture' -Description '草稿适配器：真实会话中的同步导入 ACK（原生 attachmentIds）与切会话拒绝' -Body {
            # 与 composer 门的关键区别：判据取自**原生输入状态**的 attachmentIds，而不是
            # dispatchEvent 返回值或文件名卡片。会话由 headless 一次性任务在共享 DSH_HOME 中造出，
            # 无会话首页在造会话之前判定（有工作区后会自动导航进会话）。
            $adapterEvidence = Join-Path $ArtifactsDirectory 'd08-ui-gates.json'
            $adapterScript = Join-Path $pluginRoot 'tests/fixtures/d08-ui-gates.mjs'
            if (-not (Test-Path -LiteralPath $adapterScript -PathType Leaf)) {
                throw "缺少草稿适配器判据脚本：$adapterScript"
            }
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            # 会话与工作区是跨运行残留的状态，必须从干净夹具开始：先清掉再重建。
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/down.sh')
            ) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
            ) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($adapterScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $adapterEvidence -PathType Leaf)) {
                throw "草稿适配器判据未产出证据：$adapterEvidence"
            }
            $adapterReport = Get-Content -LiteralPath $adapterEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($adapterReport.result -ne 'pass' -or @($adapterReport.failedGateIds).Count -gt 0) {
                throw "草稿适配器判据未全部通过：failed=$(@($adapterReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'browser-receiver-fixture' -Description '浏览器接收端：真实 Chromium 中分块组装成真实 File、无 WebCrypto 校验、取消回收与上界' -Body {
            # 判据必须在真实浏览器里核对 File 的字节（不是 mock 对象），并证明 HTTP 下
            # 没有"跳过完整性校验"的分支。
            $receiverEvidence = Join-Path $ArtifactsDirectory 'd12-receiver-gates.json'
            $receiverScript = Join-Path $pluginRoot 'tests/fixtures/d12-receiver-gates.mjs'
            if (-not (Test-Path -LiteralPath $receiverScript -PathType Leaf)) {
                throw "缺少接收端判据脚本：$receiverScript"
            }
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/down.sh')
            ) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
            ) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($receiverScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $receiverEvidence -PathType Leaf)) {
                throw "接收端判据未产出证据：$receiverEvidence"
            }
            $receiverReport = Get-Content -LiteralPath $receiverEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($receiverReport.result -ne 'pass' -or @($receiverReport.failedGateIds).Count -gt 0) {
                throw "接收端判据未全部通过：failed=$(@($receiverReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'addon-bridge-fixture' -Description '附加插件桥：握手/限额钳制、能力冲突与远端降级、拆解撤销、配对不受影响' -Body {
            $bridgeEvidence = Join-Path $ArtifactsDirectory 'd13-bridge-gates.json'
            $bridgeScript = Join-Path $pluginRoot 'tests/fixtures/d13-bridge-gates.mjs'
            if (-not (Test-Path -LiteralPath $bridgeScript -PathType Leaf)) {
                throw "缺少附加插件桥判据脚本：$bridgeScript"
            }
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/setup.sh')) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($bridgeScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $bridgeEvidence -PathType Leaf)) {
                throw "附加插件桥判据未产出证据：$bridgeEvidence"
            }
            $bridgeReport = Get-Content -LiteralPath $bridgeEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($bridgeReport.result -ne 'pass' -or @($bridgeReport.failedGateIds).Count -gt 0) {
                throw "附加插件桥判据未全部通过：failed=$(@($bridgeReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'resource-and-failure-suites' -Description '远端上传故障与撤销语义（D19）：可重复故障注入、错误可见/不自动重发/不误投、撤销语义分开断言、脱敏 trace' -Body {
            # 本检查的"实现"是 D19 判据脚本 + 故障注入代理：
            #   - tests/fixtures/fault-proxy.mjs 是 Linux 网络夹具，坐在页面 origin 上，
            #     把故障注入到**真实**上传路径（/remote/api/session/uploadFileBinary 经配对非 loopback 页面）；
            #   - tests/fixtures/d19-failure-gates.mjs 在真实 Chromium 里逐故障断言，并写出脱敏 trace。
            # 与其它 fixture 检查同款：先保证 tarball 是当前产物，再从干净夹具开始，收尾必 down。
            $failureEvidence = Join-Path $ArtifactsDirectory 'd19-failure-gates.json'
            $failureTrace = Join-Path $ArtifactsDirectory 'd19-failure-trace.json'
            $fixtureFailureEvidence = Join-Path $repositoryRoot 'artifacts/fixture/evidence/d19-failure-gates.json'
            $failureScript = Join-Path $pluginRoot 'tests/fixtures/d19-failure-gates.mjs'
            $faultProxyScript = Join-Path $pluginRoot 'tests/fixtures/fault-proxy.mjs'
            foreach ($requiredFailurePath in @($failureScript, $faultProxyScript)) {
                if (-not (Test-Path -LiteralPath $requiredFailurePath -PathType Leaf)) {
                    throw "缺少 D19 故障注入实现文件：$requiredFailurePath"
                }
            }
            # 先删除上一轮证据：旧绿灯不得冒充本轮结果。
            foreach ($staleFailureEvidence in @($failureEvidence, $failureTrace, $fixtureFailureEvidence)) {
                if (Test-Path -LiteralPath $staleFailureEvidence -PathType Leaf) {
                    Remove-Item -LiteralPath $staleFailureEvidence -Force
                }
            }
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/setup.sh')) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($failureScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $failureEvidence -PathType Leaf)) {
                throw "故障与撤销判据未产出证据：$failureEvidence"
            }
            if (-not (Test-Path -LiteralPath $failureTrace -PathType Leaf)) {
                throw "故障与撤销判据未产出脱敏 trace：$failureTrace"
            }
            if ((Get-Item -LiteralPath $failureTrace).Length -le 0) {
                throw "脱敏 trace 为空文件：$failureTrace"
            }
            $failureReport = Get-Content -LiteralPath $failureEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($failureReport.result -ne 'pass' -or @($failureReport.failedGateIds).Count -gt 0) {
                throw "故障与撤销判据未全部通过：failed=$(@($failureReport.failedGateIds) -join ', ')"
            }
            # 脱敏是硬要求：trace 落盘后必须零泄漏（pair=/cookie/设备 ID/绝对家目录路径）。
            $failureTraceText = Get-Content -LiteralPath $failureTrace -Raw -Encoding UTF8
            $failureLeaks = @()
            foreach ($failureLeakPattern in @(
                    'pair=[A-Za-z0-9_-]{8,}',
                    'dsh_pair=[A-Za-z0-9_-]{8,}',
                    '"token"\s*:\s*"[A-Za-z0-9_-]{8,}"',
                    '"deviceId"\s*:\s*"[A-Za-z0-9_-]{8,}"',
                    '/(home|Users)/[A-Za-z0-9._-]+/'
                )) {
                if ($failureTraceText -match $failureLeakPattern) { $failureLeaks += $failureLeakPattern }
            }
            if ($failureLeaks.Count -gt 0) {
                throw "脱敏 trace 仍含敏感形态：$($failureLeaks -join '; ')"
            }
            # 夹具侧副本：down.sh 会删除夹具目录，因此按 D12/D13/D18 同款顺序在收尾后补写。
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $fixtureFailureEvidence) -Force
            Copy-Item -LiteralPath $failureEvidence -Destination $fixtureFailureEvidence -Force
        }

        $null = Invoke-DshPortableStep -Id 'host-fixture-minimal-loop' -Description 'Linux 最小附件发送闭环：草稿 / 网络 / receipt / 工具字节四层一致（D09 架构风险闸门）' -Body {
            # 四层证据必须一致，且不得绕过生产适配器（不允许 setInputFiles 或测试页捷径）。
            # 依赖：D07 的上传承载形状（fetch 形状载体）+ D08 的版本化桥。
            $loopEvidence = Join-Path $ArtifactsDirectory 'd09-loop-gates.json'
            $loopScript = Join-Path $pluginRoot 'tests/fixtures/d09-loop-gates.mjs'
            if (-not (Test-Path -LiteralPath $loopScript -PathType Leaf)) {
                throw "缺少闭环判据脚本：$loopScript"
            }
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            # 会话与工作区是跨运行残留状态：从干净夹具开始，避免上一轮的会话/工作区影响判据。
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/down.sh')
            ) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
            ) -WorkingDirectory $repositoryRoot
            $previousApiKey = $env:DEEPSEEK_API_KEY
            $env:DEEPSEEK_API_KEY = 'stub-key'
            try {
                Invoke-DshNative -FilePath $node -Arguments @($loopScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                if ($null -eq $previousApiKey) { Remove-Item Env:DEEPSEEK_API_KEY -ErrorAction SilentlyContinue }
                else { $env:DEEPSEEK_API_KEY = $previousApiKey }
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $loopEvidence -PathType Leaf)) {
                throw "闭环判据未产出证据：$loopEvidence"
            }
            $loopReport = Get-Content -LiteralPath $loopEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($loopReport.result -ne 'pass' -or @($loopReport.failedGateIds).Count -gt 0) {
                throw "闭环判据未全部通过：failed=$(@($loopReport.failedGateIds) -join ', ')"
            }
        }

        $null = Invoke-DshPortableStep -Id 'production-interop-l13' -Description '真实 C# Core ↔ 真实 Chromium 生产接收端 ↔ 真实草稿适配器的生产互通（D18）' -Body {
            # 本检查的"实现"就在 Core.Tests 的 Interop 用例集里：用例自己 spawn
            # plugins/dsh-remote-attachments/tests/interop/d18-interop-driver.mjs，驱动把
            # 真实 C# codec/协调器产出的帧经 stdio 送进真实 Chromium 页面里的生产接收端
            # （D12 页面全局），再把 ack/import-result 送回同一个 Core。此处只负责：
            #   1) 保证夹具与 tarball 是**当前**产物（ensure-fixture.sh --force 负责 build+pack+setup）；
            #   2) 以 manifest 指定的 Interop 集合执行并逐条核对哈希清单（零跳过）；
            #   3) 要求用例写出的证据存在、非陈旧且 result=pass。
            $interopEvidence = Join-Path $ArtifactsDirectory 'd18-interop-gates.json'
            $fixtureInteropEvidence = Join-Path $repositoryRoot 'artifacts/fixture/evidence/d18-interop-gates.json'
            $ensureInteropFixture = Join-Path $pluginRoot 'tests/interop/ensure-fixture.sh'
            $interopDriver = Join-Path $pluginRoot 'tests/interop/d18-interop-driver.mjs'
            foreach ($requiredInteropPath in @($ensureInteropFixture, $interopDriver)) {
                if (-not (Test-Path -LiteralPath $requiredInteropPath -PathType Leaf)) {
                    throw "缺少 D18 互通实现文件：$requiredInteropPath"
                }
            }
            # 先删除上一轮证据：旧绿灯不得冒充本轮结果。
            foreach ($staleEvidence in @($interopEvidence, $fixtureInteropEvidence)) {
                if (Test-Path -LiteralPath $staleEvidence -PathType Leaf) {
                    Remove-Item -LiteralPath $staleEvidence -Force
                }
            }
            Invoke-DshNative -FilePath 'bash' -Arguments @($ensureInteropFixture, '--force') -WorkingDirectory $repositoryRoot
            try {
                $interopResult = Invoke-DshTestAssemblyCheck `
                    -RepositoryRoot $repositoryRoot `
                    -DotNetPath $dotnet `
                    -ProjectPath $coreTestProject `
                    -RunnerConfig $config.testRunner `
                    -ExpectedDataset $expectedDataset `
                    -ArtifactsDirectory $ArtifactsDirectory `
                    -Failures $failures `
                    -CaseSet 'interop'
                $testResults.Add($interopResult)
                if ($interopResult.result -ne 'pass') {
                    throw "Interop 用例集未通过：total=$($interopResult.total) passed=$($interopResult.passed) failed=$($interopResult.failed) skipped=$($interopResult.skipped) notRun=$($interopResult.notRun)。"
                }
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $interopEvidence -PathType Leaf)) {
                throw "D18 互通未产出证据：$interopEvidence"
            }
            $interopReport = Get-Content -LiteralPath $interopEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 50
            if ($interopReport.result -ne 'pass' -or @($interopReport.failedGateIds).Count -gt 0) {
                throw "D18 互通门禁未全部通过：failed=$(@($interopReport.failedGateIds) -join ', ')"
            }
            # 夹具侧副本：down.sh 会删除夹具目录，因此按 D12/D13 判据脚本同款顺序在收尾后补写。
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $fixtureInteropEvidence) -Force
            Copy-Item -LiteralPath $interopEvidence -Destination $fixtureInteropEvidence -Force
        }

        $null = Invoke-DshPortableStep -Id 'plugin-integration-suite' -Description '会话与来源隔离（D20）：双 target / 多会话隔离矩阵、误投递为 0、拒绝零泄漏' -Body {
            # 存在理由（D20 要求）：把"附件到底投进了哪个 target 的哪条会话"变成**机器可读的矩阵**，
            # 并且逐操作给出确定结果：会话切换 / 刷新 / 页面删除 / 隐藏文档 / iframe / 跨来源导航
            # （含 userinfo、后缀主机、IPv6 字面量伪装）/ 旧 epoch 迟到帧 / 重放 / 无会话首页 /
            # 受限 composer / 子代理形态 / 未知协议版本。落点只认**原生 attachmentIds**（由后续一次
            # 导入的 previous 回读），绝不认 DOM 卡片；拒绝路径还要逐条查"没有泄漏本地路径或文件字节"。
            # 两个 target 同主机不同端口且共享同一个 DSH_HOME ⇒ 会话集合相同，归属只能由页面身份与
            # 操作身份决定，不能靠"看不见就投不进去"蒙对。
            $isolationEvidence = Join-Path $ArtifactsDirectory 'd20-isolation-gates.json'
            $isolationMatrix = Join-Path $ArtifactsDirectory 'd20-isolation-matrix.json'
            $isolationScript = Join-Path $pluginRoot 'tests/fixtures/d20-isolation-gates.mjs'
            $originPolicyModule = Join-Path $pluginRoot 'tests/fixtures/d20-origin-policy.mjs'
            foreach ($requiredIsolationPath in @($isolationScript, $originPolicyModule)) {
                if (-not (Test-Path -LiteralPath $requiredIsolationPath -PathType Leaf)) {
                    throw "缺少 D20 隔离判据实现文件：$requiredIsolationPath"
                }
            }
            # 证据必须先删除再生成：陈旧绿灯不得冒充本轮结果。
            foreach ($staleIsolation in @($isolationEvidence, $isolationMatrix)) {
                if (Test-Path -LiteralPath $staleIsolation -PathType Leaf) {
                    Remove-Item -LiteralPath $staleIsolation -Force
                }
            }
            # 判据必须跑在**当前**产物上：先构建、再打包、再重建夹具（含第二个并存 target 的 profile）。
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/down.sh')
            ) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @(
                (Join-Path $pluginRoot 'tests/fixtures/setup.sh')
            ) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($isolationScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @(
                    (Join-Path $pluginRoot 'tests/fixtures/down.sh')
                ) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $isolationEvidence -PathType Leaf)) {
                throw "D20 隔离判据未产出证据：$isolationEvidence"
            }
            if (-not (Test-Path -LiteralPath $isolationMatrix -PathType Leaf)) {
                throw "D20 隔离判据未产出隔离矩阵：$isolationMatrix"
            }
            $isolationReport = Get-Content -LiteralPath $isolationEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            if ($isolationReport.task -ne 'D20' -or $isolationReport.purpose -ne 'session-origin-isolation') {
                throw "D20 证据身份不符：task=$($isolationReport.task) purpose=$($isolationReport.purpose)"
            }
            if ($isolationReport.result -ne 'pass' -or @($isolationReport.failedGateIds).Count -gt 0) {
                throw "D20 隔离判据未全部通过：failed=$(@($isolationReport.failedGateIds) -join ', ')"
            }
            # "零误投递"必须是在**扫描完整**的前提下得出的；扫描不完整时报告会给出 null，这里一律拒绝。
            if ($isolationReport.matrix.sweepComplete -ne $true) {
                throw "D20 隔离矩阵扫描不完整：sweepFailures=$(@($isolationReport.matrix.sweepFailures | ForEach-Object { $_.cellId }) -join ', ')"
            }
            if ($null -eq $isolationReport.matrix.misDeliveryTotal -or [int] $isolationReport.matrix.misDeliveryTotal -ne 0) {
                throw "D20 误投递总数不为 0：$($isolationReport.matrix.misDeliveryTotal)"
            }
            $matrixDocument = Get-Content -LiteralPath $isolationMatrix -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            if ($matrixDocument.verdict.zeroMisDelivery -ne $true) {
                throw 'D20 隔离矩阵 verdict.zeroMisDelivery 不为 true'
            }
            $matrixRowCount = @($matrixDocument.rows).Count
            if ($matrixRowCount -lt 48) {
                throw "D20 隔离矩阵行数不足（12 操作 × 4 落点单元）：$matrixRowCount"
            }
            if (@($matrixDocument.rows | Where-Object { $_.determinate -ne $true }).Count -gt 0) {
                throw 'D20 隔离矩阵存在"结果不确定"的行'
            }
            # 每次运行独立 runId 目录：历史不得被覆写，因此必须能在 runId 目录里找到本轮原始报告。
            $runIsolationReport = Join-Path $repositoryRoot ("artifacts/verify-portable/d20-runs/{0}/report.json" -f $isolationReport.runId)
            $runIsolationMatrix = Join-Path $repositoryRoot ("artifacts/verify-portable/d20-runs/{0}/isolation-matrix.json" -f $isolationReport.runId)
            foreach ($requiredRunArtifact in @($runIsolationReport, $runIsolationMatrix)) {
                if (-not (Test-Path -LiteralPath $requiredRunArtifact -PathType Leaf)) {
                    throw "D20 缺少 runId 目录内的原始证据：$requiredRunArtifact"
                }
            }
            $isolationIndex = Join-Path $repositoryRoot 'artifacts/verify-portable/d20-runs/index.json'
            if (-not (Test-Path -LiteralPath $isolationIndex -PathType Leaf)) {
                throw "D20 缺少运行索引：$isolationIndex"
            }
            # 未验项必须显式列出来（例如子代理专属形态），不得静默当成通过。
            Write-Host ("D20 未验项：" + ((@($isolationReport.notRun | ForEach-Object { $_.id }) -join ', ')))
            Write-Host ("D20 矩阵：runId=$($isolationReport.runId) 行=$matrixRowCount 误投递=$($isolationReport.matrix.misDeliveryTotal) 单元格=$(@($matrixDocument.sweep.counts.PSObject.Properties).Count)")
            # 夹具侧副本：down.sh 会删除夹具目录，因此按 D12/D13/D18 同款顺序在收尾后补写。
            $fixtureIsolationEvidence = Join-Path $repositoryRoot 'artifacts/fixture/evidence/d20-isolation-gates.json'
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $fixtureIsolationEvidence) -Force
            Copy-Item -LiteralPath $isolationEvidence -Destination $fixtureIsolationEvidence -Force
        }

        $null = Invoke-DshPortableStep -Id 'final-tarball-install' -Description '交付包干净安装（D22）：新建隔离 profile + 固定全家桶 + 候选 tarball；跑着的 host/client 必须就是包内字节，代表性闭环真实跑通' -Body {
            # 接入理由（D22 要求：证明"交付包本身"能跑，而不是 source link 或上一轮产物在跑）：
            # 本检查在夹具私有 DSH_HOME 里**新建一个此前不存在的 profile**（干净槽位），只装固定的
            # @linxin666/dsh-web-all 与候选 tarball；然后断言
            #   1) 包内 lib/** 与当前构建树逐文件相同（陈旧 pack/*.tgz 会被抓出）；
            #   2) profile 内实际文件与包内逐字节相同、lockfile 记录到候选包的 integrity/tarball、
            #      不存在 link:/符号链接等 source link 形态；
            #   3) 服务返回的 HTML 逐字含已安装 host 模块现场生成的预启动承载脚本；合并 client bundle
            #      里本包段落逐字节等于包内 lib/client.js（**跑着的就是本轮 tarball**）；
            #   4) remote 行恰好一次、运行期只有一个本 profile 的 dsh 进程（无额外 remote 实例）；
            #   5) 干净 profile 里的代表性闭环：分块传输 → 真 File → 生产草稿导入 → 原生 attachment id
            #      独立回读 → 真实上传 receipt → 真实发送 → 真实 read 工具逐字节读回；
            #   6) 包卫生（与 D03 test:pack 同一条扫描）+ manifest 脱敏，各自带 canary。
            # 边界诚实标注：Windows 正式打包/安装（MSI/zip、真实 WebView2 承载）属 WindowsPending，
            # 本检查只在 Linux 上给出等价承载的证据，报告里显式列为 notRun。
            $tarballEvidence = Join-Path $ArtifactsDirectory 'd22-tarball-gates.json'
            $tarballManifest = Join-Path $ArtifactsDirectory 'd22-package-manifest.json'
            $fixtureTarballEvidence = Join-Path $repositoryRoot 'artifacts/fixture/evidence/d22-tarball-gates.json'
            $fixtureTarballManifest = Join-Path $repositoryRoot 'artifacts/fixture/evidence/d22-package-manifest.json'
            $tarballScript = Join-Path $pluginRoot 'tests/fixtures/d22-tarball-gates.mjs'
            $tarballInstallScript = Join-Path $pluginRoot 'tests/fixtures/d22-clean-install.sh'
            $tarballPackScan = Join-Path $pluginRoot 'scripts/pack-scan.mjs'
            foreach ($requiredTarballPath in @($tarballScript, $tarballInstallScript, $tarballPackScan)) {
                if (-not (Test-Path -LiteralPath $requiredTarballPath -PathType Leaf)) {
                    throw "缺少 D22 交付包判据实现文件：$requiredTarballPath"
                }
            }
            # 陈旧绿灯不得冒充本轮结果。
            foreach ($staleTarballEvidence in @($tarballEvidence, $tarballManifest)) {
                if (Test-Path -LiteralPath $staleTarballEvidence -PathType Leaf) {
                    Remove-Item -LiteralPath $staleTarballEvidence -Force
                }
            }
            # 候选包必须是**当前**构建打出来的；夹具必须是干净的（D22 会新建自己的 profile）。
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/setup.sh')) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($tarballScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            }
            if (-not (Test-Path -LiteralPath $tarballEvidence -PathType Leaf)) {
                throw "D22 交付包判据未产出证据：$tarballEvidence"
            }
            if (-not (Test-Path -LiteralPath $tarballManifest -PathType Leaf)) {
                throw "D22 交付包判据未产出 manifest：$tarballManifest"
            }
            $tarballReport = Get-Content -LiteralPath $tarballEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            if ($tarballReport.task -ne 'D22' -or $tarballReport.purpose -ne 'final-tarball-install') {
                throw "D22 证据身份不符：task=$($tarballReport.task) purpose=$($tarballReport.purpose)"
            }
            if ($tarballReport.result -ne 'pass' -or @($tarballReport.failedGateIds).Count -gt 0) {
                throw "D22 判据未全部通过：failed=$(@($tarballReport.failedGateIds) -join ', ')"
            }
            if (@($tarballReport.failedNegativeIds).Count -gt 0) {
                throw "D22 负向探针未全部成立：failed=$(@($tarballReport.failedNegativeIds) -join ', ')"
            }
            # 候选身份必须落在证据里（没有 sha256 的证据无法回答"跑的是哪个包"）。
            if ([string]::IsNullOrWhiteSpace([string] $tarballReport.candidate.sha256) -or
                [string]::IsNullOrWhiteSpace([string] $tarballReport.candidate.integrity)) {
                throw 'D22 证据缺少候选包的 sha256/integrity'
            }
            if (@($tarballReport.gates).Count -lt 20) {
                throw "D22 判据条数不足（应覆盖身份/干净环境/remote/运行时身份/闭环/卫生/manifest）：$(@($tarballReport.gates).Count)"
            }
            # 每次运行独立 runId 目录：历史不得被覆写，因此必须能在 runId 目录里找到本轮原始报告。
            $runTarballReport = Join-Path $repositoryRoot ("artifacts/verify-portable/d22-runs/{0}/report.json" -f $tarballReport.runId)
            $runTarballManifest = Join-Path $repositoryRoot ("artifacts/verify-portable/d22-runs/{0}/d22-package-manifest.json" -f $tarballReport.runId)
            foreach ($requiredRunArtifact in @($runTarballReport, $runTarballManifest)) {
                if (-not (Test-Path -LiteralPath $requiredRunArtifact -PathType Leaf)) {
                    throw "D22 缺少 runId 目录内的原始证据：$requiredRunArtifact"
                }
            }
            if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'artifacts/verify-portable/d22-runs/index.json') -PathType Leaf)) {
                throw 'D22 缺少运行索引：artifacts/verify-portable/d22-runs/index.json'
            }
            # manifest 内容：sha256 / lockfile 记录的 integrity / 文件清单 / 声明入口 / 实际安装版本 / 插件版本。
            $tarballManifestJson = Get-Content -LiteralPath $tarballManifest -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            if ($tarballManifestJson.candidate.sha256 -ne $tarballReport.candidate.sha256) {
                throw "D22 manifest 的 sha256 与证据不一致：$($tarballManifestJson.candidate.sha256) vs $($tarballReport.candidate.sha256)"
            }
            if ([string]::IsNullOrWhiteSpace([string] $tarballManifestJson.integrityRecordedInProfile.integrity)) {
                throw 'D22 manifest 缺少 profile lockfile 记录的 integrity'
            }
            if ($tarballManifestJson.integrityRecordedInProfile.matchesCandidate -ne $true) {
                throw 'D22 manifest 记录到安装 profile 的 integrity 与候选包不一致'
            }
            if (@($tarballManifestJson.files).Count -lt 10) {
                throw "D22 manifest 文件清单条目不足：$(@($tarballManifestJson.files).Count)"
            }
            if (@($tarballManifestJson.declaredEntryPoints.main).Count -eq 0 -or
                [string]::IsNullOrWhiteSpace([string] $tarballManifestJson.declaredEntryPoints.exports.'./client'.default)) {
                throw 'D22 manifest 缺少声明的 host/client 入口'
            }
            if ([string]::IsNullOrWhiteSpace([string] $tarballManifestJson.candidate.pluginVersion)) {
                throw 'D22 manifest 缺少插件版本'
            }
            # 脱敏是硬要求：manifest 与证据落盘后必须零泄漏（pair=/cookie/token/设备 ID/绝对家目录路径）。
            foreach ($tarballText in @(
                    (Get-Content -LiteralPath $tarballManifest -Raw -Encoding UTF8),
                    (Get-Content -LiteralPath $tarballEvidence -Raw -Encoding UTF8))) {
                $tarballLeaks = @()
                foreach ($tarballLeakPattern in @(
                        'pair=[A-Za-z0-9_-]{8,}',
                        'dsh_pair=[A-Za-z0-9_-]{8,}',
                        '"token"\s*:\s*"[A-Za-z0-9_-]{8,}"',
                        '"deviceId"\s*:\s*"[A-Za-z0-9_-]{8,}"',
                        '/(home|Users)/[A-Za-z0-9._-]+/'
                    )) {
                    if ($tarballText -match $tarballLeakPattern) { $tarballLeaks += $tarballLeakPattern }
                }
                if ($tarballLeaks.Count -gt 0) {
                    throw "D22 证据仍含敏感形态：$($tarballLeaks -join '; ')"
                }
            }
            Write-Host ("D22 候选：sha256=$($tarballReport.candidate.sha256.Substring(0, 16))… 文件=$($tarballReport.candidate.fileCount) 插件版本=$($tarballReport.candidate.pluginVersion)")
            Write-Host ("D22 runId=$($tarballReport.runId) 判据=$(@($tarballReport.gates).Count) 未验=$((@($tarballReport.notRun | ForEach-Object { $_.id })) -join ', ')")
            # 夹具侧副本：down.sh 会删除夹具目录，因此按 D12/D13/D18/D20 同款顺序在收尾后补写。
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $fixtureTarballEvidence) -Force
            Copy-Item -LiteralPath $tarballEvidence -Destination $fixtureTarballEvidence -Force
            Copy-Item -LiteralPath $tarballManifest -Destination $fixtureTarballManifest -Force
        }

        $null = Invoke-DshPortableStep -Id 'addon-compatibility-fixture' -Description '附加插件兼容/停用/更新后状态（D23）：独立停用只动自己的行、不得谎报 runtime 恢复（必要时报 ReloadRequired）、重载/重启后配对与附件通路照常、四类组合的能力结论与原因码可核对' -Body {
            # 接入理由（方案 portable-validation-plan.md：D03–D23 各任务完成时同步注册该任务的检查与证据；
            # D22–D23 对应 L10 最终包安装 + L06 兼容/停用/更新后的行为）：
            # 本检查在真实夹具（私有 DSH_HOME + 固定全家桶 + 真实 Chromium）上回答四件事：
            #   1) 运行时停用/F5 重载/Harness 重启之后，原配对与原附件行为是否仍然正确；
            #   2) 停用**不调用** PairingService.stop、不丢配对设备（夹具私有插桩 + 负向控制会真的触发一次）；
            #   3) 只恢复全局变量**不得**被当作 runtime 恢复——状态面必须报 ReloadRequired（判据双向：重载后才翻正）；
            #   4) 未知 hook / remote 降级 / 不支持组合 / 独立停用四类组合的能力结论与原因码，落成机器可读矩阵。
            # 边界诚实标注：真实 npm 源的 all 更新、未验证上游组合（0.3.21/0.3.22 + 0.1.5-rc.2）与 Windows 实机
            # 安装/更新属 notRun（不执行任何对生产 $HOME/.dsh 的更新，只从 lock/manifest 推理）。
            $compatEvidence = Join-Path $ArtifactsDirectory 'd23-compat-gates.json'
            $compatMatrix = Join-Path $ArtifactsDirectory 'd23-compatibility-matrix.json'
            $fixtureCompatEvidence = Join-Path $repositoryRoot 'artifacts/fixture/evidence/d23-compat-gates.json'
            $fixtureCompatMatrix = Join-Path $repositoryRoot 'artifacts/fixture/evidence/d23-compatibility-matrix.json'
            $compatScript = Join-Path $pluginRoot 'tests/fixtures/d23-compat-gates.mjs'
            $compatPolicyDoc = Join-Path $pluginRoot 'docs/COMPATIBILITY.md'
            foreach ($requiredCompatPath in @($compatScript, $compatPolicyDoc)) {
                if (-not (Test-Path -LiteralPath $requiredCompatPath -PathType Leaf)) {
                    throw "缺少 D23 兼容/停用判据实现文件：$requiredCompatPath"
                }
            }
            # 陈旧绿灯不得冒充本轮结果。
            foreach ($staleCompatEvidence in @($compatEvidence, $compatMatrix)) {
                if (Test-Path -LiteralPath $staleCompatEvidence -PathType Leaf) {
                    Remove-Item -LiteralPath $staleCompatEvidence -Force
                }
            }
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'build') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath $pnpm -Arguments @('--dir', $pluginRoot, 'run', 'test:pack') -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/setup.sh')) -WorkingDirectory $repositoryRoot
            try {
                Invoke-DshNative -FilePath $node -Arguments @($compatScript) -WorkingDirectory $repositoryRoot
            }
            finally {
                Invoke-DshNative -FilePath 'bash' -Arguments @((Join-Path $pluginRoot 'tests/fixtures/down.sh')) -WorkingDirectory $repositoryRoot
            }
            foreach ($requiredCompatArtifact in @($compatEvidence, $compatMatrix)) {
                if (-not (Test-Path -LiteralPath $requiredCompatArtifact -PathType Leaf)) {
                    throw "D23 兼容/停用判据未产出证据：$requiredCompatArtifact"
                }
            }
            $compatReport = Get-Content -LiteralPath $compatEvidence -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            if ($compatReport.task -ne 'D23' -or $compatReport.purpose -ne 'compatibility-and-disable') {
                throw "D23 证据身份不符：task=$($compatReport.task) purpose=$($compatReport.purpose)"
            }
            if ($compatReport.result -ne 'pass' -or @($compatReport.failedGateIds).Count -gt 0) {
                throw "D23 判据未全部通过：failed=$(@($compatReport.failedGateIds) -join ', ')"
            }
            if (@($compatReport.failedNegativeIds).Count -gt 0) {
                throw "D23 负向探针未全部成立：failed=$(@($compatReport.failedNegativeIds) -join ', ')"
            }
            if (@($compatReport.gates).Count -lt 12) {
                throw "D23 判据条数不足（应覆盖清单/能力/通路/停用/恢复判定/重载/重启/四类组合/范围/产物）：$(@($compatReport.gates).Count)"
            }
            if (@($compatReport.negativeProbes).Count -lt 8) {
                throw "D23 负向探针条数不足：$(@($compatReport.negativeProbes).Count)"
            }
            # 关键结论必须逐条落在证据里，不能只靠一个总 pass。
            $compatGateById = @{}
            foreach ($compatGate in @($compatReport.gates)) { $compatGateById[[string] $compatGate.id] = $compatGate }
            foreach ($requiredCompatGate in @(
                    'G06-no-false-recovery-claim',
                    'G07b-genuine-page-reload-on-loopback',
                    'G11-independent-disable-scope-and-builtin-fallback',
                    'G12b-remote-degraded-disables-without-fallback',
                    'G13b-pairing-trend-monotonic')) {
                if (-not $compatGateById.ContainsKey($requiredCompatGate)) {
                    throw "D23 证据缺少关键判据：$requiredCompatGate"
                }
                if ($compatGateById[$requiredCompatGate].ok -ne $true) {
                    throw "D23 关键判据未通过：$requiredCompatGate"
                }
            }
            # 每次运行独立 runId 目录：历史不得被覆写。
            $runCompatReport = Join-Path $repositoryRoot ("artifacts/verify-portable/d23-runs/{0}/report.json" -f $compatReport.runId)
            $runCompatMatrix = Join-Path $repositoryRoot ("artifacts/verify-portable/d23-runs/{0}/d23-compatibility-matrix.json" -f $compatReport.runId)
            foreach ($requiredRunCompat in @($runCompatReport, $runCompatMatrix)) {
                if (-not (Test-Path -LiteralPath $requiredRunCompat -PathType Leaf)) {
                    throw "D23 缺少 runId 目录内的原始证据：$requiredRunCompat"
                }
            }
            if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'artifacts/verify-portable/d23-runs/index.json') -PathType Leaf)) {
                throw 'D23 缺少运行索引：artifacts/verify-portable/d23-runs/index.json'
            }
            # 兼容矩阵：五行组合齐备、原因码确定、非 verified 行配对/心跳均不受影响。
            $compatMatrixJson = Get-Content -LiteralPath $compatMatrix -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            if ($compatMatrixJson.task -ne 'D23' -or $compatMatrixJson.purpose -ne 'compatibility-matrix') {
                throw "D23 兼容矩阵身份不符：task=$($compatMatrixJson.task) purpose=$($compatMatrixJson.purpose)"
            }
            $compatRows = @($compatMatrixJson.rows)
            if ($compatRows.Count -lt 5) {
                throw "D23 兼容矩阵行数不足：$($compatRows.Count)"
            }
            foreach ($expectedRow in @('verified', 'unknown-hook', 'remote-degraded', 'unsupported-version', 'addon-disabled')) {
                if (@($compatRows | Where-Object { $_.combination -eq $expectedRow }).Count -ne 1) {
                    throw "D23 兼容矩阵缺少组合行：$expectedRow"
                }
            }
            $compatVerified = @($compatRows | Where-Object { $_.combination -eq 'verified' })[0]
            if ($compatVerified.status -ne 'available') {
                throw "D23 已验证组合的能力状态不是 available：$($compatVerified.status)"
            }
            $compatConflict = @($compatRows | Where-Object { $_.combination -eq 'unknown-hook' })[0]
            if ($compatConflict.code -ne 'capability-conflict') {
                throw "D23 未知 hook 组合的原因码不是 capability-conflict：$($compatConflict.code)"
            }
            foreach ($degradedRow in @($compatRows | Where-Object { $_.combination -in @('remote-degraded', 'unsupported-version', 'addon-disabled') })) {
                if ($degradedRow.code -ne 'capability-disabled') {
                    throw "D23 组合 $($degradedRow.combination) 的原因码不是 capability-disabled：$($degradedRow.code)"
                }
                if ($degradedRow.pairingUnaffected -ne $true -or $degradedRow.heartbeatUnaffected -ne $true) {
                    throw "D23 组合 $($degradedRow.combination) 未证明配对/心跳不受影响"
                }
            }
            if ([string]::IsNullOrWhiteSpace([string] $compatMatrixJson.pinned.harness) -or
                [string]::IsNullOrWhiteSpace([string] $compatMatrixJson.observed.all)) {
                throw 'D23 兼容矩阵缺少固定/实测版本记录'
            }
            # 未验项必须是显式 notRun（WindowsPending 与未执行的上游更新都在这里）。
            $compatNotRunIds = @($compatReport.notRun | ForEach-Object { [string] $_.id })
            foreach ($requiredNotRun in @('WindowsPackagingAndUpdate', 'RealAllBundleUpdate')) {
                if ($compatNotRunIds -notcontains $requiredNotRun) {
                    throw "D23 未验项缺少显式登记：$requiredNotRun"
                }
            }
            # 脱敏是硬要求：证据与矩阵落盘后必须零泄漏（pair=/cookie/设备 ID/绝对家目录路径）。
            foreach ($compatText in @(
                    (Get-Content -LiteralPath $compatEvidence -Raw -Encoding UTF8),
                    (Get-Content -LiteralPath $compatMatrix -Raw -Encoding UTF8))) {
                $compatLeaks = @()
                foreach ($compatLeakPattern in @(
                        'pair=[A-Za-z0-9_-]{8,}',
                        'dsh_pair=[A-Za-z0-9_-]{8,}',
                        '"token"\s*:\s*"[A-Za-z0-9_-]{8,}"',
                        '"deviceId"\s*:\s*"[A-Za-z0-9_-]{8,}"',
                        '/(home|Users)/[A-Za-z0-9._-]+/'
                    )) {
                    if ($compatText -match $compatLeakPattern) { $compatLeaks += $compatLeakPattern }
                }
                if ($compatLeaks.Count -gt 0) {
                    throw "D23 证据仍含敏感形态：$($compatLeaks -join '; ')"
                }
            }
            Write-Host ("D23 固定版本：harness=$($compatMatrixJson.pinned.harness) all=$($compatMatrixJson.pinned.all) remote=$($compatMatrixJson.pinned.remote) 插件=$($compatMatrixJson.pinned.addon)")
            Write-Host ("D23 runId=$($compatReport.runId) 判据=$(@($compatReport.gates).Count) 负向=$(@($compatReport.negativeProbes).Count) 未验=$($compatNotRunIds -join ', ')")
            # 夹具侧副本：down.sh 会删除夹具目录，因此按 D12/D13/D18/D20/D22 同款顺序在收尾后补写。
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $fixtureCompatEvidence) -Force
            Copy-Item -LiteralPath $compatEvidence -Destination $fixtureCompatEvidence -Force
            Copy-Item -LiteralPath $compatMatrix -Destination $fixtureCompatMatrix -Force
        }

        # ---- D24：收敛完整 Linux 开发验证（覆盖表 + 零容忍 + 独立性 + 候选一致性） ----
        Write-Host ''
        Write-Host '== D24：完整 Linux 开发验证收敛 =='
        $null = Invoke-DshPortableStep -Id 'd24-linux-convergence' -Description 'L01–L11+L13 覆盖表↔检查↔证据、零容忍（零用例/跳过/未验）、portable↔crossBuild 独立性与候选一致性、正式摘要' -Body {
            $d24Config = $config.d24Convergence
            if ($null -eq $d24Config) { throw 'eng/verification-profiles.json 缺少 d24Convergence 配置' }

            # ---- 本轮用例计数（与结尾摘要同一算法；此处用于零容忍判定） ----
            $d24Executed = 0
            $d24Failed = 0
            $d24Skipped = 0
            foreach ($result in $testResults) {
                $d24Executed += [int] $result.total
                $d24Failed += [int] $result.failed
                $d24Skipped += [int] $result.skipped + [int] $result.notRun
            }
            $d24Required = 0
            foreach ($assemblyName in @($config.requiredTestAssembliesOnLinux)) {
                if (-not $expectedDataset.Contains($assemblyName)) { continue }
                foreach ($setName in @($expectedDataset[$assemblyName].sets.Keys)) {
                    if ($setName -eq 'interop' -and $Profile -ne 'Development') { continue }
                    $d24Required += @($expectedDataset[$assemblyName].sets[$setName].hashes).Count
                }
            }

            # ---- 候选身份（权威来源：git + 插件 manifest + src 树哈希，与夹具同一算法） ----
            $gitCommand = Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $gitCommand) { throw 'D24 需要 git 计算候选身份' }
            $gitHead = (Invoke-DshNativeCapture -FilePath $gitCommand.Source -Arguments @('-C', $repositoryRoot, 'rev-parse', 'HEAD') -WorkingDirectory $repositoryRoot).Trim().ToLowerInvariant()
            $gitBranch = (Invoke-DshNativeCapture -FilePath $gitCommand.Source -Arguments @('-C', $repositoryRoot, 'rev-parse', '--abbrev-ref', 'HEAD') -WorkingDirectory $repositoryRoot).Trim()
            $porcelainRaw = (Invoke-DshNativeCapture -FilePath $gitCommand.Source -Arguments @('-C', $repositoryRoot, 'status', '--porcelain=v1', '--untracked-files=normal') -WorkingDirectory $repositoryRoot).Trim()
            $porcelainLines = @()
            if (-not [string]::IsNullOrWhiteSpace($porcelainRaw)) {
                $porcelainLines = @($porcelainRaw -split "`n" | Where-Object { $_ -ne '' })
            }
            $porcelainSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($porcelainRaw))).ToLowerInvariant()
            $manifestJson = Get-Content -LiteralPath (Join-Path $pluginRoot 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 30
            $srcTree = Get-DshD24SourceTreeHash -PluginRoot $pluginRoot -RelativeRoot 'src'
            $candidateBlock = [ordered]@{
                gitHead                 = $gitHead
                gitBranch               = $gitBranch
                worktreeState           = if ($porcelainLines.Count -eq 0) { 'clean' } else { 'dirty' }
                worktreeDirtyCount      = $porcelainLines.Count
                worktreePorcelainSha256 = $porcelainSha
                pluginName              = [string] $manifestJson.name
                pluginVersion           = [string] $manifestJson.version
                pluginSourceTree        = $srcTree
                platform                = 'linux'
            }
            $sourceState = Get-DshSourceState -RepositoryRoot $repositoryRoot
            $identityViolations = [System.Collections.Generic.List[string]]::new()
            if ($sourceState.Commit -ne $gitHead) {
                $identityViolations.Add("摘要 commit 与 git rev-parse HEAD 不一致：$($sourceState.Commit) ≠ $gitHead")
            }

            # 已声明 candidate 身份的证据必须逐字段与本轮权威身份一致。
            $identityAgreement = [System.Collections.Generic.List[object]]::new()
            $declaredEvidence = [System.Collections.Generic.List[object]]::new()
            foreach ($relative in @($d24Config.identityEvidence | ForEach-Object { [string] $_ })) {
                $full = Join-Path $repositoryRoot $relative
                if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
                    $identityViolations.Add("身份证据缺失：$relative")
                    continue
                }
                $document = Get-Content -LiteralPath $full -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 80
                if ($document.PSObject.Properties.Name -notcontains 'candidate') {
                    $identityViolations.Add("证据 $relative 声明为身份证据但缺少 candidate 块")
                    continue
                }
                $violationsBefore = $identityViolations.Count
                $candidate = $document.candidate
                $fields = [ordered]@{}
                if ($candidate.PSObject.Properties.Name -contains 'gitHead' -and -not [string]::IsNullOrWhiteSpace([string] $candidate.gitHead)) {
                    $fields.gitHead = [string] $candidate.gitHead
                    if ([string] $candidate.gitHead -ne $gitHead) { $identityViolations.Add("$relative candidate.gitHead=$($candidate.gitHead) ≠ $gitHead") }
                }
                if ($candidate.PSObject.Properties.Name -contains 'gitBranch' -and -not [string]::IsNullOrWhiteSpace([string] $candidate.gitBranch)) {
                    $fields.gitBranch = [string] $candidate.gitBranch
                    if ([string] $candidate.gitBranch -ne $gitBranch) { $identityViolations.Add("$relative candidate.gitBranch=$($candidate.gitBranch) ≠ $gitBranch") }
                }
                if ($candidate.PSObject.Properties.Name -contains 'pluginVersion' -and -not [string]::IsNullOrWhiteSpace([string] $candidate.pluginVersion)) {
                    $fields.pluginVersion = [string] $candidate.pluginVersion
                    if ([string] $candidate.pluginVersion -ne [string] $manifestJson.version) { $identityViolations.Add("$relative candidate.pluginVersion=$($candidate.pluginVersion) ≠ $($manifestJson.version)") }
                }
                if ($candidate.PSObject.Properties.Name -contains 'gitWorktree' -and $null -ne $candidate.gitWorktree) {
                    $dirtyCount = [int] $candidate.gitWorktree.dirtyEntries
                    $fields.worktreeDirtyCount = $dirtyCount
                    if ($dirtyCount -ne $porcelainLines.Count) { $identityViolations.Add("$relative 工作树脏计数 $dirtyCount ≠ 本轮 $($porcelainLines.Count)") }
                }
                if ($candidate.PSObject.Properties.Name -contains 'pluginSourceTree' -and $null -ne $candidate.pluginSourceTree -and $null -ne $srcTree) {
                    $fields.pluginSourceTreeSha256 = [string] $candidate.pluginSourceTree.sha256
                    if ([string] $candidate.pluginSourceTree.sha256 -ne [string] $srcTree.sha256) {
                        $identityViolations.Add("$relative 源码树哈希 $($candidate.pluginSourceTree.sha256) ≠ 本轮 $($srcTree.sha256)")
                    }
                }
                $declaredEvidence.Add([pscustomobject]@{ path = $relative; declaredFields = @($fields.Keys) })
                $identityAgreement.Add([ordered]@{
                        path   = $relative
                        fields = $fields
                        agrees = ($identityViolations.Count -eq $violationsBefore)
                    })
            }
            # 两个 D22 证据的 tarball 摘要必须互相一致（同一候选包）。
            $manifestEvidencePath = Join-Path $repositoryRoot 'artifacts/verify-portable/d22-package-manifest.json'
            $tarballEvidencePath = Join-Path $repositoryRoot 'artifacts/verify-portable/d22-tarball-gates.json'
            if ((Test-Path -LiteralPath $manifestEvidencePath -PathType Leaf) -and (Test-Path -LiteralPath $tarballEvidencePath -PathType Leaf)) {
                $manifestCandidate = (Get-Content -LiteralPath $manifestEvidencePath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60).candidate
                $tarballCandidate = (Get-Content -LiteralPath $tarballEvidencePath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60).candidate
                if ([string] $manifestCandidate.sha256 -ne [string] $tarballCandidate.sha256) {
                    $identityViolations.Add("D22 manifest 与 gates 的 tarball sha256 不一致")
                }
            }

            # ---- L01–L11、L13 覆盖表（L12 单独判定） ----
            $layerViews = @(Get-DshD24LayerViews -LayerConfig @($d24Config.layers) -Checks @($checks) -RepositoryRoot $repositoryRoot -RunStartedAtUtc $startedAtUtc)
            $coverageGaps = [System.Collections.Generic.List[string]]::new()
            foreach ($view in $layerViews) {
                $separatelyJudged = $d24Config.separatelyJudgedLayers -contains [string] $view.layer
                if (-not $separatelyJudged) {
                    foreach ($checkStatus in @($view.checks)) {
                        if ($checkStatus.status -eq 'missing') {
                            $coverageGaps.Add("$($view.layer) 的检查 $($checkStatus.id) 未在本轮 Development profile 中登记/执行")
                        }
                        elseif ($checkStatus.status -ne 'pass') {
                            $coverageGaps.Add("$($view.layer) 的检查 $($checkStatus.id) 状态为 $($checkStatus.status)")
                        }
                    }
                }
                foreach ($evidence in @($view.evidence)) {
                    if (-not $evidence.exists) {
                        $coverageGaps.Add("$($view.layer) 的证据缺失：$($evidence.path)")
                    }
                    elseif ($evidence.sameRunFresh -eq $false) {
                        $coverageGaps.Add("$($view.layer) 的证据非本轮生成（疑似旧候选残留）：$($evidence.path)")
                    }
                }
            }

            # ---- notRun 清单：默认拒绝，只有 WindowsPending/显式白名单可接受 ----
            $evidenceNotRunLists = [System.Collections.Generic.List[object]]::new()
            foreach ($relative in @(
                    'artifacts/verify-portable/d19-failure-gates.json',
                    'artifacts/verify-portable/d20-isolation-gates.json',
                    'artifacts/verify-portable/d21-resource-report.json',
                    'artifacts/verify-portable/d22-tarball-gates.json',
                    'artifacts/verify-portable/d23-compat-gates.json')) {
                $full = Join-Path $repositoryRoot $relative
                if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
                $document = Get-Content -LiteralPath $full -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 80
                if (($document.PSObject.Properties.Name -contains 'notRun') -and @($document.notRun).Count -gt 0) {
                    $evidenceNotRunLists.Add([pscustomobject]@{ source = $relative; items = @($document.notRun) })
                }
            }
            $notRunInventory = @(Get-DshD24NotRunInventory -Config $config -EvidenceNotRunLists @($evidenceNotRunLists) -AcceptedScopeLimited @($d24Config.acceptedScopeLimitedNotRun))

            # ---- 零容忍（真实输入） ----
            $zeroToleranceViolations = @(Get-DshD24ZeroToleranceViolations `
                    -Layers $layerViews `
                    -TestResults @($testResults) `
                    -NotRunInventory $notRunInventory `
                    -RequiredCases $d24Required `
                    -ExecutedCases $d24Executed `
                    -FailedCases $d24Failed `
                    -SkippedCases $d24Skipped `
                    -SeparatelyJudgedLayers @($d24Config.separatelyJudgedLayers))

            # ---- 零容忍负向控制：证明零用例/跳过/未验/假 pass 确实会失败 ----
            $zeroToleranceControls = [System.Collections.Generic.List[object]]::new()
            $cleanProbe = @(Get-DshD24ZeroToleranceViolations `
                    -Layers @((New-DshD24ProbeLayer -Name 'L02' -Status 'pass' -CaseCount 7)) `
                    -TestResults @([pscustomobject]@{ assembly = 'Probe'; caseSet = 'core'; total = 7; failed = 0; skipped = 0; notRun = 0 }) `
                    -NotRunInventory @() -RequiredCases 7 -ExecutedCases 7 -FailedCases 0 -SkippedCases 0)
            $zeroToleranceControls.Add([ordered]@{ id = 'C0-clean-input'; mutation = '干净输入（对照，防止误报）'; expected = '0 违规'; actual = @($cleanProbe).Count; ok = (@($cleanProbe).Count -eq 0) })
            $zeroCaseProbe = @(Get-DshD24ZeroToleranceViolations `
                    -Layers @((New-DshD24ProbeLayer -Name 'L02' -Status 'pass' -CaseCount 0)) `
                    -TestResults @() -NotRunInventory @() -RequiredCases 7 -ExecutedCases 7)
            $zeroToleranceControls.Add([ordered]@{ id = 'C1-zero-cases'; mutation = 'L02 用例证据计数改成 0'; expected = '≥1 违规'; actual = @($zeroCaseProbe).Count; ok = (@($zeroCaseProbe).Count -ge 1) })
            $skipProbe = @(Get-DshD24ZeroToleranceViolations `
                    -Layers @((New-DshD24ProbeLayer -Name 'L02' -Status 'pass' -CaseCount 7)) `
                    -TestResults @([pscustomobject]@{ assembly = 'Probe'; caseSet = 'core'; total = 7; failed = 0; skipped = 1; notRun = 0 }) `
                    -NotRunInventory @() -RequiredCases 7 -ExecutedCases 7)
            $zeroToleranceControls.Add([ordered]@{ id = 'C2-skip'; mutation = '某程序集 skipped=1'; expected = '≥1 违规'; actual = @($skipProbe).Count; ok = (@($skipProbe).Count -ge 1) })
            $notRunProbe = @(Get-DshD24ZeroToleranceViolations `
                    -Layers @((New-DshD24ProbeLayer -Name 'L02' -Status 'pass' -CaseCount 7)) `
                    -TestResults @([pscustomobject]@{ assembly = 'Probe'; caseSet = 'interop'; total = 7; failed = 0; skipped = 0; notRun = 1 }) `
                    -NotRunInventory @() -RequiredCases 7 -ExecutedCases 7)
            $zeroToleranceControls.Add([ordered]@{ id = 'C3-not-run'; mutation = '某程序集 notRun=1'; expected = '≥1 违规'; actual = @($notRunProbe).Count; ok = (@($notRunProbe).Count -ge 1) })
            $incompleteProbe = @(Get-DshD24ZeroToleranceViolations `
                    -Layers @((New-DshD24ProbeLayer -Name 'L02' -Status 'incomplete' -CaseCount 7)) `
                    -TestResults @() -NotRunInventory @() -RequiredCases 7 -ExecutedCases 7)
            $zeroToleranceControls.Add([ordered]@{ id = 'C4-incomplete-layer'; mutation = '必需层 L02 状态 incomplete'; expected = '≥1 违规'; actual = @($incompleteProbe).Count; ok = (@($incompleteProbe).Count -ge 1) })
            $unjustifiedProbe = @(Get-DshD24ZeroToleranceViolations `
                    -Layers @((New-DshD24ProbeLayer -Name 'L02' -Status 'pass' -CaseCount 7)) `
                    -TestResults @() `
                    -NotRunInventory @([pscustomobject]@{ id = 'X-unjustified'; source = 'probe'; kind = 'in-check-notRun'; justification = 'unjustified'; detail = '无理由' }) `
                    -RequiredCases 7 -ExecutedCases 7)
            $zeroToleranceControls.Add([ordered]@{ id = 'C5-unjustified-notRun'; mutation = 'notRun 无正当理由'; expected = '≥1 违规'; actual = @($unjustifiedProbe).Count; ok = (@($unjustifiedProbe).Count -ge 1) })
            $justifiedProbe = @(Get-DshD24ZeroToleranceViolations `
                    -Layers @((New-DshD24ProbeLayer -Name 'L02' -Status 'pass' -CaseCount 7)) `
                    -TestResults @() `
                    -NotRunInventory @([pscustomobject]@{ id = 'WP-probe'; source = 'probe'; kind = 'in-check-notRun'; justification = 'WindowsPending'; detail = 'WindowsPending' }) `
                    -RequiredCases 7 -ExecutedCases 7)
            $zeroToleranceControls.Add([ordered]@{ id = 'C6-windows-pending-notRun'; mutation = 'notRun 明确为 WindowsPending'; expected = '0 违规（不误报）'; actual = @($justifiedProbe).Count; ok = (@($justifiedProbe).Count -eq 0) })
            $zeroToleranceControlsFailed = @($zeroToleranceControls | Where-Object { $_.ok -ne $true })

            # ---- 独立性：portableStatus 与 crossBuildStatus 互不可掩 ----
            $portableParams = @((Get-Command -Name Resolve-DshPortableStatus).Parameters.Keys | ForEach-Object { [string] $_ })
            $structuralIndependent = -not ($portableParams -contains 'CrossBuildStatus')
            $baselinePortableFailures = @($checks | Where-Object { [string] $_.status -eq 'fail' -and [string] $_.id -ne 'cross-build-l12' } | ForEach-Object { [string] $_.id })
            $baselineIncomplete = @($checks | Where-Object { [string] $_.status -eq 'incomplete' }).Count
            $baselinePortable = Resolve-DshPortableStatus -PortableFailureIds $baselinePortableFailures -IncompleteCount $baselineIncomplete -RequiredCases $d24Required -ExecutedCases $d24Executed -FailedCases $d24Failed -SkippedCases $d24Skipped
            $baselineCross = Resolve-DshCrossBuildStatus -Checks @($checks)

            $independenceControls = [System.Collections.Generic.List[object]]::new()
            $mutatedCrossChecks = @($checks | ForEach-Object {
                    if ([string] $_.id -eq 'cross-build-l12') { [pscustomobject]@{ id = 'cross-build-l12'; status = 'fail'; detail = 'negative-control' } } else { $_ }
                })
            $ic1Failures = @($mutatedCrossChecks | Where-Object { [string] $_.status -eq 'fail' -and [string] $_.id -ne 'cross-build-l12' } | ForEach-Object { [string] $_.id })
            $ic1Portable = Resolve-DshPortableStatus -PortableFailureIds $ic1Failures -IncompleteCount $baselineIncomplete -RequiredCases $d24Required -ExecutedCases $d24Executed -FailedCases $d24Failed -SkippedCases $d24Skipped
            $ic1Cross = Resolve-DshCrossBuildStatus -Checks $mutatedCrossChecks
            $independenceControls.Add([ordered]@{
                    id = 'I1-crossbuild-fail-only'
                    mutation = '把 cross-build-l12 改成 fail（可移植层不动）'
                    portableStatus = $ic1Portable
                    crossBuildStatus = $ic1Cross
                    expected = "portableStatus 仍为 $baselinePortable；crossBuildStatus 变为 fail"
                    ok = (($ic1Portable -eq $baselinePortable) -and ($ic1Cross -eq 'fail'))
                })

            $mutatedPortableChecks = @($checks | ForEach-Object {
                    if ([string] $_.id -eq 'core-build') { [pscustomobject]@{ id = 'core-build'; status = 'fail'; detail = 'negative-control' } } else { $_ }
                })
            $ic2Failures = @($mutatedPortableChecks | Where-Object { [string] $_.status -eq 'fail' -and [string] $_.id -ne 'cross-build-l12' } | ForEach-Object { [string] $_.id })
            $ic2Portable = Resolve-DshPortableStatus -PortableFailureIds $ic2Failures -IncompleteCount $baselineIncomplete -RequiredCases $d24Required -ExecutedCases $d24Executed -FailedCases $d24Failed -SkippedCases $d24Skipped
            $ic2Cross = Resolve-DshCrossBuildStatus -Checks $mutatedPortableChecks
            $independenceControls.Add([ordered]@{
                    id = 'I2-portable-fail-only'
                    mutation = '把 core-build 改成 fail（cross-build 不动）'
                    portableStatus = $ic2Portable
                    crossBuildStatus = $ic2Cross
                    expected = "portableStatus 变为 fail；crossBuildStatus 仍为 $baselineCross"
                    ok = (($ic2Portable -eq 'fail') -and ($ic2Cross -eq $baselineCross))
                })
            $independenceControlsFailed = @($independenceControls | Where-Object { $_.ok -ne $true })

            # ---- L12 单独判定（不并入 portableStatus） ----
            $allowedCrossBuild = @('pass', 'toolchainUnsupported')
            $crossBuildAcceptable = $allowedCrossBuild -contains [string] $crossBuildStatus
            $crossBuildLogPath = Join-Path $repositoryRoot ([string] $d24Config.crossBuildLog)
            if ($crossBuildStatus -eq 'toolchainUnsupported' -and -not (Test-Path -LiteralPath $crossBuildLogPath -PathType Leaf)) {
                $crossBuildAcceptable = $false
            }
            $productionInteropChecks = @($checks | Where-Object { [string] $_.id -eq 'production-interop-l13' })
            $productionInteropPreview = if ($productionInteropChecks.Count -gt 0) { [string] $productionInteropChecks[0].status } else { 'notRun' }
            $harnessIntegrationPreview = Resolve-DshHarnessIntegrationStatus -Checks @($checks)

            # ---- 汇总违规、写机器可读报告（先落盘，再决定是否失败） ----
            $d24HardViolations = @($coverageGaps) + @($zeroToleranceViolations) + @($identityViolations)
            if ($zeroToleranceControlsFailed.Count -gt 0) {
                $d24HardViolations += @($zeroToleranceControlsFailed | ForEach-Object { "零容忍负向控制未成立：$($_.id)" })
            }
            if ($independenceControlsFailed.Count -gt 0) {
                $d24HardViolations += @($independenceControlsFailed | ForEach-Object { "独立性负向控制未成立：$($_.id)" })
            }
            if (-not $structuralIndependent) {
                $d24HardViolations += 'portableStatus 纯函数意外接受 CrossBuildStatus 参数（结构性独立性被破坏）'
            }

            $previewPortableFailures = @($baselinePortableFailures)
            if (@($d24HardViolations).Count -gt 0) { $previewPortableFailures += 'd24-linux-convergence' }
            $previewPortable = Resolve-DshPortableStatus -PortableFailureIds $previewPortableFailures -IncompleteCount $baselineIncomplete -RequiredCases $d24Required -ExecutedCases $d24Executed -FailedCases $d24Failed -SkippedCases $d24Skipped
            $previewCross = $baselineCross

            $runId = 'd24-' + $startedAtUtc.ToString('yyyy-MM-ddTHH-mm-ss-fffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
            $runDirectory = Join-Path $repositoryRoot ("artifacts/verify-portable/d24-runs/{0}" -f $runId)
            $null = New-Item -ItemType Directory -Path $runDirectory -Force
            $reportObject = [ordered]@{
                schemaVersion         = 1
                task                  = 'D24'
                purpose               = 'converge-complete-linux-development-verification'
                runId                 = $runId
                generatedAtUtc        = [DateTime]::UtcNow.ToString('O')
                startedAtUtc          = $startedAtUtc.ToString('O')
                platform              = 'linux'
                verificationProfile   = $Profile
                candidate             = $candidateBlock
                layers                = @($layerViews)
                gaps                  = @($coverageGaps)
                separatelyJudgedLayers = @($d24Config.separatelyJudgedLayers)
                counts                = [ordered]@{ requiredCases = $d24Required; executedCases = $d24Executed; failedCases = $d24Failed; skippedCases = $d24Skipped }
                zeroTolerance         = [ordered]@{ passed = (@($zeroToleranceViolations).Count -eq 0); violations = @($zeroToleranceViolations); controls = @($zeroToleranceControls) }
                independence          = [ordered]@{
                    portableStatus                 = $previewPortable
                    crossBuildStatus               = $previewCross
                    crossBuildAcceptable           = $crossBuildAcceptable
                    crossBuildEvidence             = [string] $d24Config.crossBuildLog
                    portableStatusInputs           = @('PortableFailureIds', 'IncompleteCount', 'RequiredCases', 'ExecutedCases', 'FailedCases', 'SkippedCases')
                    portableStatusReadsCrossBuild  = (-not $structuralIndependent)
                    controls                       = @($independenceControls)
                }
                identity              = [ordered]@{
                    declaredEvidence = @($declaredEvidence)
                    agreement        = @($identityAgreement)
                    notDeclared      = @($d24Config.identityNotDeclared)
                    notDeclaredNote  = [string] $d24Config.identityNotDeclaredNote
                    violations       = @($identityViolations)
                }
                notRun                = @($notRunInventory)
                notRunSummary         = [ordered]@{
                    total                 = @($notRunInventory).Count
                    windowsPending        = @($notRunInventory | Where-Object { $_.justification -eq 'WindowsPending' }).Count
                    justifiedScopeLimited = @($notRunInventory | Where-Object { $_.justification -eq 'justified-scope-limited' }).Count
                    unjustified           = @($notRunInventory | Where-Object { $_.justification -eq 'unjustified' } | ForEach-Object { $_.id })
                }
                summary               = [ordered]@{
                    portableStatus     = $previewPortable
                    crossBuildStatus   = $previewCross
                    developmentReady   = $false
                    releaseEligible    = $false
                    harnessIntegration = $harnessIntegrationPreview
                    productionInterop  = $productionInteropPreview
                    windowsValidation  = 'notRun'
                }
                hardViolations        = @($d24HardViolations)
                result                = if (@($d24HardViolations).Count -eq 0) { 'pass' } else { 'fail' }
                evidenceLayout        = [ordered]@{
                    runDirectory = "artifacts/verify-portable/d24-runs/$runId"
                    index        = 'artifacts/verify-portable/d24-runs/index.json'
                    latestMirror = 'artifacts/verify-portable/d24-linux-report.json'
                }
            }
            $reportText = $reportObject | ConvertTo-Json -Depth 40
            Set-Content -LiteralPath (Join-Path $runDirectory 'report.json') -Value $reportText -Encoding utf8NoBOM
            Set-Content -LiteralPath (Join-Path $repositoryRoot 'artifacts/verify-portable/d24-linux-report.json') -Value $reportText -Encoding utf8NoBOM

            # 只增不改的索引：每次运行 push 一条，历史报告在各自 runId 目录。
            $indexPath = Join-Path $repositoryRoot 'artifacts/verify-portable/d24-runs/index.json'
            $index = if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
                Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 60
            }
            else {
                [pscustomobject]@{ schemaVersion = 1; runs = @() }
            }
            $index.runs = @($index.runs) + @([pscustomobject]@{
                    runId           = $runId
                    generatedAtUtc  = $reportObject.generatedAtUtc
                    candidateGitHead = $gitHead
                    pluginVersion   = [string] $manifestJson.version
                    requiredCases   = $d24Required
                    executedCases   = $d24Executed
                    failedCases     = $d24Failed
                    skippedCases    = $d24Skipped
                    portableStatus  = $previewPortable
                    crossBuildStatus = $previewCross
                    result          = $reportObject.result
                    report          = "artifacts/verify-portable/d24-runs/$runId/report.json"
                })
            ($index | ConvertTo-Json -Depth 60) |
                Set-Content -LiteralPath $indexPath -Encoding utf8NoBOM

            # 把聚合结果回传给摘要（子作用域只能写回引用类型）。
            $d24State['candidate'] = $candidateBlock
            $d24State['runId'] = $runId
            $d24State['reportPath'] = 'artifacts/verify-portable/d24-linux-report.json'
            $d24State['layers'] = @($layerViews)
            $d24State['notRunInventory'] = @($notRunInventory)
            $d24State['zeroToleranceViolations'] = @($zeroToleranceViolations)
            $d24State['identityViolations'] = @($identityViolations)
            $d24State['gaps'] = @($coverageGaps)
            $d24State['portableStatusPreview'] = $previewPortable
            $d24State['crossBuildStatus'] = $previewCross
            $d24State['crossBuildAcceptable'] = $crossBuildAcceptable

            Write-Host ("D24 runId=$runId 覆盖层=$(@($layerViews).Count) 缺口=$(@($coverageGaps).Count) 零容忍违规=$(@($zeroToleranceViolations).Count) 身份违规=$(@($identityViolations).Count)")
            Write-Host ("D24 用例 required=$d24Required executed=$d24Executed failed=$d24Failed skipped=$d24Skipped；notRun total=$(@($notRunInventory).Count) WindowsPending=$(@($notRunInventory | Where-Object { $_.justification -eq 'WindowsPending' }).Count) 白名单=$(@($notRunInventory | Where-Object { $_.justification -eq 'justified-scope-limited' }).Count) unjustified=$(@($notRunInventory | Where-Object { $_.justification -eq 'unjustified' }).Count)")
            Write-Host ("D24 独立性：portableStatus=$previewPortable crossBuildStatus=$previewCross（交叉构建可接受=$crossBuildAcceptable）")
            Write-Host ("D24 负向控制：零容忍 $((@($zeroToleranceControls | Where-Object { $_.ok -eq $true }).Count))/$(@($zeroToleranceControls).Count) 成立；独立性 $((@($independenceControls | Where-Object { $_.ok -eq $true }).Count))/$(@($independenceControls).Count) 成立")

            if (@($d24HardViolations).Count -gt 0) {
                throw ("D24 收敛检查失败：`n" + (@($d24HardViolations) -join [Environment]::NewLine))
            }
        }

        # 尚未实现的必需项：显式记为 incomplete，绝不静默通过。
        foreach ($pendingCheck in @($requiredChecks | Where-Object { $_ -notin @($checks.id) })) {
            Add-DshCheckResult -Id $pendingCheck -Status 'incomplete' -Detail '该层级尚未实现（属后续 DEV 任务）'
        }
    }
    else {
        # Core profile 不得冒充整个项目：明确声明自己未覆盖的范围。
        $notCovered = @($config.profiles | Where-Object { $_.name -eq 'Development' })[0].requiredChecks |
            Where-Object { $_ -notin @($checks.id) }
        foreach ($pendingCheck in @($notCovered)) {
            Add-DshCheckResult -Id $pendingCheck -Status 'incomplete' -Detail '不在 Core profile 范围内'
        }
    }

    # ---- 汇总 ----
    $requiredOnLinux = @($config.requiredTestAssembliesOnLinux)
    $executedCases = 0
    $failedCases = 0
    $skippedCases = 0
    foreach ($result in $testResults) {
        $executedCases += [int] $result.total
        $failedCases += [int] $result.failed
        $skippedCases += [int] $result.skipped + [int] $result.notRun
    }
    # 必需用例数按 manifest 的**显式用例集**累加：core 一定执行，interop 只在 Development
    # profile 里由 production-interop-l13 执行。集合划分与执行计划因此是同一条真值。
    $requiredCases = 0
    foreach ($assemblyName in $requiredOnLinux) {
        if (-not $expectedDataset.Contains($assemblyName)) { continue }
        $assemblySets = $expectedDataset[$assemblyName].sets
        foreach ($setName in @($assemblySets.Keys)) {
            if ($setName -eq 'interop' -and $Profile -ne 'Development') { continue }
            $requiredCases += @($assemblySets[$setName].hashes).Count
        }
    }

    $incomplete = @($checks | Where-Object { $_.status -eq 'incomplete' })
    # portableStatus 只由可移植层推导：L12（cross-build-l12）从可移植失败集合里排除，
    # 由 crossBuildStatus 单独判定，二者因此不能互相掩盖。零用例同样判 fail。
    $portableFailureIds = @($checks |
            Where-Object { [string] $_.status -eq 'fail' -and [string] $_.id -ne 'cross-build-l12' } |
            ForEach-Object { [string] $_.id })
    $portableStatus = Resolve-DshPortableStatus -PortableFailureIds $portableFailureIds -IncompleteCount $incomplete.Count `
        -RequiredCases $requiredCases -ExecutedCases $executedCases -FailedCases $failedCases -SkippedCases $skippedCases
    # crossBuildStatus 只由 cross-build-l12 的状态推导（与 portableStatus 的输入完全不相交）。
    $crossBuildStatus = Resolve-DshCrossBuildStatus -Checks @($checks)

    # productionInterop 只反映真实执行过的检查状态：未跑就是 notRun，绝不冒充通过。
    $productionInteropStatus = 'notRun'
    $interopChecks = @($checks | Where-Object { $_.id -eq 'production-interop-l13' })
    if ($interopChecks.Count -gt 0) { $productionInteropStatus = [string] $interopChecks[0].status }
    # harnessIntegration 同理按 L06/L07 的真实 Harness 检查推导，不写死 notRun。
    $harnessIntegrationStatus = Resolve-DshHarnessIntegrationStatus -Checks @($checks)

    $sourceState = Get-DshSourceState -RepositoryRoot $repositoryRoot

    # Core profile 不跑 D24；$d24State 为空时下面必须得到空集合，而不是把 $null 当成一个元素。
    $d24Layers = @()
    if ($null -ne $d24State['layers']) { $d24Layers = @($d24State['layers']) }
    $d24NotRunItems = @()
    if ($null -ne $d24State['notRunInventory']) { $d24NotRunItems = @($d24State['notRunInventory']) }
    $d24Gaps = @()
    if ($null -ne $d24State['gaps']) { $d24Gaps = @($d24State['gaps']) }

    $summary = [ordered]@{
        schemaVersion       = 1
        startedAtUtc        = $startedAtUtc.ToString('O')
        endedAtUtc          = [DateTime]::UtcNow.ToString('O')
        commit              = $sourceState.Commit
        worktreeState       = $sourceState.WorktreeState
        platform            = 'linux'
        verificationProfile = $Profile
        portableStatus      = $portableStatus
        crossBuildStatus    = $crossBuildStatus
        requiredCases       = $requiredCases
        executedCases       = $executedCases
        failedCases         = $failedCases
        skippedCases        = $skippedCases
        harnessIntegration  = $harnessIntegrationStatus
        productionInterop   = $productionInteropStatus
        windowsValidation   = 'notRun'
        releaseEligible     = $false
        developmentReady    = $false
        notCoveredByProfile = @($incomplete | ForEach-Object { $_.id })
        # D24：可移植层与交叉构建的失败集合彼此独立，摘要里显式给出输入以证明不可互相掩盖。
        portableLayerFailures = @($portableFailureIds)
        crossBuildIndependence = [ordered]@{
            portableStatusInputs          = @('PortableFailureIds', 'IncompleteCount', 'RequiredCases', 'ExecutedCases', 'FailedCases', 'SkippedCases')
            crossBuildStatusInputs        = @('cross-build-l12 check status')
            portableStatusReadsCrossBuild = $false
        }
        candidate           = $d24State['candidate']
        d24Report           = $d24State['reportPath']
        d24RunId            = $d24State['runId']
        d24Coverage         = @($d24Layers | ForEach-Object {
                [ordered]@{ layer = $_.layer; status = $_.status; checks = @($_.checks | ForEach-Object { $_.id }); evidence = @($_.evidence | ForEach-Object { $_.path }) }
            })
        d24Gaps             = @($d24Gaps)
        notRunInventory     = @($d24NotRunItems)
        notRunSummary       = [ordered]@{
            total                 = @($d24NotRunItems).Count
            windowsPending        = @($d24NotRunItems | Where-Object { $_.justification -eq 'WindowsPending' }).Count
            justifiedScopeLimited = @($d24NotRunItems | Where-Object { $_.justification -eq 'justified-scope-limited' }).Count
            unjustified           = @($d24NotRunItems | Where-Object { $_.justification -eq 'unjustified' } | ForEach-Object { $_.id })
        }
        testAssemblies      = @($testResults)
        checks              = @($checks)
        note                = '本摘要只证明非 Windows 开发层级，不是发布凭据；eng/verify.ps1 的 verify-summary.json 才是正式门禁摘要。'
    }
    $summary | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (Join-Path $ArtifactsDirectory 'portable-verify-summary.json') -Encoding utf8NoBOM
    $summaryWritten = $true

    Write-Host ''
    Write-Host "portableStatus=$portableStatus  crossBuildStatus=$crossBuildStatus  cases=$executedCases/$requiredCases（failed=$failedCases skipped=$skippedCases）"
    if ($incomplete.Count -gt 0) {
        Write-Host "未覆盖 / 未实现（$($incomplete.Count)）：$((@($incomplete | ForEach-Object { $_.id }) -join ', '))"
    }

    if ($failures.Count -gt 0) {
        Write-Host ''
        Write-Host '失败项：' -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        Write-Error "verify-portable.ps1: FAIL（profile=$Profile）`n$($failures -join [Environment]::NewLine)"
        exit 1
    }

    if ($incomplete.Count -gt 0) {
        # 必需项未实现不是通过。Core profile 用退出码 0 表示自身范围成立，
        # 但摘要明确 portableStatus=fail 且 developmentReady=false。
        Write-Host ''
        Write-Host "verify-portable.ps1：profile=$Profile 自身范围通过；完整 Development 验证仍未完成（incomplete=$($incomplete.Count)）。"
        exit 0
    }

    Write-Host ''
    Write-Host "verify-portable.ps1: PASS（profile=$Profile）"
    exit 0
}
catch {
    if ($null -ne $resolvedArtifactsDirectory -and -not $summaryWritten) {
        [ordered]@{
            schemaVersion       = 1
            startedAtUtc        = $startedAtUtc.ToString('O')
            endedAtUtc          = [DateTime]::UtcNow.ToString('O')
            platform            = 'linux'
            verificationProfile = $Profile
            portableStatus      = 'fail'
            crossBuildStatus    = 'notRun'
            requiredCases       = 0
            executedCases       = 0
            failedCases         = 0
            skippedCases        = 0
            harnessIntegration  = 'notRun'
            productionInterop   = 'notRun'
            windowsValidation   = 'notRun'
            releaseEligible     = $false
            developmentReady    = $false
            failure             = $_.Exception.Message
            checks              = @($checks)
            note                = '入口在完成检查前失败；本摘要不是发布凭据。'
        } | ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath (Join-Path $resolvedArtifactsDirectory 'portable-verify-summary.json') -Encoding utf8NoBOM
    }
    Write-Error "verify-portable.ps1: FAIL`n$($_.Exception.Message)"
    exit 1
}
