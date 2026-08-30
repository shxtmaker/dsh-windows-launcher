Set-StrictMode -Version Latest

function Test-DshSemVer {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string] $Version
    )

    if ([string]::IsNullOrWhiteSpace($Version)) {
        return $false
    }

    $match = [regex]::Match(
        $Version,
        '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$')
    if (-not $match.Success) {
        return $false
    }
    foreach ($identifier in $match.Groups['prerelease'].Value.Split('.', [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($identifier -match '^[0-9]+$' -and $identifier.Length -gt 1 -and $identifier[0] -eq '0') {
            return $false
        }
    }
    return $true
}

function ConvertTo-DshSemanticVersionParts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Version
    )

    if (-not (Test-DshSemVer -Version $Version)) {
        throw "无效 SemVer：$Version"
    }

    $match = [regex]::Match(
        $Version,
        '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$')

    return [pscustomobject]@{
        Major      = [int64] $match.Groups['major'].Value
        Minor      = [int64] $match.Groups['minor'].Value
        Patch      = [int64] $match.Groups['patch'].Value
        Prerelease = $match.Groups['prerelease'].Value
        Build       = $match.Groups['build'].Value
    }
}

function Compare-DshSemVer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Left,

        [Parameter(Mandatory)]
        [string] $Right
    )

    $leftParts = ConvertTo-DshSemanticVersionParts -Version $Left
    $rightParts = ConvertTo-DshSemanticVersionParts -Version $Right

    foreach ($name in @('Major', 'Minor', 'Patch')) {
        if ($leftParts.$name -lt $rightParts.$name) {
            return -1
        }

        if ($leftParts.$name -gt $rightParts.$name) {
            return 1
        }
    }

    if ([string]::IsNullOrEmpty($leftParts.Prerelease) -and [string]::IsNullOrEmpty($rightParts.Prerelease)) {
        return 0
    }

    if ([string]::IsNullOrEmpty($leftParts.Prerelease)) {
        return 1
    }

    if ([string]::IsNullOrEmpty($rightParts.Prerelease)) {
        return -1
    }

    $leftIdentifiers = $leftParts.Prerelease.Split('.')
    $rightIdentifiers = $rightParts.Prerelease.Split('.')
    $count = [Math]::Min($leftIdentifiers.Count, $rightIdentifiers.Count)

    for ($index = 0; $index -lt $count; $index++) {
        $leftIdentifier = $leftIdentifiers[$index]
        $rightIdentifier = $rightIdentifiers[$index]
        $leftNumeric = $leftIdentifier -match '^(0|[1-9][0-9]*)$'
        $rightNumeric = $rightIdentifier -match '^(0|[1-9][0-9]*)$'

        if ($leftNumeric -and $rightNumeric) {
            $leftNumber = [System.Numerics.BigInteger]::Parse($leftIdentifier)
            $rightNumber = [System.Numerics.BigInteger]::Parse($rightIdentifier)
            if ($leftNumber -lt $rightNumber) {
                return -1
            }

            if ($leftNumber -gt $rightNumber) {
                return 1
            }

            continue
        }

        if ($leftNumeric) {
            return -1
        }

        if ($rightNumeric) {
            return 1
        }

        $comparison = [string]::CompareOrdinal($leftIdentifier, $rightIdentifier)
        if ($comparison -lt 0) {
            return -1
        }

        if ($comparison -gt 0) {
            return 1
        }
    }

    return $leftIdentifiers.Count.CompareTo($rightIdentifiers.Count)
}

function ConvertTo-DshFileVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Version
    )

    $parts = ConvertTo-DshSemanticVersionParts -Version $Version
    foreach ($component in @($parts.Major, $parts.Minor, $parts.Patch)) {
        if ($component -gt 65535) {
            throw "SemVer 组件超过 Windows 文件版本上限：$Version"
        }
    }

    return '{0}.{1}.{2}.0' -f $parts.Major, $parts.Minor, $parts.Patch
}

function Get-DshSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
}

function Get-DshReleaseConstants {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $path = Join-Path $RepositoryRoot 'eng/release-constants.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "发布常量不存在：$path"
    }

    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
}

function Get-DshEmptyValuePaths {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object] $InputObject,

        [string] $Path = '$'
    )

    $findings = [System.Collections.Generic.List[string]]::new()

    function Visit-DshValue {
        param(
            [AllowNull()]
            [object] $Value,
            [string] $CurrentPath
        )

        if ($null -eq $Value) {
            $findings.Add($CurrentPath)
            return
        }

        if ($Value -is [string]) {
            if ([string]::IsNullOrWhiteSpace($Value)) {
                $findings.Add($CurrentPath)
            }

            return
        }

        if ($Value -is [System.Collections.IDictionary]) {
            foreach ($key in $Value.Keys) {
                Visit-DshValue -Value $Value[$key] -CurrentPath "$CurrentPath.$key"
            }

            return
        }

        if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [pscustomobject]) {
            $index = 0
            foreach ($item in $Value) {
                Visit-DshValue -Value $item -CurrentPath "$CurrentPath[$index]"
                $index++
            }

            return
        }

        foreach ($property in $Value.PSObject.Properties) {
            Visit-DshValue -Value $property.Value -CurrentPath "$CurrentPath.$($property.Name)"
        }
    }

    Visit-DshValue -Value $InputObject -CurrentPath $Path
    return $findings.ToArray()
}

function Assert-DshHarnessFingerprintBaseline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory)]
        [pscustomobject] $Constants
    )

    $fingerprint = $Constants.dependencyBaseline.harness.probeFingerprint
    $expected = @(
        [pscustomobject]@{
            Name = 'api'
            SourcePath = 'packages/client/connection/src/index.ts'
            RequestPath = '/api'
            StatusCode = 401
            BodyLength = 12
            BodySha256 = 'e9d83f01c9aff03af6380e341aad90a5547d378ef54582383c6c9a35c53181af'
        },
        [pscustomobject]@{
            Name = 'root'
            SourcePath = 'packages/client/connection/src/browser-auth.ts'
            RequestPath = '/'
            StatusCode = 401
            BodyLength = 68
            BodySha256 = '3aad6226baa021e747c19d2c55cbd7a5995d10f43c1d05b248af37836942acaf'
        }
    )
    foreach ($item in $expected) {
        $actual = $fingerprint.($item.Name)
        if ($actual.sourcePath -ne $item.SourcePath -or
            $actual.requestPath -ne $item.RequestPath -or
            $actual.statusCode -ne $item.StatusCode -or
            $actual.bodyLength -ne $item.BodyLength -or
            $actual.bodySha256 -ne $item.BodySha256) {
            throw "Harness $($item.Name) 401 指纹偏离固定源码基线。"
        }
    }

    $implementationPath = Join-Path $RepositoryRoot 'src/DshLauncher.Platform.Windows/HarnessTargetProbePort.cs'
    $implementation = Get-Content -LiteralPath $implementationPath -Raw -Encoding UTF8
    foreach ($fragment in @(
        'ApiStatusCode: 401',
        'ApiBodyLength: 12',
        'ApiBodySha256: "e9d83f01c9aff03af6380e341aad90a5547d378ef54582383c6c9a35c53181af"',
        'RootStatusCode: 401',
        'RootBodyLength: 68',
        'RootBodySha256: "3aad6226baa021e747c19d2c55cbd7a5995d10f43c1d05b248af37836942acaf"'
    )) {
        if (-not $implementation.Contains($fragment, [StringComparison]::Ordinal)) {
            throw "Harness 401 指纹实现未与发布常量对齐：$fragment"
        }
    }

    $legacy = $Constants.dependencyBaseline.harness.legacyUnauthenticatedBaseline
    $legacyApi = $legacy.probeFingerprint.api
    $legacyRoot = $legacy.probeFingerprint.root
    if ($legacy.package -cne '@deepseek-ai/dsh' -or
        $legacy.version -cne '0.1.1-rc.2' -or
        $legacyApi.requestPath -cne '/api' -or
        $legacyApi.statusCode -ne 404 -or
        $legacyApi.bodyLength -ne 9 -or
        $legacyApi.bodySha256 -cne '907ba78b4545338d3539683e63ecb51cf51c10adc9dabd86e92bd52339f298b9' -or
        $legacyRoot.requestPath -cne '/' -or
        $legacyRoot.statusCode -ne 200 -or
        $legacyRoot.bodyLength -ne 14556 -or
        $legacyRoot.bodySha256 -cne 'a1c9e8d395d34fc83466b19d652a36e94b4f643fafef57f840b226b0bfab74de') {
        throw '旧版无认证 Harness 指纹偏离固定 npm 基线。'
    }

    foreach ($fragment in @(
        'ApiStatusCode: 404',
        'ApiBodyLength: 9',
        'ApiBodySha256: "907ba78b4545338d3539683e63ecb51cf51c10adc9dabd86e92bd52339f298b9"',
        'RootStatusCode: 200',
        'RootBodyLength: 14556',
        'RootBodySha256: "a1c9e8d395d34fc83466b19d652a36e94b4f643fafef57f840b226b0bfab74de"'
    )) {
        if (-not $implementation.Contains($fragment, [StringComparison]::Ordinal)) {
            throw "旧版无认证 Harness 指纹实现未与发布常量对齐：$fragment"
        }
    }
}

function Assert-DshReleaseConstants {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory)]
        [pscustomobject] $Constants,

        [switch] $RequireCandidate
    )

    $constantsPath = Join-Path $RepositoryRoot 'eng/release-constants.json'
    $schemaPath = Join-Path $RepositoryRoot 'eng/release-constants.schema.json'
    $testJson = Get-Command -Name Test-Json -ErrorAction SilentlyContinue
    if ($null -ne $testJson) {
        $json = Get-Content -LiteralPath $constantsPath -Raw -Encoding UTF8
        if (-not ($json | Test-Json -SchemaFile $schemaPath -ErrorAction Stop)) {
            throw '发布常量未通过 JSON Schema。'
        }
    }

    if ($Constants.schemaVersion -ne 1) {
        throw "未知发布常量模式：$($Constants.schemaVersion)"
    }

    if (-not (Test-DshSemVer -Version $Constants.product.version)) {
        throw "发布常量中的产品版本不是 SemVer：$($Constants.product.version)"
    }

    if ($Constants.build.dotnetSdkVersion -ne '10.0.400' -or
        $Constants.build.targetFramework -ne 'net10.0-windows' -or
        $Constants.build.runtimeIdentifier -ne 'win-x64' -or
        $Constants.build.selfContained -ne $true -or
        $Constants.build.singleFile -ne $false -or
        $Constants.build.trimmed -ne $false) {
        throw '发布常量中的构建身份偏离 V1 固定基线。'
    }

    if ($Constants.dependencyBaseline.harness.commit -ne 'cd5ef8148158c3a752a658978873241fdf8e2bbc' -or
        $Constants.dependencyBaseline.harness.version -ne 'dsh-v0.1.2-alpha.1') {
        throw '发布常量中的 Harness 源码身份偏离 V1 固定基线。'
    }
    Assert-DshHarnessFingerprintBaseline -RepositoryRoot $RepositoryRoot -Constants $Constants

    if ($Constants.dependencyBaseline.webView2Sdk.version -ne '1.0.4129.50' -or
        $Constants.dependencyBaseline.webView2Runtime.minimumVersion -ne '151.0.4129.50' -or
        $Constants.dependencyBaseline.webView2Runtime.offlineInstallerVersion -ne '1.3.263.3' -or
        $Constants.dependencyBaseline.webView2Runtime.bootstrapper.version -ne '1.3.263.3' -or
        $Constants.dependencyBaseline.webView2Runtime.bootstrapper.sourceUri -ne 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -or
        $Constants.dependencyBaseline.webView2Runtime.bootstrapper.sha256 -ne '94314d8b20c8a370df81c5cc3d8d7a3e23fe5de14ef5e988229ff3208e449146' -or
        $Constants.distribution.innoSetup.version -ne '7.0.2') {
        throw '发布常量中的 WebView2 或 Inno Setup 版本偏离 V1 固定基线。'
    }

    $candidateRequired = $RequireCandidate -or $Constants.releaseStatus -eq 'candidate'
    if ($candidateRequired) {
        if ($Constants.releaseStatus -ne 'candidate') {
            throw "正式候选要求 releaseStatus=candidate，当前值为 $($Constants.releaseStatus)。"
        }

        $emptyPaths = @(Get-DshEmptyValuePaths -InputObject $Constants)
        if ($emptyPaths.Count -gt 0) {
            throw "正式候选仍有空发布常量：$($emptyPaths -join ', ')"
        }

        if ($Constants.distribution.officialReleaseUri -notmatch '^https://') {
            throw '正式发布地址必须使用 HTTPS。'
        }

        if ($Constants.distribution.signing.timestampServerUri -notmatch '^https://') {
            throw '时间戳服务必须使用 HTTPS。'
        }
    }
}

function Get-DshExactDotNetPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ExpectedVersion,

        [AllowNull()]
        [string] $DotNetPath
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
        $candidates.Add($DotNetPath)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:DSHWL_DOTNET_EXE)) {
        $candidates.Add($env:DSHWL_DOTNET_EXE)
    }
    else {
        if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            $candidates.Add((Join-Path $env:LOCALAPPDATA "DshWindowsLauncherDev\dotnet-$ExpectedVersion\dotnet.exe"))
        }

        $command = Get-Command -Name dotnet -CommandType Application -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $candidates.Add($command.Source)
        }
    }

    $observed = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $observed.Add("$candidate (不存在)")
            continue
        }

        try {
            $actualVersion = Invoke-DshNativeCapture `
                -FilePath $candidate `
                -Arguments @('--version') `
                -WorkingDirectory (Split-Path -Parent $candidate)
            $actualVersion = $actualVersion.Trim()
        }
        catch {
            $observed.Add("$candidate (无法执行)")
            continue
        }
        if ($actualVersion -eq $ExpectedVersion) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }

        $observed.Add("$candidate ($actualVersion)")
    }

    throw "找不到精确 .NET SDK $ExpectedVersion。已检查：$($observed -join '; ')"
}

function Invoke-DshNative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [Parameter(Mandatory)]
        [string] $WorkingDirectory
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $false
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "无法启动命令：$FilePath"
        }
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        if (-not [string]::IsNullOrEmpty($standardOutput)) {
            Write-Host -NoNewline $standardOutput
        }
        if (-not [string]::IsNullOrEmpty($standardError)) {
            Write-Host -NoNewline $standardError
        }
        if ($process.ExitCode -ne 0) {
            throw "命令失败，退出码 $($process.ExitCode)：$FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-DshNativeCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [Parameter(Mandatory)]
        [string] $WorkingDirectory,

        [Text.Encoding] $OutputEncoding
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($null -ne $OutputEncoding) {
        $startInfo.StandardOutputEncoding = $OutputEncoding
        $startInfo.StandardErrorEncoding = $OutputEncoding
    }
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "无法启动命令：$FilePath"
        }
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "命令失败，退出码 $($process.ExitCode)：$FilePath $($Arguments -join ' ')`n$standardOutput`n$standardError"
        }

        return ($standardOutput + $standardError).TrimEnd()
    }
    finally {
        $process.Dispose()
    }
}

function Get-DshSourceState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $git = Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        return [pscustomobject]@{
            Available = $false
            Commit = $null
            WorktreeState = 'git-unavailable'
        }
    }

    try {
        $insideWorktree = Invoke-DshNativeCapture `
            -FilePath $git.Source `
            -Arguments @('-C', $RepositoryRoot, 'rev-parse', '--is-inside-work-tree') `
            -WorkingDirectory $RepositoryRoot
        if ($insideWorktree.Trim() -cne 'true') {
            throw 'not a Git worktree'
        }

        $commit = (Invoke-DshNativeCapture `
            -FilePath $git.Source `
            -Arguments @('-C', $RepositoryRoot, 'rev-parse', '--verify', 'HEAD') `
            -WorkingDirectory $RepositoryRoot).Trim().ToLowerInvariant()
        if ($commit -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
            throw 'invalid Git object id'
        }

        $status = (Invoke-DshNativeCapture `
            -FilePath $git.Source `
            -Arguments @('-C', $RepositoryRoot, 'status', '--porcelain=v1', '--untracked-files=normal') `
            -WorkingDirectory $RepositoryRoot).Trim()
        return [pscustomobject]@{
            Available = $true
            Commit = $commit
            WorktreeState = if ([string]::IsNullOrWhiteSpace($status)) { 'clean' } else { 'dirty' }
        }
    }
    catch {
        return [pscustomobject]@{
            Available = $false
            Commit = $null
            WorktreeState = 'not-a-git-worktree'
        }
    }
}

function Get-DshArchitectureViolations {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $violations = [System.Collections.Generic.List[string]]::new()
    $coreProject = Join-Path $RepositoryRoot 'src/DshLauncher.Core/DshLauncher.Core.csproj'
    [xml] $coreXml = Get-Content -LiteralPath $coreProject -Raw -Encoding UTF8

    foreach ($nodeName in @('ProjectReference', 'PackageReference', 'FrameworkReference')) {
        $nodes = @($coreXml.SelectNodes("//$nodeName"))
        if ($nodes.Count -gt 0) {
            $violations.Add("Core 不得包含 $nodeName。")
        }
    }

    if (@($coreXml.SelectNodes('//UseWPF[text()="true"]')).Count -gt 0) {
        $violations.Add('Core 不得启用 WPF。')
    }

    $forbiddenPatterns = @(
        '(?m)^\s*using\s+System\.Windows(?:\.|;)',
        '(?m)^\s*using\s+Microsoft\.Web\.WebView2(?:\.|;)',
        '(?m)^\s*using\s+DshLauncher\.Platform\.Windows(?:\.|;)',
        '(?m)^\s*using\s+Microsoft\.Win32(?:\.|;)'
    )
    foreach ($source in Get-ChildItem -LiteralPath (Split-Path -Parent $coreProject) -Filter '*.cs' -File -Recurse -ErrorAction Stop) {
        if ($source.FullName -match '[\\/](?:bin|obj)[\\/]') {
            continue
        }

        $content = Get-Content -LiteralPath $source.FullName -Raw -Encoding UTF8
        foreach ($pattern in $forbiddenPatterns) {
            if ($content -match $pattern) {
                $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $source.FullName)
                $violations.Add("Core 源码引用了禁止的 Windows/WPF/WebView 命名空间：$relative")
            }
        }
    }

    $expectedReferences = @{
        'DshLauncher.Core'             = @()
        'DshLauncher.WebView'          = @('DshLauncher.Core')
        'DshLauncher.Platform.Windows' = @('DshLauncher.Core')
        'DshLauncher.Desktop'          = @('DshLauncher.Core', 'DshLauncher.Platform.Windows', 'DshLauncher.WebView')
    }

    foreach ($projectName in $expectedReferences.Keys) {
        $projectPath = Join-Path $RepositoryRoot "src/$projectName/$projectName.csproj"
        [xml] $projectXml = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
        $actual = @(
            $projectXml.SelectNodes('//ProjectReference') |
                ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) } |
                Sort-Object -Unique
        )
        $expected = @($expectedReferences[$projectName] | Sort-Object)
        if (($actual -join '|') -ne ($expected -join '|')) {
            $violations.Add("$projectName 项目引用不符合固定依赖图。实际：$($actual -join ', ')；预期：$($expected -join ', ')")
        }
    }

    return $violations.ToArray()
}

function Find-DshSecretFindings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $patterns = [ordered]@{
        PrivateKey    = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        GitHubToken   = '\bgh[pousr]_[A-Za-z0-9]{30,}\b'
        AwsAccessKey  = '\bAKIA[0-9A-Z]{16}\b'
        Jwt           = '\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\b'
        AssignedSecret = '(?i)\b(?:api[_-]?key|client[_-]?secret|password|access[_-]?token)\b\s*[:=]\s*["''][^"''\r\n]{16,}["'']'
    }
    $extensions = @(
        '.cs', '.csproj', '.json', '.md', '.props', '.ps1', '.iss', '.config',
        '.xml', '.xaml', '.yml', '.yaml', '.py', '.sh', '.sln', '.slnx', '.txt',
        '.toml', '.ini'
    )
    $findings = [System.Collections.Generic.List[string]]::new()

    foreach ($file in Get-ChildItem -LiteralPath $RepositoryRoot -File -Recurse -ErrorAction Stop) {
        $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName)
        if ($relative -match '(^|[\\/])(?:\.git|\.scratch|bin|obj|artifacts|TestResults)([\\/]|$)' -or
            $extensions -notcontains $file.Extension.ToLowerInvariant() -or
            $file.Length -gt 2MB) {
            continue
        }

        $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        foreach ($entry in $patterns.GetEnumerator()) {
            if ($content -match $entry.Value) {
                $findings.Add("$($entry.Key): $relative")
            }
        }
    }

    return $findings.ToArray()
}

function Get-DshLockedPackageInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $packages = @{}
    $lockFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src'), (Join-Path $RepositoryRoot 'tests') `
            -Filter 'packages.lock.json' -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' }
    )
    if ($lockFiles.Count -ne 8) {
        throw "许可证与 SBOM 输入要求 8 个锁文件，实际为 $($lockFiles.Count)。"
    }

    foreach ($lockFile in $lockFiles) {
        $relativeLockPath = [IO.Path]::GetRelativePath($RepositoryRoot, $lockFile.FullName)
        $lock = Get-Content -LiteralPath $lockFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
        foreach ($framework in $lock.dependencies.PSObject.Properties) {
            foreach ($packageProperty in $framework.Value.PSObject.Properties) {
                $details = $packageProperty.Value
                if ($details.type -eq 'Project') {
                    continue
                }
                if ([string]::IsNullOrWhiteSpace($details.resolved) -or
                    [string]::IsNullOrWhiteSpace($details.contentHash)) {
                    throw "锁文件包缺少 resolved/contentHash：$relativeLockPath；$($packageProperty.Name)"
                }

                $key = "$($packageProperty.Name.ToLowerInvariant())|$($details.resolved.ToLowerInvariant())"
                if (-not $packages.ContainsKey($key)) {
                    $packages[$key] = [ordered]@{
                        Id = $packageProperty.Name
                        Version = $details.resolved
                        ContentHash = $details.contentHash
                        Types = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                        LockFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                    }
                }
                elseif ($packages[$key].ContentHash -ne $details.contentHash) {
                    throw "同一 NuGet 包版本出现不同 contentHash：$($packageProperty.Name) $($details.resolved)"
                }

                $null = $packages[$key].Types.Add([string] $details.type)
                $null = $packages[$key].LockFiles.Add($relativeLockPath)
            }
        }
    }

    return @(
        $packages.Values |
            ForEach-Object {
                [pscustomobject]@{
                    Id = $_.Id
                    Version = $_.Version
                    ContentHash = $_.ContentHash
                    Direct = $_.Types.Contains('Direct') -or $_.Types.Contains('CentralTransitive')
                    Scope = if (@($_.LockFiles | Where-Object { $_ -match '^src[\\/]' }).Count -gt 0) { 'runtime' } else { 'development' }
                    Types = @($_.Types | Sort-Object)
                    LockFiles = @($_.LockFiles | Sort-Object)
                }
            } |
            Sort-Object Id, Version
    )
}

function Get-DshNuGetGlobalPackagesFolder {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $assetsFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src'), (Join-Path $RepositoryRoot 'tests') `
            -Filter 'project.assets.json' -File -Recurse |
            Where-Object { $_.FullName -match '[\\/]obj[\\/]project\.assets\.json$' }
    )
    if ($assetsFiles.Count -ne 8) {
        throw "许可证与 SBOM 生成要求 8 个还原资产文件，实际为 $($assetsFiles.Count)。"
    }
    $folders = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($assetsFile in $assetsFiles) {
        $assets = Get-Content -LiteralPath $assetsFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 100
        foreach ($folderProperty in $assets.packageFolders.PSObject.Properties) {
            if (Test-Path -LiteralPath $folderProperty.Name -PathType Container) {
                $null = $folders.Add((Resolve-Path -LiteralPath $folderProperty.Name).Path)
            }
        }
    }

    if ($folders.Count -ne 1) {
        throw "无法从还原资产唯一确定 NuGet global-packages 目录。已发现：$(@($folders) -join ', ')"
    }
    return @($folders)[0]
}

function Get-DshNuGetPackageMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $GlobalPackagesFolder,

        [Parameter(Mandatory)]
        [pscustomobject] $Package
    )

    $packageDirectory = Join-Path $GlobalPackagesFolder "$($Package.Id.ToLowerInvariant())\$($Package.Version.ToLowerInvariant())"
    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
        throw "NuGet 缓存缺少锁定包：$($Package.Id) $($Package.Version)"
    }
    $nuspecFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File)
    if ($nuspecFiles.Count -ne 1) {
        throw "NuGet 包必须有且只有一个 nuspec：$($Package.Id) $($Package.Version)"
    }

    [xml] $nuspec = Get-Content -LiteralPath $nuspecFiles[0].FullName -Raw -Encoding UTF8
    $licenseNode = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="license"]')
    $licenseUrlNode = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="licenseUrl"]')
    $projectUrlNode = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="projectUrl"]')
    $repositoryNode = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="repository"]')

    $licenseExpression = $null
    $licenseFile = $null
    $licenseFileSha256 = $null
    if ($null -ne $licenseNode) {
        $licenseType = $licenseNode.Attributes['type']
        if ($null -ne $licenseType -and $licenseType.Value -eq 'expression') {
            $licenseExpression = $licenseNode.InnerText.Trim()
        }
        elseif ($null -ne $licenseType -and $licenseType.Value -eq 'file') {
            $licenseFile = $licenseNode.InnerText.Trim()
            $licenseFilePath = Join-Path $packageDirectory $licenseFile
            if (-not (Test-Path -LiteralPath $licenseFilePath -PathType Leaf)) {
                throw "NuGet 包声明的许可证文件不存在：$($Package.Id) $($Package.Version)；$licenseFile"
            }
            $licenseFileSha256 = Get-DshSha256 -Path $licenseFilePath
        }
    }
    $licenseUrl = if ($null -eq $licenseUrlNode) { $null } else { $licenseUrlNode.InnerText.Trim() }
    if ([string]::IsNullOrWhiteSpace($licenseExpression) -and
        [string]::IsNullOrWhiteSpace($licenseFile) -and
        [string]::IsNullOrWhiteSpace($licenseUrl)) {
        throw "NuGet 包没有许可证声明：$($Package.Id) $($Package.Version)"
    }

    return [pscustomobject]@{
        Id = $Package.Id
        Version = $Package.Version
        Direct = $Package.Direct
        Scope = $Package.Scope
        ContentHash = $Package.ContentHash
        LicenseExpression = $licenseExpression
        LicenseFile = $licenseFile
        LicenseFileSha256 = $licenseFileSha256
        LicenseUrl = $licenseUrl
        ProjectUrl = if ($null -eq $projectUrlNode) { $null } else { $projectUrlNode.InnerText.Trim() }
        RepositoryUrl = if ($null -eq $repositoryNode) { $null } else { $repositoryNode.GetAttribute('url') }
        LockFiles = $Package.LockFiles
    }
}

function New-DshSupplyChainArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory)]
        [string] $OutputDirectory,

        [Parameter(Mandatory)]
        [string] $ProductName,

        [Parameter(Mandatory)]
        [string] $Version
    )

    if (-not (Test-DshSemVer -Version $Version)) {
        throw "SBOM 产品版本不是 SemVer：$Version"
    }
    $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
    $inventory = @(Get-DshLockedPackageInventory -RepositoryRoot $RepositoryRoot)
    if ($inventory.Count -eq 0) {
        throw '锁文件没有可进入许可证清单和 SBOM 的 NuGet 包。'
    }
    $globalPackagesFolder = Get-DshNuGetGlobalPackagesFolder -RepositoryRoot $RepositoryRoot
    $metadata = @(
        foreach ($package in $inventory) {
            Get-DshNuGetPackageMetadata -GlobalPackagesFolder $globalPackagesFolder -Package $package
        }
    )

    $createdAtUtc = [DateTime]::UtcNow.ToString(
        'yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture)
    $licensePath = Join-Path $OutputDirectory 'third-party-licenses.json'
    [ordered]@{
        schemaVersion = 1
        generatedAtUtc = $createdAtUtc
        source = 'NuGet packages.lock.json and restored nuspec metadata'
        packages = $metadata
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $licensePath -Encoding utf8NoBOM

    $rootSpdxId = 'SPDXRef-Application'
    $spdxPackages = [System.Collections.Generic.List[object]]::new()
    $spdxPackages.Add([ordered]@{
        name = $ProductName
        SPDXID = $rootSpdxId
        versionInfo = $Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
    })
    $relationships = [System.Collections.Generic.List[object]]::new()
    foreach ($package in $metadata) {
        try {
            $sha512 = [Convert]::ToHexString([Convert]::FromBase64String($package.ContentHash))
        }
        catch {
            throw "NuGet contentHash 不是有效 SHA-512 Base64：$($package.Id) $($package.Version)"
        }
        $spdxId = 'SPDXRef-Package-' + (($package.Id + '-' + $package.Version) -replace '[^A-Za-z0-9.-]', '-')
        $spdxPackages.Add([ordered]@{
            name = $package.Id
            SPDXID = $spdxId
            versionInfo = $package.Version
            downloadLocation = "https://api.nuget.org/v3-flatcontainer/$($package.Id.ToLowerInvariant())/$($package.Version.ToLowerInvariant())/$($package.Id.ToLowerInvariant()).$($package.Version.ToLowerInvariant()).nupkg"
            filesAnalyzed = $false
            checksums = @([ordered]@{ algorithm = 'SHA512'; checksumValue = $sha512 })
            licenseConcluded = 'NOASSERTION'
            licenseDeclared = if ([string]::IsNullOrWhiteSpace($package.LicenseExpression)) { 'NOASSERTION' } else { $package.LicenseExpression }
            copyrightText = 'NOASSERTION'
            externalRefs = @([ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = "pkg:nuget/$($package.Id)@$($package.Version)"
            })
        })
        $relationships.Add([ordered]@{
            spdxElementId = if ($package.Scope -eq 'runtime') { $rootSpdxId } else { $spdxId }
            relationshipType = if ($package.Scope -eq 'runtime') { 'DEPENDS_ON' } else { 'DEV_DEPENDENCY_OF' }
            relatedSpdxElement = if ($package.Scope -eq 'runtime') { $spdxId } else { $rootSpdxId }
        })
    }

    $sbomPath = Join-Path $OutputDirectory 'sbom.spdx.json'
    [ordered]@{
        spdxVersion = 'SPDX-2.3'
        dataLicense = 'CC0-1.0'
        SPDXID = 'SPDXRef-DOCUMENT'
        name = "$ProductName-$Version"
        documentNamespace = "https://schemas.invalid/dsh-windows-launcher/spdxdocs/$Version/$([guid]::NewGuid().ToString('N'))"
        creationInfo = [ordered]@{
            created = $createdAtUtc
            creators = @('Tool: DSH Windows Launcher eng/common.ps1')
        }
        documentDescribes = @($rootSpdxId)
        packages = $spdxPackages.ToArray()
        relationships = $relationships.ToArray()
    } | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $sbomPath -Encoding utf8NoBOM

    return [pscustomobject]@{
        SbomPath = $sbomPath
        LicenseInventoryPath = $licensePath
        PackageCount = $metadata.Count
    }
}

function Assert-DshPackageReportClean {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Json,

        [Parameter(Mandatory)]
        [ValidateSet('vulnerable', 'deprecated')]
        [string] $Kind
    )

    try {
        $null = $Json | ConvertFrom-Json -Depth 100 -ErrorAction Stop
    }
    catch {
        throw "无法解析 dotnet package list $Kind 的 JSON 输出。"
    }

    if ($Kind -eq 'vulnerable' -and $Json -match '"vulnerabilities"\s*:\s*\[\s*\{') {
        throw '发现存在已知漏洞的 NuGet 依赖。'
    }

    if ($Kind -eq 'deprecated' -and
        ($Json -match '"deprecationReasons"\s*:\s*\[\s*"' -or $Json -match '"deprecated"\s*:\s*true')) {
        throw '发现已弃用的 NuGet 依赖。'
    }
}

function Get-DshAuthenticodeEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [AllowNull()]
        [string] $ExpectedSubject,

        [switch] $RequireTimestamp
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode 无效：$Path；状态：$($signature.Status)；$($signature.StatusMessage)"
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSubject) -and
        $signature.SignerCertificate.Subject -ne $ExpectedSubject) {
        throw "签名证书主体不匹配：$Path；实际：$($signature.SignerCertificate.Subject)；预期：$ExpectedSubject"
    }

    if ($RequireTimestamp -and $null -eq $signature.TimeStamperCertificate) {
        throw "缺少可信时间戳：$Path"
    }

    return [pscustomobject]@{
        Path             = (Resolve-Path -LiteralPath $Path).Path
        Status           = $signature.Status.ToString()
        SignerSubject    = $signature.SignerCertificate.Subject
        SignerThumbprint = $signature.SignerCertificate.Thumbprint
        TimestampSubject = if ($null -eq $signature.TimeStamperCertificate) { $null } else { $signature.TimeStamperCertificate.Subject }
        TimestampNotAfter = if ($null -eq $signature.TimeStamperCertificate) { $null } else { $signature.TimeStamperCertificate.NotAfter.ToUniversalTime().ToString('O') }
    }
}

function Test-DshEvidenceTextSafe {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string] $Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $true
    }

    $forbidden = @(
        '(?i)\b(?:token|cookie|authorization)\s*[=:]',
        '(?i)https?://\S+\?(?:\S*&)?(?:token|access_token|id_token|code|key|sig|signature|auth|password)=[^\s&#]+',
        '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
        '\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\b'
    )
    foreach ($pattern in $forbidden) {
        if ($Text -match $pattern) {
            return $false
        }
    }

    return $true
}

function Test-DshReleaseHostEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $Evidence,

        [Parameter(Mandatory)]
        [string] $ExpectedVersion,

        [Parameter(Mandatory)]
        [string] $ExpectedExecutableName,

        [Parameter(Mandatory)]
        [string] $ExpectedSignerSubject,

        [string] $ExpectedSha256
    )

    try {
        if ($Evidence.SchemaVersion -ne 1 -or
            [DateTimeOffset]::Parse([string] $Evidence.CapturedAtUtc) -eq [DateTimeOffset]::MinValue) {
            return $false
        }

        $os = $Evidence.Os
        if ($null -eq $os -or
            $os.Platform -ne 'Win32NT' -or
            [string]::IsNullOrWhiteSpace([string] $os.Version) -or
            [string]::IsNullOrWhiteSpace([string] $os.BuildNumber) -or
            [string]::IsNullOrWhiteSpace([string] $os.Architecture) -or
            [int] $os.LogicalProcessorCount -lt 1) {
            return $false
        }

        $executables = @($Evidence.CandidateExecutable, $Evidence.InstalledExecutable)
        foreach ($executable in $executables) {
            if ($null -eq $executable -or
                -not [IO.Path]::IsPathFullyQualified([string] $executable.Path) -or
                $executable.FileName -cne $ExpectedExecutableName -or
                [int64] $executable.Size -le 0 -or
                [string]::IsNullOrWhiteSpace([string] $executable.FileVersion) -or
                -not ([string] $executable.FileVersion).StartsWith(
                    $ExpectedVersion,
                    [StringComparison]::OrdinalIgnoreCase) -or
                $executable.Sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
                return $false
            }

            $signature = $executable.Signature
            if ($null -eq $signature -or
                $signature.Status -ne 'Valid' -or
                $signature.SignerSubject -cne $ExpectedSignerSubject -or
                $signature.SignerThumbprint -notmatch '^[0-9A-Fa-f]{16,}$' -or
                [string]::IsNullOrWhiteSpace([string] $signature.TimestampSubject) -or
                [DateTimeOffset]::Parse([string] $signature.TimestampNotAfter) -eq
                    [DateTimeOffset]::MinValue) {
                return $false
            }
        }

        if ($Evidence.CandidateExecutable.Sha256 -cne
                $Evidence.InstalledExecutable.Sha256 -or
            [int64] $Evidence.CandidateExecutable.Size -ne
                [int64] $Evidence.InstalledExecutable.Size -or
            $Evidence.CandidateExecutable.FileVersion -cne
                $Evidence.InstalledExecutable.FileVersion) {
            return $false
        }
        if ([string]::Equals(
                [IO.Path]::GetFullPath([string] $Evidence.CandidateExecutable.Path),
                [IO.Path]::GetFullPath([string] $Evidence.InstalledExecutable.Path),
                [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }

        if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
            $Evidence.CandidateExecutable.Sha256 -cne $ExpectedSha256.ToUpperInvariant()) {
            return $false
        }

        return $true
    }
    catch {
        return $false
    }
}

function Get-DshRs13Verdict {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $Measurement
    )

    $maximumPrivateBytes = [int64] (2.5GB)
    $maximumIdleCpuPercent = 10.0
    $maximumLockReleaseSeconds = 60.0
    $minimumDurationSeconds = 30 * 60
    $idleWindowSeconds = 5 * 60
    $requiredSampleIntervalSeconds = 5
    $failures = [System.Collections.Generic.List[string]]::new()
    $peakPrivateBytes = [int64] 0
    $idleAverageCpuPercent = [double]::PositiveInfinity
    $maximumObservedGapSeconds = [double]::PositiveInfinity

    try {
        if ($Measurement.SchemaVersion -ne 1) {
            $failures.Add('schemaVersion')
        }

        $startedAt = [DateTimeOffset]::Parse([string] $Measurement.StartedAtUtc)
        $completedAt = [DateTimeOffset]::Parse([string] $Measurement.CompletedAtUtc)
        $closeRequestedAt = [DateTimeOffset]::Parse([string] $Measurement.CloseRequestedAtUtc)
        if ($completedAt -le $startedAt -or
            ($completedAt - $startedAt).TotalSeconds -lt $minimumDurationSeconds) {
            $failures.Add('duration')
        }
        if ($closeRequestedAt -lt $completedAt) {
            $failures.Add('closeSequence')
        }

        $sampleIntervalSeconds = [int] $Measurement.SampleIntervalSeconds
        if ($sampleIntervalSeconds -ne $requiredSampleIntervalSeconds) {
            $failures.Add('sampleInterval')
        }

        $logicalProcessorCount = [int] $Measurement.LogicalProcessorCount
        if ($logicalProcessorCount -ne 4) {
            $failures.Add('logicalProcessorCount')
        }

        $samples = @($Measurement.Samples)
        $minimumSampleCount = [int] [Math]::Ceiling(
            ($minimumDurationSeconds / $requiredSampleIntervalSeconds) * 0.95) + 1
        if ($samples.Count -lt $minimumSampleCount) {
            $failures.Add('sampleCount')
        }

        $parsedSamples = [System.Collections.Generic.List[object]]::new()
        $previousAt = [DateTimeOffset]::MinValue
        $maximumObservedGapSeconds = 0.0
        foreach ($sample in $samples) {
            $capturedAt = [DateTimeOffset]::Parse([string] $sample.CapturedAtUtc)
            if ($previousAt -ne [DateTimeOffset]::MinValue) {
                $gapSeconds = ($capturedAt - $previousAt).TotalSeconds
                if ($gapSeconds -le 0 -or $gapSeconds -gt ($requiredSampleIntervalSeconds * 2)) {
                    $failures.Add('sampleContinuity')
                }
                $maximumObservedGapSeconds = [Math]::Max(
                    $maximumObservedGapSeconds,
                    $gapSeconds)
            }
            $previousAt = $capturedAt

            $rootProcessId = [int] $sample.RootProcessId
            $processes = @($sample.Processes)
            if ($rootProcessId -le 0 -or $processes.Count -eq 0 -or
                @($processes | Where-Object { [int] $_.ProcessId -eq $rootProcessId }).Count -ne 1) {
                $failures.Add('processTree')
            }
            if (@($processes | Where-Object {
                        [string] $_.Name -match '^msedgewebview2(?:\.exe)?$'
                    }).Count -eq 0) {
                $failures.Add('webViewProcessTree')
            }

            $samplePrivateBytes = [int64] 0
            $cpuByProcess = @{}
            foreach ($process in $processes) {
                $processId = [int] $process.ProcessId
                $privateBytes = [int64] $process.PrivateBytes
                $totalProcessorSeconds = [double] $process.TotalProcessorSeconds
                if ($processId -le 0 -or
                    [string]::IsNullOrWhiteSpace([string] $process.Name) -or
                    $privateBytes -lt 0 -or
                    $totalProcessorSeconds -lt 0 -or
                    -not [double]::IsFinite($totalProcessorSeconds) -or
                    $cpuByProcess.ContainsKey($processId)) {
                    $failures.Add('processSample')
                    continue
                }

                $samplePrivateBytes += $privateBytes
                $cpuByProcess[$processId] = $totalProcessorSeconds
            }
            foreach ($process in $processes) {
                if ([int] $process.ProcessId -ne $rootProcessId -and
                    -not $cpuByProcess.ContainsKey([int] $process.ParentProcessId)) {
                    $failures.Add('processTree')
                }
            }
            $peakPrivateBytes = [Math]::Max($peakPrivateBytes, $samplePrivateBytes)
            $parsedSamples.Add([pscustomobject]@{
                CapturedAt = $capturedAt
                CpuByProcess = $cpuByProcess
            })
        }

        if ($parsedSamples.Count -gt 1 -and
            ($parsedSamples[$parsedSamples.Count - 1].CapturedAt -
                $parsedSamples[0].CapturedAt).TotalSeconds -lt $minimumDurationSeconds) {
            $failures.Add('sampleSpan')
        }

        if ($peakPrivateBytes -gt $maximumPrivateBytes) {
            $failures.Add('privateBytes')
        }

        $idleStartedAt = $startedAt.AddSeconds(
            $minimumDurationSeconds - $idleWindowSeconds)
        $idleSamples = @($parsedSamples | Where-Object CapturedAt -ge $idleStartedAt)
        if ($idleSamples.Count -lt 2 -or
            ($idleSamples[$idleSamples.Count - 1].CapturedAt -
                $idleSamples[0].CapturedAt).TotalSeconds -lt $idleWindowSeconds) {
            $failures.Add('idleWindow')
        }
        else {
            $idleCpuSeconds = 0.0
            for ($index = 1; $index -lt $idleSamples.Count; $index++) {
                $previous = $idleSamples[$index - 1]
                $current = $idleSamples[$index]
                if ((@($previous.CpuByProcess.Keys | Sort-Object) -join ',') -ne
                    (@($current.CpuByProcess.Keys | Sort-Object) -join ',')) {
                    $failures.Add('idleProcessChurn')
                }
                foreach ($entry in $current.CpuByProcess.GetEnumerator()) {
                    if ($previous.CpuByProcess.ContainsKey($entry.Key)) {
                        $delta = [double] $entry.Value -
                            [double] $previous.CpuByProcess[$entry.Key]
                        if ($delta -lt 0) {
                            $failures.Add('cpuCounter')
                        }
                        else {
                            $idleCpuSeconds += $delta
                        }
                    }
                    else {
                        $idleCpuSeconds += [double] $entry.Value
                    }
                }
            }

            $idleElapsedSeconds = (
                $idleSamples[$idleSamples.Count - 1].CapturedAt -
                $idleSamples[0].CapturedAt).TotalSeconds
            $idleAverageCpuPercent =
                ($idleCpuSeconds / $idleElapsedSeconds / $logicalProcessorCount) * 100
            if ($idleAverageCpuPercent -gt $maximumIdleCpuPercent) {
                $failures.Add('idleCpu')
            }
        }

        $udfLocks = @($Measurement.UdfLocks)
        if ($udfLocks.Count -ne 4 -or
            @($udfLocks.Path | Sort-Object -Unique).Count -ne 4) {
            $failures.Add('udfSet')
        }
        foreach ($udfLock in $udfLocks) {
            $releaseSeconds = [double] $udfLock.ReleaseSeconds
            $releasedAt = [DateTimeOffset]::Parse([string] $udfLock.ReleasedAtUtc)
            if ([string]::IsNullOrWhiteSpace([string] $udfLock.Path) -or
                $udfLock.Released -ne $true -or
                -not [double]::IsFinite($releaseSeconds) -or
                $releaseSeconds -lt 0 -or
                $releaseSeconds -gt $maximumLockReleaseSeconds -or
                $releasedAt -lt $closeRequestedAt -or
                [Math]::Abs(
                    ($releasedAt - $closeRequestedAt).TotalSeconds - $releaseSeconds) -gt 2) {
                $failures.Add('udfLockRelease')
            }
        }
    }
    catch {
        $failures.Add('malformed')
    }

    $distinctFailures = @($failures | Sort-Object -Unique)
    $reportedIdleCpuPercent = if ([double]::IsFinite($idleAverageCpuPercent)) {
        [Math]::Round($idleAverageCpuPercent, 3)
    }
    else {
        $null
    }
    $reportedMaximumGapSeconds = if ([double]::IsFinite($maximumObservedGapSeconds)) {
        [Math]::Round($maximumObservedGapSeconds, 3)
    }
    else {
        $null
    }
    return [pscustomobject]@{
        Passed = $distinctFailures.Count -eq 0
        Failures = $distinctFailures
        PeakPrivateBytes = $peakPrivateBytes
        PeakPrivateGiB = [Math]::Round($peakPrivateBytes / 1GB, 3)
        IdleAverageCpuPercent = $reportedIdleCpuPercent
        MaximumObservedGapSeconds = $reportedMaximumGapSeconds
        AllUdfLocksReleased = -not ($distinctFailures -contains 'udfLockRelease') -and
            -not ($distinctFailures -contains 'udfSet') -and
            -not ($distinctFailures -contains 'malformed')
        Limits = [pscustomobject]@{
            MinimumDurationSeconds = $minimumDurationSeconds
            SampleIntervalSeconds = $requiredSampleIntervalSeconds
            MaximumPrivateBytes = $maximumPrivateBytes
            MaximumIdleCpuPercent = $maximumIdleCpuPercent
            MaximumLockReleaseSeconds = $maximumLockReleaseSeconds
        }
    }
}

function Resolve-DshSmokeResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('PASS', 'FAIL', 'MISSING')]
        [string] $AutomatedEvidence,

        [Parameter(Mandatory)]
        [ValidateSet('PASS', 'FAIL', 'SKIP', 'PENDING', 'RECORD')]
        [string] $Attestation
    )

    if ($AutomatedEvidence -ne 'PASS') {
        return 'FAIL'
    }
    return $Attestation
}

function Get-DshSmokeMatrix {
    [CmdletBinding()]
    param()

    return @(
        [pscustomobject]@{ Id = 'RS-01'; Frequency = '每次'; TriggerTags = @('every', 'installer', 'signing', 'release-constants'); Success = 'PASS'; Environment = 'Windows 11 VM'; Instructions = '核对最终安装包签名、可信时间戳、文件名、大小、版本、SHA-256、Publisher、SBOM 和依赖基线；确认安装包、自有 EXE 和卸载器身份一致。' },
        [pscustomobject]@{ Id = 'RS-02'; Frequency = '每次'; TriggerTags = @('every', 'installer'); Success = 'PASS'; Environment = '干净 Windows 11 VM'; Instructions = '以标准用户从默认目录全新安装并启动真实 DshWindowsLauncher.exe；确认无提权、无后台进程，开始菜单和卸载登记正确。' },
        [pscustomobject]@{ Id = 'RS-03'; Frequency = '首版或安装器/Runtime 变化'; TriggerTags = @('installer', 'runtime', 'release-constants'); Success = 'PASS'; Environment = 'Windows 11 VM'; Instructions = '分别在 Runtime 已满足、缺失、低于下限和完全离线快照验证检测、离线修复及失败前不替换程序；验证固定卷空目录资格与危险路径拒绝。' },
        [pscustomobject]@{ Id = 'RS-04'; Frequency = '每次'; TriggerTags = @('every', 'windows', 'process'); Success = 'PASS'; Environment = 'Windows 11 实体机'; Instructions = '验证空目录添加流程、重复启动转交、默认目标、同目标窗口去重、纯键盘核心流程、高对比度及 100%/150%/200% DPI。' },
        [pscustomobject]@{ Id = 'RS-05'; Frequency = '每次'; TriggerTags = @('every', 'network', 'pairing', 'harness'); Success = 'PASS'; Environment = 'Windows 11 实体机 + Linux A'; Instructions = '使用默认 3080 完成无凭据探测、信任确认和 authority 一致的 LAN 链接配对；核对 303、干净根页面、认证 API 与重启自动连接。' },
        [pscustomobject]@{ Id = 'RS-06'; Frequency = '每次'; TriggerTags = @('every', 'data', 'udf', 'windows'); Success = 'PASS'; Environment = 'Windows 11 实体机 + Linux A/B'; Instructions = '保存四个规定目标，验证端点、唯一默认、窗口去重、逐目标会话隔离、单目标故障隔离及忘记目标继任规则。' },
        [pscustomobject]@{ Id = 'RS-07'; Frequency = '首版或网络/探测/插件变化'; TriggerTags = @('network', 'pairing', 'harness', 'lan-plugin'); Success = 'PASS'; Environment = 'Windows 11 实体机 + 错误服务夹具'; Instructions = '轮换非 HTTP、错误 200、403、旧无认证服务、非 RFC1918 和公用网络；确认拒绝错误服务且不扫描或改连端口。' },
        [pscustomobject]@{ Id = 'RS-08'; Frequency = '首版或配对/数据变化'; TriggerTags = @('data', 'network', 'pairing', 'harness', 'lan-plugin'); Success = 'PASS'; Environment = 'Windows 11 实体机'; Instructions = '验证错误、过期、重复、loopback、跨目标和额外参数链接；确认 token、输入、剪贴板竞态、取消、崩溃恢复和 401 仅影响当前目标。' },
        [pscustomobject]@{ Id = 'RS-09'; Frequency = '每次'; TriggerTags = @('every', 'harness', 'webview'); Success = 'PASS'; Environment = 'Windows 11 实体机 + Linux A'; Instructions = '在真实 Harness 完成流式回复、持续 WebSocket、空闲恢复、页面复制和 Linux 工作目录浏览；确认启动器未复制 Harness 业务。' },
        [pscustomobject]@{ Id = 'RS-10'; Frequency = '每次'; TriggerTags = @('every', 'webview', 'permissions'); Success = 'PASS'; Environment = 'Windows 11 实体机'; Instructions = '验证导航、外链、mailto、下载、弹窗、协议、权限、证书、DevTools 和原生桥接策略；检查原生诊断 ZIP 敏感数据排除。' },
        [pscustomobject]@{ Id = 'RS-11'; Frequency = '每次'; TriggerTags = @('every', 'images', 'harness', 'webview'); Success = 'PASS'; Environment = 'Windows 11 实体机 + Linux A/B'; Instructions = '验证单图、多图、截图、文本图片混合、不支持格式及非图片的 Harness 原生结果；确认当前目标 Session 隔离且恢复后不重放。' },
        [pscustomobject]@{ Id = 'RS-12'; Frequency = '首版或 WebView/Runtime/Harness 变化'; TriggerTags = @('webview', 'runtime', 'harness', 'lan-plugin', 'process'); Success = 'PASS'; Environment = 'Windows 11 实体机'; Instructions = '验证网络中断、导航失败、renderer/browser 失败和无响应恢复；确认 UDF 复用、跨目标隔离、默认不变和业务写入不重放。' },
        [pscustomobject]@{ Id = 'RS-13'; Frequency = '每次'; TriggerTags = @('every', 'windows', 'udf', 'process', 'webview'); Success = 'PASS'; Environment = '4 逻辑处理器、8 GiB、SSD 的 Windows 11 实体机'; Instructions = '四个目标窗口混合使用 30 分钟；记录进程树 Private Bytes、最后五分钟 CPU 和关闭后 UDF 锁释放，按检查表阈值判定。' },
        [pscustomobject]@{ Id = 'RS-14'; Frequency = '每次普通卸载；条件执行升级/修复'; TriggerTags = @('every', 'installer', 'data', 'runtime'); Success = 'PASS'; Environment = 'Windows 11 VM'; Instructions = '验证同版修复、前一 RC 升级、拒绝降级、IPC 正常退出、迁移保护、普通卸载保留数据、清数据卸载、Runtime 保留和安装日志。' },
        [pscustomobject]@{ Id = 'RS-15'; Frequency = '首版及平台/.NET/WebView2/安装器变化'; TriggerTags = @('windows', 'runtime', 'webview', 'installer'); Success = 'RECORD'; Environment = 'Windows 10 22H2 x64'; Instructions = '记录真实 EXE 启动、已配对连接和图片拖放/粘贴；Windows 10 专属问题记为 RECORD，共同安全或数据问题必须 FAIL。' }
    )
}

function Get-DshRequiredSmokeIds {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('FirstRelease', 'RegularPatch')]
        [string] $ReleaseKind,

        [string[]] $TriggerTags = @()
    )

    $matrix = @(Get-DshSmokeMatrix)
    if ($ReleaseKind -eq 'FirstRelease') {
        return @($matrix.Id)
    }

    $knownTags = @($matrix.TriggerTags | ForEach-Object { $_ } | Sort-Object -Unique)
    foreach ($tag in $TriggerTags) {
        if ($knownTags -notcontains $tag) {
            throw "未知 smoke triggerTag：$tag"
        }
    }

    $required = @(
        $matrix |
            Where-Object {
                $_.TriggerTags -contains 'every' -or
                @($_.TriggerTags | Where-Object { $TriggerTags -contains $_ }).Count -gt 0
            } |
            Select-Object -ExpandProperty Id
    )
    return @($required | Sort-Object { [int] ($_ -replace '^RS-', '') })
}
