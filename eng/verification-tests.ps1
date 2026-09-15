# Linux 开发门禁的共享测试核对逻辑（D02）。
# 复用固定用例枚举、固定数据集、failSkips、触发标签与按项目求值 RID 的调用方式。
# 本文件只定义函数，入口为 eng/verify-portable.ps1。

Set-StrictMode -Version Latest

function Get-DshVerificationProfiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $path = Join-Path $RepositoryRoot 'eng/verification-profiles.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "缺少门禁配置：$path"
    }
    $config = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
    if ($config.schemaVersion -ne 1) {
        throw "未知的门禁配置模式：$($config.schemaVersion)"
    }
    foreach ($requiredProfile in @('Core', 'Development')) {
        if (@($config.profiles | Where-Object { $_.name -eq $requiredProfile }).Count -ne 1) {
            throw "门禁配置必须且只能定义一次 profile：$requiredProfile"
        }
    }
    if (@($config.expectedProjects).Count -ne 6) {
        throw "门禁配置必须声明 6 个预期项目。"
    }
    # 生产可移植性的护栏：Core 与 Core.Tests 必须被声明为无 Windows 目标且无 RID。
    foreach ($expected in @($config.expectedProjects | Where-Object { $_.portable })) {
        if ($expected.targetFramework -ne 'net10.0' -or $expected.runtimeIdentifier -ne '') {
            throw "可移植项目 $($expected.name) 的平台声明不合法：必须是 net10.0 且无 RID。"
        }
    }
    return $config
}

function Get-DshTestAssemblyInventory {
    <#
    .SYNOPSIS
        枚举一个测试程序集的实际用例集合（不执行）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory)]
        [string] $DotNetPath,

        [Parameter(Mandatory)]
        [string] $ProjectPath,

        [Parameter(Mandatory)]
        [string[]] $EnumerationArguments
    )

    $runtimeIdentifier = (Get-DshProjectPlatform -RepositoryRoot $RepositoryRoot -ProjectPath $ProjectPath).RuntimeIdentifier
    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @('run', '--project', $ProjectPath, '--configuration', 'Release')) {
        $arguments.Add($argument)
    }
    if (-not [string]::IsNullOrWhiteSpace($runtimeIdentifier)) {
        $arguments.Add('--runtime')
        $arguments.Add($runtimeIdentifier)
    }
    foreach ($argument in @('--no-build', '--no-restore', '--no-launch-profile', '--')) {
        $arguments.Add($argument)
    }
    foreach ($argument in $EnumerationArguments) {
        $arguments.Add($argument)
    }

    $json = Invoke-DshNativeCapture -FilePath $DotNetPath -Arguments $arguments.ToArray() `
        -WorkingDirectory $RepositoryRoot -OutputEncoding ([Text.UTF8Encoding]::new($false))
    $cases = @($json | ConvertFrom-Json -Depth 100)
    if ($cases.Count -eq 0) {
        throw "测试程序集枚举到 0 个用例：$ProjectPath"
    }
    return $cases
}

function Get-DshTextSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Text
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToUpperInvariant()
}

function Get-DshExpectedTestDataset {
    <#
    .SYNOPSIS
        读取 eng/expected-test-dataset.json 的固定用例哈希清单与显式用例集划分。
    .DESCRIPTION
        返回 [ordered]@{ <程序集> = [ordered]@{ union = <全量哈希>; sets = [ordered]@{ <集合> = [ordered]@{
        hashes; runnerArguments } } } }。未声明 caseSets 的程序集自动获得一个 default 集合。
        声明了 caseSets 时，**并集必须与冻结的 union 完全一致**：显式划分既不得丢掉既有基线
        （additive 原则），也不得凭空增加哈希。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $path = Join-Path $RepositoryRoot 'eng/expected-test-dataset.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "缺少固定测试数据集清单：$path"
    }
    $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
    if ($manifest.schemaVersion -ne 1) {
        throw '固定测试数据集清单模式无效。'
    }
    $result = [ordered]@{}
    foreach ($assembly in @($manifest.assemblies)) {
        $name = [string] $assembly.assembly
        $hashes = @($assembly.caseNameSha256 | ForEach-Object { [string] $_ })
        if ([string]::IsNullOrWhiteSpace($name) -or $result.Contains($name) -or $hashes.Count -eq 0) {
            throw "固定测试数据集清单包含无效程序集：$name"
        }
        $sets = [ordered]@{}
        if ($assembly.PSObject.Properties.Name -contains 'caseSets') {
            foreach ($set in @($assembly.caseSets)) {
                $setName = [string] $set.name
                $setHashes = @($set.caseNameSha256 | ForEach-Object { [string] $_ })
                if ([string]::IsNullOrWhiteSpace($setName) -or $sets.Contains($setName) -or $setHashes.Count -eq 0) {
                    throw "$name：用例集定义无效：$setName"
                }
                $runnerArguments = @()
                if ($set.PSObject.Properties.Name -contains 'runnerArguments') {
                    $runnerArguments = @($set.runnerArguments | ForEach-Object { [string] $_ })
                }
                $sets[$setName] = [ordered]@{
                    hashes          = @($setHashes | Sort-Object)
                    runnerArguments = $runnerArguments
                }
            }
            $unionSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $hashes)
            $setsUnion = [System.Collections.Generic.HashSet[string]]::new()
            foreach ($setName in @($sets.Keys)) {
                $null = $setsUnion.UnionWith([string[]] @($sets[$setName].hashes))
            }
            if (-not $unionSet.SetEquals($setsUnion)) {
                $missing = [System.Collections.Generic.HashSet[string]]::new([string[]] $hashes)
                $missing.ExceptWith([string[]] @($setsUnion))
                $unexpected = [System.Collections.Generic.HashSet[string]]::new([string[]] @($setsUnion))
                $unexpected.ExceptWith([string[]] $hashes)
                throw ("$name：用例集并集与冻结基线不一致；missing=$($missing.Count) unexpected=$($unexpected.Count)。" +
                    '只有显式审查并更新 eng/expected-test-dataset.json 才能重建基线。')
            }
        }
        else {
            $sets['default'] = [ordered]@{
                hashes          = @($hashes | Sort-Object)
                runnerArguments = @()
            }
        }
        $result[$name] = [ordered]@{ union = @($hashes | Sort-Object); sets = $sets }
    }
    return $result
}

function Invoke-DshTestAssemblyCheck {
    <#
    .SYNOPSIS
        执行一个可在 Linux 运行的测试程序集，并与固定数据集逐条核对。
    .DESCRIPTION
        以 failSkips 运行，要求零跳过、零失败、零 notRun，且实际用例哈希集合与
        eng/expected-test-dataset.json 中**指定用例集**完全一致。零用例、缺失或多余用例都判失败。
        CaseSet 为 default 时使用未划分程序集的全量清单；对 DshLauncher.Core.Tests 而言
        core 与 interop 是两个显式集合，分别由 core-test-execution 与 production-interop-l13 执行。
        任何失败都写入 Failures，由调用方决定是否中止；本函数本身不吞错。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory)]
        [string] $DotNetPath,

        [Parameter(Mandatory)]
        [string] $ProjectPath,

        [Parameter(Mandatory)]
        [object] $RunnerConfig,

        [Parameter(Mandatory)]
        [object] $ExpectedDataset,

        [Parameter(Mandatory)]
        [string] $ArtifactsDirectory,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]] $Failures,

        [string] $CaseSet = 'default'
    )

    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $failureCountBefore = $Failures.Count
    $evidence = [ordered]@{
        assembly = $assemblyName
        caseSet = $CaseSet
        discovered = 0
        expected = 0
        total = 0
        passed = 0
        failed = 0
        skipped = 0
        notRun = 0
        result = 'fail'
        evidenceFile = $null
    }

    if (-not $ExpectedDataset.Contains($assemblyName)) {
        $Failures.Add("$assemblyName：不在固定测试数据集清单中。")
        return [pscustomobject] $evidence
    }
    $assemblyEntry = $ExpectedDataset[$assemblyName]
    if (-not $assemblyEntry.sets.Contains($CaseSet)) {
        $Failures.Add("$assemblyName：固定测试数据集清单未定义用例集 $CaseSet（可用：$((@($assemblyEntry.sets.Keys)) -join ', ')）。")
        return [pscustomobject] $evidence
    }
    $setEntry = $assemblyEntry.sets[$CaseSet]
    $expectedHashes = @($setEntry.hashes)
    $setArguments = @($setEntry.runnerArguments)
    $evidence.expected = $expectedHashes.Count

    # 1) 枚举：确认实际发现数与固定清单一致，防止用例被悄悄删除。
    #    枚举须与执行使用同一组过滤参数（用例集划分），否则"发现 536、执行 4"会自相矛盾。
    $cases = @(Get-DshTestAssemblyInventory -RepositoryRoot $RepositoryRoot -DotNetPath $DotNetPath `
            -ProjectPath $ProjectPath -EnumerationArguments (@($RunnerConfig.enumerationArguments) + $setArguments))
    $evidence.discovered = $cases.Count

    $runtimeIdentifier = (Get-DshProjectPlatform -RepositoryRoot $RepositoryRoot -ProjectPath $ProjectPath).RuntimeIdentifier
    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @('run', '--project', $ProjectPath, '--configuration', 'Release')) {
        $arguments.Add($argument)
    }
    if (-not [string]::IsNullOrWhiteSpace($runtimeIdentifier)) {
        $arguments.Add('--runtime')
        $arguments.Add($runtimeIdentifier)
    }
    foreach ($argument in @('--no-build', '--no-restore', '--no-launch-profile', '--')) {
        $arguments.Add($argument)
    }
    foreach ($argument in $RunnerConfig.executionArguments) {
        $arguments.Add($argument)
    }
    foreach ($argument in $setArguments) {
        $arguments.Add($argument)
    }

    $rawResultPath = Join-Path ([IO.Path]::GetTempPath()) ("DshPortable.$([Guid]::NewGuid().ToString('N')).xml")
    try {
        $arguments.Add('-result-xml')
        $arguments.Add($rawResultPath)
        $null = Invoke-DshNativeCapture -FilePath $DotNetPath -Arguments $arguments.ToArray() `
            -WorkingDirectory $RepositoryRoot -OutputEncoding ([Text.UTF8Encoding]::new($false))

        if (-not (Test-Path -LiteralPath $rawResultPath -PathType Leaf)) {
            $Failures.Add("$assemblyName：测试运行器未生成结构化结果。")
            return [pscustomobject] $evidence
        }

        [xml] $testResult = Get-Content -LiteralPath $rawResultPath -Raw -Encoding UTF8
        $assemblyNode = $testResult.SelectSingleNode('/assemblies/assembly')
        if ($null -eq $assemblyNode) {
            $Failures.Add("$assemblyName：测试结果缺少 assembly 节点。")
            return [pscustomobject] $evidence
        }

        $total = [int] $assemblyNode.GetAttribute('total')
        $passed = [int] $assemblyNode.GetAttribute('passed')
        $failed = [int] $assemblyNode.GetAttribute('failed')
        $skipped = [int] $assemblyNode.GetAttribute('skipped')
        $notRun = [int] $assemblyNode.GetAttribute('not-run')
        $evidence.total = $total
        $evidence.passed = $passed
        $evidence.failed = $failed
        $evidence.skipped = $skipped
        $evidence.notRun = $notRun

        if ($total -eq 0) {
            $Failures.Add("$assemblyName：执行了 0 个用例。")
        }
        if ($failed -ne 0 -or $skipped -ne 0 -or $notRun -ne 0 -or $passed -ne $total) {
            $Failures.Add("$assemblyName：结果不完整；total=$total passed=$passed failed=$failed skipped=$skipped notRun=$notRun。")
        }

        $testNodes = @($testResult.SelectNodes('/assemblies/assembly/collection/test'))
        $actualHashes = @($testNodes | ForEach-Object { Get-DshTextSha256 -Text $_.GetAttribute('name') })
        # 用集合比较而不是 Compare-Object 的位置绑定，避免 PowerShell 数组展开带来的假差异。
        $actualSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $actualHashes)
        $expectedSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $expectedHashes)
        if ($actualSet.Count -ne $expectedSet.Count -or -not $actualSet.SetEquals($expectedSet)) {
            $missingSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $expectedHashes)
            $missingSet.ExceptWith([string[]] $actualHashes)
            $unexpectedSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $actualHashes)
            $unexpectedSet.ExceptWith([string[]] $expectedHashes)
            $Failures.Add("$assemblyName：实际用例与固定哈希清单不一致；missing=$($missingSet.Count) unexpected=$($unexpectedSet.Count)。只有显式审查并更新 eng/expected-test-dataset.json 才能重建基线。")
        }

        $missingTags = 0
        $invalidTags = 0
        $caseEvidence = [System.Collections.Generic.List[object]]::new()
        $index = 0
        foreach ($testNode in $testNodes) {
            $tags = @(
                $testNode.SelectNodes('./traits/trait[@name="triggerTags"]') |
                    ForEach-Object { $_.GetAttribute('value').Split(',') } |
                    ForEach-Object { $_.Trim() } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    Sort-Object -Unique
            )
            if (@($tags | Where-Object { $_ -match '^VFY-0[1-8]$' }).Count -eq 0) { $missingTags++ }
            if (@($tags | Where-Object { $_ -notmatch '^(?:VFY-0[1-8]|RS-(?:0[1-9]|1[0-5])|[a-z][a-z0-9-]*)$' }).Count -gt 0) { $invalidTags++ }
            $caseEvidence.Add([ordered]@{
                    sequence    = $index
                    nameSha256  = Get-DshTextSha256 -Text $testNode.GetAttribute('name')
                    class       = $testNode.GetAttribute('type')
                    method      = $testNode.GetAttribute('method')
                    result      = $testNode.GetAttribute('result')
                    triggerTags = $tags
                })
            $index++
        }
        if ($missingTags -gt 0) { $Failures.Add("$assemblyName：$missingTags 个用例缺少 VFY triggerTags。") }
        if ($invalidTags -gt 0) { $Failures.Add("$assemblyName：$invalidTags 个用例包含无效 triggerTags。") }

        # 证据文件名带用例集：core 与 interop 是两次独立执行，不能互相覆盖。
        $evidenceFileName = if ($CaseSet -eq 'default') {
            "$assemblyName.test-evidence.json"
        }
        else {
            "$assemblyName.$CaseSet.test-evidence.json"
        }
        [ordered]@{
            schemaVersion = 1
            assembly      = $assemblyName
            caseSet       = $CaseSet
            platform      = 'linux'
            expected      = $expectedHashes.Count
            discovered    = $cases.Count
            total         = $total
            passed        = $passed
            failed        = $failed
            skipped       = $skipped
            notRun        = $notRun
            tests         = @($caseEvidence | Sort-Object sequence)
        } | ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath (Join-Path $ArtifactsDirectory $evidenceFileName) -Encoding utf8NoBOM
        $evidence.evidenceFile = $evidenceFileName
        $evidence.result = if ($Failures.Count -eq $failureCountBefore) { 'pass' } else { 'fail' }
    }
    finally {
        if (Test-Path -LiteralPath $rawResultPath -PathType Leaf) {
            [IO.File]::Delete($rawResultPath)
        }
    }

    return [pscustomobject] $evidence
}
