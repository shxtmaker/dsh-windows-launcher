[CmdletBinding()]
param(
    [string] $DotNetPath,

    [string] $ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')
$startedAtUtc = [DateTime]::UtcNow
$resolvedArtifactsDirectory = $null
$completedGates = [System.Collections.Generic.List[string]]::new()
$testAssemblyResults = [System.Collections.Generic.List[object]]::new()
$verifiedTriggerTags = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$verificationImpact = $null

function Get-DshExpectedTestDataset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $path = Join-Path $RepositoryRoot 'eng/expected-test-dataset.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "缺少固定测试数据集清单：$path"
    }

    $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 |
        ConvertFrom-Json -Depth 100
    if ($manifest.schemaVersion -ne 1 -or (@($manifest.assemblies).Count) -ne 3) {
        throw '固定测试数据集清单模式无效，或测试程序集数量不是 3。'
    }

    $result = @{}
    foreach ($assembly in @($manifest.assemblies)) {
        $name = [string] $assembly.assembly
        $hashes = @($assembly.caseNameSha256 | ForEach-Object { [string] $_ })
        if ([string]::IsNullOrWhiteSpace($name) -or
            $result.ContainsKey($name) -or
            $hashes.Count -eq 0 -or
            @($hashes | Where-Object { $_ -notmatch '^[A-F0-9]{64}$' }).Count -gt 0) {
            throw "固定测试数据集清单包含无效程序集或用例哈希：$name"
        }

        $result[$name] = @($hashes | Sort-Object)
    }

    return $result
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

function Assert-DshFixtureInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory)]
        [string] $OutputDirectory
    )

    $serverPath = Join-Path $RepositoryRoot 'eng/fixtures/linux/fixture_server.py'
    $controlPath = Join-Path $RepositoryRoot 'eng/fixtures/linux/fixture-control.sh'
    $legacyRootPath = Join-Path $RepositoryRoot 'eng/fixtures/linux/legacy-root-0.1.1-rc.2.html'
    $legacyLicensePath = Join-Path $RepositoryRoot 'eng/fixtures/linux/LICENSE.deepseek-harness'
    foreach ($path in @($serverPath, $controlPath, $legacyRootPath, $legacyLicensePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "缺少 VFY-04 必需夹具：$path"
        }
    }
    if ((Get-Item -LiteralPath $legacyRootPath).Length -ne 14556 -or
        (Get-DshSha256 -Path $legacyRootPath) -cne
            'A1C9E8D395D34FC83466B19D652A36E94B4F643FAFEF57F840B226B0BFAB74DE') {
        throw '旧版无认证 Harness 夹具正文偏离固定 npm 基线。'
    }

    $server = Get-Content -LiteralPath $serverPath -Raw -Encoding UTF8
    $control = Get-Content -LiteralPath $controlPath -Raw -Encoding UTF8
    $requiredModes = @(
        'wrong-200',
        'fence-403',
        'old-unauthenticated',
        'redirect-302',
        'notfound-404',
        'non-http',
        'timeout'
    )
    foreach ($mode in $requiredModes) {
        if (-not $server.Contains("`"$mode`"", [StringComparison]::Ordinal) -or
            -not $control.Contains($mode, [StringComparison]::Ordinal)) {
            throw "VFY-04 夹具缺少必需模式：$mode"
        }
    }

    foreach ($requiredFragment in @(
        'def log_message(',
        'bind address must be RFC1918',
        'PYTHONDONTWRITEBYTECODE=1',
        'Fixture already running:',
        'refusing to force-kill'
    )) {
        if (-not ($server.Contains($requiredFragment, [StringComparison]::Ordinal) -or
                  $control.Contains($requiredFragment, [StringComparison]::Ordinal))) {
            throw "VFY-04 夹具缺少安全或幂等约束：$requiredFragment"
        }
    }

    $evidence = [ordered]@{
        schemaVersion = 1
        required = $true
        modes = $requiredModes
        files = @(
            [ordered]@{
                path = 'eng/fixtures/linux/fixture_server.py'
                sha256 = Get-DshSha256 -Path $serverPath
            },
            [ordered]@{
                path = 'eng/fixtures/linux/fixture-control.sh'
                sha256 = Get-DshSha256 -Path $controlPath
            },
            [ordered]@{
                path = 'eng/fixtures/linux/legacy-root-0.1.1-rc.2.html'
                sha256 = Get-DshSha256 -Path $legacyRootPath
            },
            [ordered]@{
                path = 'eng/fixtures/linux/LICENSE.deepseek-harness'
                sha256 = Get-DshSha256 -Path $legacyLicensePath
            }
        )
    }
    $evidence | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'fixture-inventory.json') -Encoding utf8NoBOM
}

try {
    $constants = Get-DshReleaseConstants -RepositoryRoot $repositoryRoot
    Assert-DshReleaseConstants -RepositoryRoot $repositoryRoot -Constants $constants
    $completedGates.Add('release-constants')
    $completedGates.Add('harness-source-fingerprint')

    $globalJson = Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($globalJson.sdk.version -ne $constants.build.dotnetSdkVersion -or
        $globalJson.sdk.rollForward -ne 'disable' -or
        $globalJson.sdk.allowPrerelease -ne $false) {
        throw 'global.json 未锁定到发布常量指定 SDK，或仍允许版本滚动。'
    }

    $directoryBuild = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw -Encoding UTF8
    foreach ($requiredFragment in @(
        '<TargetFramework>net10.0-windows</TargetFramework>',
        '<RuntimeIdentifier>win-x64</RuntimeIdentifier>',
        '<Nullable>enable</Nullable>',
        '<EnableNETAnalyzers>true</EnableNETAnalyzers>',
        '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>',
        '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>'
    )) {
        if (-not $directoryBuild.Contains($requiredFragment, [StringComparison]::Ordinal)) {
            throw "Directory.Build.props 缺少固定门禁：$requiredFragment"
        }
    }

    $centralPackages = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Packages.props') -Raw -Encoding UTF8
    if ($centralPackages -match 'Version="[^"]*[\*\[(]') {
        throw '中心包版本存在漂移（出现通配或浮动版本）。'
    }

    $projects = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src'), (Join-Path $repositoryRoot 'tests') -Filter '*.csproj' -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' }
    )
    if ($projects.Count -ne 6) {
        throw "项目数必须为 6，实际为 $($projects.Count)。"
    }

    foreach ($project in $projects) {
        $lockPath = Join-Path $project.DirectoryName 'packages.lock.json'
        if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
            throw "缺少锁文件：$lockPath"
        }

        $lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
        if ($lock.version -ne 2) {
            throw "未知 NuGet 锁文件版本：$lockPath"
        }
    }

    [xml] $nugetConfig = Get-Content -LiteralPath (Join-Path $repositoryRoot 'NuGet.config') -Raw -Encoding UTF8
    $sources = @($nugetConfig.configuration.packageSources.add)
    if ($sources.Count -ne 1 -or
        $sources[0].key -ne 'nuget.org' -or
        $sources[0].value -ne 'https://api.nuget.org/v3/index.json') {
        throw 'NuGet 只允许固定的 nuget.org HTTPS 源。'
    }

    $dotnet = Get-DshExactDotNetPath -ExpectedVersion $constants.build.dotnetSdkVersion -DotNetPath $DotNetPath
    $solution = Join-Path $repositoryRoot 'DshWindowsLauncher.slnx'

    if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
        $ArtifactsDirectory = Join-Path $repositoryRoot 'artifacts/verify'
    }

    $ArtifactsDirectory = [IO.Path]::GetFullPath($ArtifactsDirectory)
    $null = New-Item -ItemType Directory -Path $ArtifactsDirectory -Force
    $resolvedArtifactsDirectory = $ArtifactsDirectory

    Write-Host '[VFY-08] pairing-baseline governance identity'
    $pairingIdentity = [pscustomobject][ordered]@{
        plugin = [string] $constants.pairingBaseline.plugin
        referenceVersion = [string] $constants.pairingBaseline.referenceVersion
        cookieName = [string] $constants.pairingBaseline.cookieName
    }
    $completedGates.Add('pairing-governance-identity')

    Write-Host '[VFY-08] fail-closed verification impact map'
    $verificationImpact = Get-DshCurrentVerificationImpact `
        -RepositoryRoot $repositoryRoot `
        -Constants $constants
    $verificationImpact | ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath (Join-Path $ArtifactsDirectory 'verification-impact.json') -Encoding utf8NoBOM
    $completedGates.Add('verification-impact-map')

    
    Write-Host "[VFY-01] locked restore ($($constants.build.dotnetSdkVersion))"
    Invoke-DshNative -FilePath $dotnet -Arguments @('restore', $solution, '--locked-mode') -WorkingDirectory $repositoryRoot
    $completedGates.Add('locked-restore')

    Write-Host '[VFY-01] dotnet format --verify-no-changes'
    Invoke-DshNative -FilePath $dotnet -Arguments @('format', $solution, '--verify-no-changes', '--no-restore') -WorkingDirectory $repositoryRoot
    $completedGates.Add('format')

    Write-Host '[VFY-01] Release win-x64 build'
    Invoke-DshNative -FilePath $dotnet -Arguments @('build', $solution, '--configuration', 'Release', '--no-restore', '--warnaserror') -WorkingDirectory $repositoryRoot
    $completedGates.Add('release-win-x64-build')

    Write-Host '[VFY-02..07] Microsoft.Testing.Platform test executables'
    $testProjects = @(
        $projects |
            Where-Object { $_.FullName.StartsWith((Join-Path $repositoryRoot 'tests'), [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object FullName
    )
    if ($testProjects.Count -ne 3) {
        throw "测试项目数必须为 3，实际为 $($testProjects.Count)。"
    }
    $fixedTestDataset = Get-DshExpectedTestDataset -RepositoryRoot $repositoryRoot
    $projectAssemblyNames = @($testProjects.BaseName | Sort-Object)
    $fixedAssemblyNames = @($fixedTestDataset.Keys | Sort-Object)
    if (($projectAssemblyNames -join '|') -ne ($fixedAssemblyNames -join '|')) {
        throw "固定测试数据集清单与测试项目不一致。项目：$($projectAssemblyNames -join ', ')；清单：$($fixedAssemblyNames -join ', ')"
    }
    $testGateFailures = [System.Collections.Generic.List[string]]::new()
    $observedVfyTags = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($testProject in $testProjects) {
        $inventoryJson = Invoke-DshNativeCapture -FilePath $dotnet -Arguments @(
            'run',
            '--project', $testProject.FullName,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--no-build',
            '--no-restore',
            '--no-launch-profile',
            '--',
            '-list', 'full/json',
            '-preEnumerateTheories',
            '-printMaxStringLength', '0',
            '-noColor',
            '-noLogo'
        ) -WorkingDirectory $repositoryRoot -OutputEncoding ([Text.UTF8Encoding]::new($false))
        $expectedTests = @($inventoryJson | ConvertFrom-Json -Depth 100)
        if ($expectedTests.Count -eq 0) {
            $testGateFailures.Add("$($testProject.BaseName)：发现 0 个预期测试数据集。")
            continue
        }

        $rawResultPath = Join-Path ([IO.Path]::GetTempPath()) ("DshLauncher.Verify.$([Guid]::NewGuid().ToString('N')).xml")
        try {
            $testOutput = Invoke-DshNativeCapture -FilePath $dotnet -Arguments @(
                'run',
                '--project', $testProject.FullName,
                '--configuration', 'Release',
                '--runtime', 'win-x64',
                '--no-build',
                '--no-restore',
                '--no-launch-profile',
                '--',
                '-explicit', 'on',
                '-failSkips',
                '-preEnumerateTheories',
                '-printMaxStringLength', '0',
                '-noColor',
                '-noLogo',
                '-reporter', 'quiet',
                '-result-xml', $rawResultPath
            ) -WorkingDirectory $repositoryRoot -OutputEncoding ([Text.UTF8Encoding]::new($false))
            if (-not (Test-Path -LiteralPath $rawResultPath -PathType Leaf)) {
                throw "测试运行器未生成结构化结果：$($testProject.BaseName)"
            }

            [xml] $testResult = Get-Content -LiteralPath $rawResultPath -Raw -Encoding UTF8
            $assemblyNode = $testResult.SelectSingleNode('/assemblies/assembly')
            if ($null -eq $assemblyNode) {
                throw "测试结果缺少 assembly 节点：$($testProject.BaseName)"
            }

            $actualTests = @($testResult.SelectNodes('/assemblies/assembly/collection/test'))
            $expectedGroups = @{}
            foreach ($expectedTest in $expectedTests) {
                $key = "$($expectedTest.Class)`n$($expectedTest.Method)"
                if (-not $expectedGroups.ContainsKey($key)) {
                    $expectedGroups[$key] = 0
                }
                $expectedGroups[$key]++
            }
            $actualGroups = @{}
            foreach ($testNode in $actualTests) {
                $key = "$($testNode.GetAttribute('type'))`n$($testNode.GetAttribute('method'))"
                if (-not $actualGroups.ContainsKey($key)) {
                    $actualGroups[$key] = 0
                }
                $actualGroups[$key]++
            }
            $missingMethodCount = @($expectedGroups.Keys | Where-Object { -not $actualGroups.ContainsKey($_) }).Count
            $unexpectedMethodCount = @($actualGroups.Keys | Where-Object { -not $expectedGroups.ContainsKey($_) }).Count
            $fixedDatasetMismatchCount = @(
                $expectedGroups.Keys |
                    Where-Object {
                        $expectedGroups[$_] -gt 1 -and
                        $actualGroups.ContainsKey($_) -and
                        $actualGroups[$_] -ne $expectedGroups[$_]
                    }
            ).Count
            if ($missingMethodCount -gt 0 -or $unexpectedMethodCount -gt 0 -or $fixedDatasetMismatchCount -gt 0) {
                $testGateFailures.Add(
                    "$($testProject.BaseName)：预期方法/固定数据集与实际执行不一致；missingMethods=$missingMethodCount，unexpectedMethods=$unexpectedMethodCount，fixedDatasetMismatches=$fixedDatasetMismatchCount。")
            }

            $missingMetadataCount = 0
            $invalidMetadataCount = 0
            $caseEvidence = [System.Collections.Generic.List[object]]::new()
            $caseIndex = 0
            foreach ($testNode in $actualTests) {
                $nameHash = Get-DshTextSha256 -Text $testNode.GetAttribute('name')
                $triggerTags = @(
                    $testNode.SelectNodes('./traits/trait[@name="triggerTags"]') |
                        ForEach-Object { $_.GetAttribute('value').Split(',') } |
                        ForEach-Object { $_.Trim() } |
                        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                        Sort-Object -Unique
                )
                $vfyTags = @($triggerTags | Where-Object { $_ -match '^VFY-0[1-8]$' })
                if ($vfyTags.Count -eq 0) {
                    $missingMetadataCount++
                }
                if (@($triggerTags | Where-Object { $_ -notmatch '^(?:VFY-0[1-8]|RS-(?:0[1-9]|1[0-5])|[a-z][a-z0-9-]*)$' }).Count -gt 0) {
                    $invalidMetadataCount++
                }
                foreach ($tag in $vfyTags) {
                    $null = $observedVfyTags.Add($tag)
                }

                $caseEvidence.Add([ordered]@{
                    sequence = $caseIndex
                    nameSha256 = $nameHash
                    class = $testNode.GetAttribute('type')
                    method = $testNode.GetAttribute('method')
                    result = $testNode.GetAttribute('result')
                    triggerTags = $triggerTags
                })
                $caseIndex++
            }
            if ($missingMetadataCount -gt 0) {
                $testGateFailures.Add("$($testProject.BaseName)：$missingMetadataCount 个测试数据集缺少 VFY triggerTags。")
            }
            if ($invalidMetadataCount -gt 0) {
                $testGateFailures.Add("$($testProject.BaseName)：$invalidMetadataCount 个测试数据集包含无效 triggerTags。")
            }

            $actualCaseHashes = @(
                $caseEvidence |
                    ForEach-Object { [string] $_.nameSha256 } |
                    Sort-Object
            )
            $fixedCaseHashes = @($fixedTestDataset[$testProject.BaseName])
            if (($actualCaseHashes -join '|') -cne ($fixedCaseHashes -join '|')) {
                $missingFixedCases = @(
                    Compare-Object -ReferenceObject $fixedCaseHashes -DifferenceObject $actualCaseHashes |
                        Where-Object SideIndicator -eq '<='
                ).Count
                $unexpectedFixedCases = @(
                    Compare-Object -ReferenceObject $fixedCaseHashes -DifferenceObject $actualCaseHashes |
                        Where-Object SideIndicator -eq '=>'
                ).Count
                $testGateFailures.Add(
                    "$($testProject.BaseName)：实际用例与固定哈希清单不一致；missing=$missingFixedCases，unexpected=$unexpectedFixedCases。只有显式审查并更新 eng/expected-test-dataset.json 才能重建基线。")
            }

            $total = [int] $assemblyNode.GetAttribute('total')
            $passed = [int] $assemblyNode.GetAttribute('passed')
            $failed = [int] $assemblyNode.GetAttribute('failed')
            $skipped = [int] $assemblyNode.GetAttribute('skipped')
            $notRun = [int] $assemblyNode.GetAttribute('not-run')
            if ($total -ne $actualTests.Count -or $passed -ne $total -or $failed -ne 0 -or $skipped -ne 0 -or $notRun -ne 0) {
                $testGateFailures.Add(
                    "$($testProject.BaseName)：测试结果不完整；total=$total，passed=$passed，failed=$failed，skipped=$skipped，notRun=$notRun。")
            }

            $evidenceFileName = "$($testProject.BaseName).test-evidence.json"
            $assemblyEvidence = [ordered]@{
                schemaVersion = 1
                assembly = $testProject.BaseName
                expected = $fixedCaseHashes.Count
                discovered = $expectedTests.Count
                total = $total
                passed = $passed
                failed = $failed
                skipped = $skipped
                notRun = $notRun
                triggerTags = @(
                    $caseEvidence |
                        ForEach-Object { $_.triggerTags } |
                        ForEach-Object { $_ } |
                        Sort-Object -Unique
                )
                expectedDataset = @(
                    $expectedTests |
                        ForEach-Object {
                            [ordered]@{
                                id = [string] $_.ID
                                nameSha256 = Get-DshTextSha256 -Text ([string] $_.DisplayName)
                                class = [string] $_.Class
                                method = [string] $_.Method
                                explicit = [bool] $_.Explicit
                            }
                        } |
                        Sort-Object id
                )
                tests = @($caseEvidence | Sort-Object sequence)
            }
            $assemblyEvidence | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath (Join-Path $ArtifactsDirectory $evidenceFileName) -Encoding utf8NoBOM
            $testAssemblyResults.Add([ordered]@{
                assembly = $testProject.BaseName
                expected = $fixedCaseHashes.Count
                discovered = $expectedTests.Count
                total = $total
                passed = $passed
                failed = $failed
                skipped = $skipped
                notRun = $notRun
                evidenceFile = $evidenceFileName
            })

            $safeLog = "assembly=$($testProject.BaseName) expected=$($fixedCaseHashes.Count) discovered=$($expectedTests.Count) total=$total passed=$passed failed=$failed skipped=$skipped notRun=$notRun"
            $safeLog | Set-Content `
                -LiteralPath (Join-Path $ArtifactsDirectory "$($testProject.BaseName).mtp.log") `
                -Encoding utf8NoBOM
            Write-Host $safeLog
        }
        finally {
            if (Test-Path -LiteralPath $rawResultPath -PathType Leaf) {
                [IO.File]::Delete($rawResultPath)
            }
        }
    }

    $requiredVfyTags = @('VFY-01', 'VFY-02', 'VFY-03', 'VFY-04', 'VFY-05', 'VFY-06', 'VFY-07', 'VFY-08')
    $missingVfyTags = @($requiredVfyTags | Where-Object { -not $observedVfyTags.Contains($_) })
    if ($missingVfyTags.Count -gt 0) {
        $testGateFailures.Add("测试元数据未覆盖稳定组：$($missingVfyTags -join ', ')")
    }

    Write-Host '[VFY-04] required fixture inventory'
    Assert-DshFixtureInventory -RepositoryRoot $repositoryRoot -OutputDirectory $ArtifactsDirectory
    $completedGates.Add('required-fixture-inventory')

    if ($testGateFailures.Count -gt 0) {
        throw "测试证据门禁未满足：`n$($testGateFailures -join [Environment]::NewLine)"
    }
    $completedGates.Add('expected-test-dataset')
    $completedGates.Add('fixed-test-dataset-baseline')
    $completedGates.Add('test-execution-no-skip')
    $completedGates.Add('test-trigger-tags')

    Write-Host '[VFY-01/VFY-07/VFY-08] architecture constraints'
    $architectureViolations = @(Get-DshArchitectureViolations -RepositoryRoot $repositoryRoot)
    if ($architectureViolations.Count -gt 0) {
        throw "架构约束失败：`n$($architectureViolations -join [Environment]::NewLine)"
    }
    $completedGates.Add('architecture-constraints')

    Write-Host '[VFY-08] dependency vulnerability and deprecation scan'
    $vulnerableJson = Invoke-DshNativeCapture -FilePath $dotnet -Arguments @(
        'list', $solution, 'package', '--vulnerable', '--include-transitive', '--format', 'json', '--output-version', '1'
    ) -WorkingDirectory $repositoryRoot
    Assert-DshPackageReportClean -Json $vulnerableJson -Kind vulnerable

    $deprecatedJson = Invoke-DshNativeCapture -FilePath $dotnet -Arguments @(
        'list', $solution, 'package', '--deprecated', '--include-transitive', '--format', 'json', '--output-version', '1'
    ) -WorkingDirectory $repositoryRoot
    Assert-DshPackageReportClean -Json $deprecatedJson -Kind deprecated
    $completedGates.Add('dependency-vulnerability-deprecation-scan')

    Write-Host '[VFY-08] third-party licenses and SPDX 2.3 SBOM'
    $supplyChainArtifacts = New-DshSupplyChainArtifacts `
        -RepositoryRoot $repositoryRoot `
        -OutputDirectory $ArtifactsDirectory `
        -ProductName $constants.product.name `
        -Version $constants.product.version `
        -PairingIdentity $pairingIdentity
    Assert-DshSbomPairingIdentity `
        -Path $supplyChainArtifacts.SbomPath `
        -PairingIdentity $pairingIdentity
    Write-Host "[VFY-08] SBOM packages: $($supplyChainArtifacts.PackageCount)"
    $completedGates.Add('licenses-and-spdx-sbom')

    Write-Host '[VFY-08] repository secret scan'
    $secretFindings = @(Find-DshSecretFindings -RepositoryRoot $repositoryRoot)
    if ($secretFindings.Count -gt 0) {
        throw "秘密扫描命中：`n$($secretFindings -join [Environment]::NewLine)"
    }
    $completedGates.Add('repository-secret-scan')

    foreach ($tag in $requiredVfyTags) {
        $null = $verifiedTriggerTags.Add($tag)
    }

    $sourceState = Get-DshSourceState -RepositoryRoot $repositoryRoot

    $resultFiles = @(
        Get-ChildItem -LiteralPath $ArtifactsDirectory -File -Recurse |
            Where-Object { $_.Name -ne 'verify-summary.json' } |
            ForEach-Object {
                [ordered]@{
                    path = [IO.Path]::GetRelativePath($ArtifactsDirectory, $_.FullName)
                    sha256 = Get-DshSha256 -Path $_.FullName
                }
            }
    )
    $summary = [ordered]@{
        schemaVersion = 1
        startedAtUtc = $startedAtUtc.ToString('O')
        endedAtUtc = [DateTime]::UtcNow.ToString('O')
        result = 'PASS'
        dotnetSdkVersion = $constants.build.dotnetSdkVersion
        runtimeIdentifier = $constants.build.runtimeIdentifier
        releaseStatus = $constants.releaseStatus
        pairingIdentity = $pairingIdentity
        verificationImpact = $verificationImpact
        source = [ordered]@{
            available = $sourceState.Available
            commit = $sourceState.Commit
            worktreeState = $sourceState.WorktreeState
        }
        executedGates = @($completedGates)
        triggerTags = @(@(
                $verifiedTriggerTags
                $verificationImpact.triggerTags
            ) | Sort-Object -Unique)
        requiredRs = @($verificationImpact.requiredRs)
        testAssemblies = @($testAssemblyResults)
        resultFiles = $resultFiles
    }
    $summary | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath (Join-Path $ArtifactsDirectory 'verify-summary.json') -Encoding utf8NoBOM

    Write-Host 'verify.ps1: PASS'
}
catch {
    if ($null -ne $resolvedArtifactsDirectory) {
        $failureMessage = $_.Exception.Message
        if (-not (Test-DshEvidenceTextSafe -Text $failureMessage)) {
            $failureMessage = '失败详情包含禁止进入持久证据的敏感形式，已脱敏；仅查看当前临时控制台输出。'
        }
        $failureTriggerTags = @($verifiedTriggerTags)
        $failureRequiredRs = @()
        if ($null -ne $verificationImpact) {
            $failureTriggerTags = @(@(
                    $failureTriggerTags
                    $verificationImpact.triggerTags
                ) | Sort-Object -Unique)
            $failureRequiredRs = @($verificationImpact.requiredRs)
        }
        $summary = [ordered]@{
            schemaVersion = 1
            startedAtUtc = $startedAtUtc.ToString('O')
            endedAtUtc = [DateTime]::UtcNow.ToString('O')
            result = 'FAIL'
            failure = $failureMessage
            executedGates = @($completedGates)
            triggerTags = $failureTriggerTags
            requiredRs = $failureRequiredRs
            pairingIdentity = $pairingIdentity
            verificationImpact = $verificationImpact
            testAssemblies = @($testAssemblyResults)
        }
        $summary | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath (Join-Path $resolvedArtifactsDirectory 'verify-summary.json') -Encoding utf8NoBOM
    }
    Write-Error "verify.ps1: FAIL`n$($_.Exception.Message)"
    exit 1
}
