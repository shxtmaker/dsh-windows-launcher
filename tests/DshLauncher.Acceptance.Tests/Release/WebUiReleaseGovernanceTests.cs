using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DshLauncher.Acceptance.Tests.Release;

public sealed class WebUiReleaseGovernanceTests
{
    [Fact]
    [Trait("triggerTags", "VFY-08,governance-change,release-identity")]
    public void GovernanceIdentityMatchesReleaseConstantsAndIsDeterministic()
    {
        string root = FindRepositoryRoot();
        string first = Path.Combine(Path.GetTempPath(), $"dsh-governance-{Guid.NewGuid():N}");
        string second = Path.Combine(Path.GetTempPath(), $"dsh-governance-{Guid.NewGuid():N}");
        try
        {
            using JsonDocument result = RunCommonJson(
                root,
                "$pwsh=Join-Path $PSHOME 'pwsh.exe';" +
                $"$script={QuotePowerShell(Path.Combine(root, "eng", "generate-webui-governance.ps1"))};" +
                $"$null=Invoke-DshNativeCapture -FilePath $pwsh -WorkingDirectory {QuotePowerShell(root)} " +
                $"-Arguments @('-NoLogo','-NoProfile','-NonInteractive','-File',$script," +
                $"'-RepositoryRoot',{QuotePowerShell(root)},'-OutputDirectory',{QuotePowerShell(first)});" +
                $"$null=Invoke-DshNativeCapture -FilePath $pwsh -WorkingDirectory {QuotePowerShell(root)} " +
                $"-Arguments @('-NoLogo','-NoProfile','-NonInteractive','-File',$script," +
                $"'-RepositoryRoot',{QuotePowerShell(root)},'-OutputDirectory',{QuotePowerShell(second)});" +
                $"$constants=Get-DshReleaseConstants -RepositoryRoot {QuotePowerShell(root)};" +
                $"$a=Get-DshWebUiGovernanceIdentity -RepositoryRoot {QuotePowerShell(root)} " +
                $"-SummaryPath {QuotePowerShell(Path.Combine(first, "webui-governance-summary.json"))} -Constants $constants;" +
                $"$b=Get-DshWebUiGovernanceIdentity -RepositoryRoot {QuotePowerShell(root)} " +
                $"-SummaryPath {QuotePowerShell(Path.Combine(second, "webui-governance-summary.json"))} -Constants $constants;" +
                "[pscustomobject]@{First=$a;Second=$b}");

            JsonElement firstIdentity = result.RootElement.GetProperty("First");
            JsonElement secondIdentity = result.RootElement.GetProperty("Second");
            Assert.Equal("1.1.0", firstIdentity.GetProperty("contractVersion").GetString());
            Assert.Equal(3, firstIdentity.GetProperty("registryVersion").GetInt64());
            Assert.Equal(
                firstIdentity.GetProperty("governanceSummarySha256").GetString(),
                secondIdentity.GetProperty("governanceSummarySha256").GetString());
            Assert.Matches(
                "^[0-9a-f]{64}$",
                firstIdentity.GetProperty("contractCapabilitiesCanonicalSha256").GetString() ?? string.Empty);
            Assert.Matches(
                "^[0-9a-f]{64}$",
                firstIdentity.GetProperty("registryCanonicalSha256").GetString() ?? string.Empty);
        }
        finally
        {
            if (Directory.Exists(first))
            {
                Directory.Delete(first, recursive: true);
            }
            if (Directory.Exists(second))
            {
                Directory.Delete(second, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("triggerTags", "VFY-08,evidence-change")]
    public void VerificationImpactFallsBackToAllVfyAndRsForUnknownInput()
    {
        string root = FindRepositoryRoot();
        using JsonDocument result = RunCommonJson(
            root,
            $"Resolve-DshVerificationImpact -RepositoryRoot {QuotePowerShell(root)} " +
            "-ChangedPaths @('unknown/product-input.bin')");

        Assert.True(result.RootElement.GetProperty("failClosed").GetBoolean());
        Assert.Equal(8, result.RootElement.GetProperty("triggerTags").GetArrayLength());
        Assert.Equal(15, result.RootElement.GetProperty("requiredRs").GetArrayLength());
        Assert.Equal(
            "unknown/product-input.bin",
            result.RootElement.GetProperty("unmappedPaths")[0].GetString());
    }

    [Fact]
    [Trait("triggerTags", "VFY-08,webview-change")]
    public void DedicatedLiteralScanAllowsFixturesButRejectsProductCode()
    {
        string root = FindRepositoryRoot();
        string fixtureRoot = Path.Combine(Path.GetTempPath(), $"dsh-static-scan-{Guid.NewGuid():N}");
        string productDirectory = Path.Combine(fixtureRoot, "src", "Product");
        string testsDirectory = Path.Combine(fixtureRoot, "tests");
        string referenceDirectory = Path.Combine(fixtureRoot, "docs", "reference-adapters");
        Directory.CreateDirectory(productDirectory);
        Directory.CreateDirectory(testsDirectory);
        Directory.CreateDirectory(referenceDirectory);
        string dedicatedOrigin = "https://dsh-" + "market.com";
        string productPath = Path.Combine(productDirectory, "Policy.cs");
        File.WriteAllText(productPath, $"internal static class Policy {{ const string Origin = \"{dedicatedOrigin}\"; }}");
        File.WriteAllText(
            Path.Combine(referenceDirectory, "provider.md"),
            $"Reference fixture origin: `{dedicatedOrigin}`.");
        try
        {
            using JsonDocument rejected = RunCommonJson(
                root,
                $"[pscustomobject]@{{Violations=@(Get-DshLegacyCompatibilityEntryViolations " +
                $"-RepositoryRoot {QuotePowerShell(fixtureRoot)})}}");
            JsonElement violations = rejected.RootElement.GetProperty("Violations");
            Assert.Single(violations.EnumerateArray());
            Assert.Contains(
                "src/Product/Policy.cs",
                violations[0].GetString(),
                StringComparison.Ordinal);

            File.Delete(productPath);
            using JsonDocument accepted = RunCommonJson(
                root,
                $"[pscustomobject]@{{Violations=@(Get-DshLegacyCompatibilityEntryViolations " +
                $"-RepositoryRoot {QuotePowerShell(fixtureRoot)})}}");
            Assert.Equal(0, accepted.RootElement.GetProperty("Violations").GetArrayLength());
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-08,installer,signing,release-identity")]
    public void VerificationAndPackagingScriptsCarryTheCompatibilityIdentityAndSignedSet()
    {
        string root = FindRepositoryRoot();
        string common = File.ReadAllText(Path.Combine(root, "eng", "common.ps1"));
        string verify = File.ReadAllText(Path.Combine(root, "eng", "verify.ps1"));
        string package = File.ReadAllText(Path.Combine(root, "eng", "package.ps1"));
        string internalPackage = File.ReadAllText(Path.Combine(root, "eng", "package-internal.ps1"));
        string unsignedPackage = File.ReadAllText(Path.Combine(root, "eng", "package-unsigned.ps1"));
        string smoke = File.ReadAllText(Path.Combine(root, "eng", "release-smoke.ps1"));

        Assert.Contains("'DshLauncher.Compatibility'    = @()", common, StringComparison.Ordinal);
        Assert.Contains("$projects.Count -ne 10", verify, StringComparison.Ordinal);
        Assert.Contains("$testProjects.Count -ne 5", verify, StringComparison.Ordinal);
        Assert.Contains("generate-webui-governance.ps1", verify, StringComparison.Ordinal);
        Assert.Contains("Get-DshCurrentVerificationImpact", verify, StringComparison.Ordinal);
        Assert.Contains("Get-DshLegacyCompatibilityEntryViolations", verify, StringComparison.Ordinal);
        Assert.Contains("managedEntryAssemblySignature", package, StringComparison.Ordinal);
        Assert.Contains("signedApplicationSet = [ordered]@{", package, StringComparison.Ordinal);
        Assert.Contains("compatibilityIdentity = $compatibilityIdentity", package, StringComparison.Ordinal);
        Assert.Contains("release-notes-input.md", package, StringComparison.Ordinal);
        Assert.Contains("releaseEligible = [bool] $OfficialUnsignedRelease", internalPackage, StringComparison.Ordinal);
        Assert.Contains("OfficialUnsignedRelease = $true", unsignedPackage, StringComparison.Ordinal);
        Assert.Contains("authenticodeStatus = 'NotSigned'", internalPackage, StringComparison.Ordinal);
        Assert.Contains("compatibilityIdentity = $compatibilityIdentity", internalPackage, StringComparison.Ordinal);
        Assert.Contains("Assert-DshSbomCompatibilityIdentity", smoke, StringComparison.Ordinal);
        Assert.Contains("signedApplicationSet = [ordered]@{", smoke, StringComparison.Ordinal);
        Assert.Contains("verificationImpact = $packageManifest.verificationImpact", smoke, StringComparison.Ordinal);
    }

    private static JsonDocument RunCommonJson(string repositoryRoot, string expression)
    {
        string commonPath = Path.Combine(repositoryRoot, "eng", "common.ps1");
        string output = RunPowerShell(
            repositoryRoot,
            $". {QuotePowerShell(commonPath)};{expression} | ConvertTo-Json -Depth 50 -Compress");
        return JsonDocument.Parse(output);
    }

    private static string QuotePowerShell(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eng", "common.ps1")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static string RunPowerShell(string repositoryRoot, string script)
    {
        string? configuredPowerShell = Environment.GetEnvironmentVariable("DSHWL_PWSH_EXE");
        string programFilesPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");
        string executable = !string.IsNullOrWhiteSpace(configuredPowerShell)
            ? configuredPowerShell
            : File.Exists(programFilesPowerShell)
                ? programFilesPowerShell
                : "pwsh.exe";
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell.");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell logic test timed out.");
        }
        string standardOutput = standardOutputTask.GetAwaiter().GetResult();
        string standardError = standardErrorTask.GetAwaiter().GetResult();
        Assert.True(
            process.ExitCode == 0,
            $"PowerShell exited with {process.ExitCode}.{Environment.NewLine}{standardError}");
        return standardOutput.Trim();
    }
}
