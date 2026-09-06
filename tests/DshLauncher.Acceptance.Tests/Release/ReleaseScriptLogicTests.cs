using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace DshLauncher.Acceptance.Tests.Release;

public sealed class ReleaseScriptLogicTests
{
    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-08")]
    public void SemanticVersionComparisonMatchesReleaseOrdering()
    {
        using JsonDocument result = RunCommonJson(
            "@(" +
            "[pscustomobject]@{ Left='1.0.0-rc.1'; Right='1.0.0-rc.2'; Result=(Compare-DshSemVer '1.0.0-rc.1' '1.0.0-rc.2') }," +
            "[pscustomobject]@{ Left='1.0.0-rc.2'; Right='1.0.0'; Result=(Compare-DshSemVer '1.0.0-rc.2' '1.0.0') }," +
            "[pscustomobject]@{ Left='1.0.0'; Right='1.0.0+build.1'; Result=(Compare-DshSemVer '1.0.0' '1.0.0+build.1') }" +
            ")");

        JsonElement[] cases = result.RootElement.EnumerateArray().ToArray();
        Assert.Collection(
            cases,
            item => Assert.Equal(-1, item.GetProperty("Result").GetInt32()),
            item => Assert.Equal(-1, item.GetProperty("Result").GetInt32()),
            item => Assert.Equal(0, item.GetProperty("Result").GetInt32()));
    }

    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-08")]
    public void SemanticVersionValidationEnforcesNumericPrereleaseRules()
    {
        using JsonDocument result = RunCommonJson(
            "[pscustomobject]@{" +
            "Valid=(Test-DshSemVer '1.0.0-alpha.1+build.01');" +
            "LeadingZero=(Test-DshSemVer '1.0.0-alpha.01');" +
            "LargeNumericOrder=(Compare-DshSemVer '1.0.0-999999999999999999999999' '1.0.0-1000000000000000000000000')" +
            "}");

        Assert.True(result.RootElement.GetProperty("Valid").GetBoolean());
        Assert.False(result.RootElement.GetProperty("LeadingZero").GetBoolean());
        Assert.Equal(-1, result.RootElement.GetProperty("LargeNumericOrder").GetInt32());
    }

    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-08")]
    public void CandidateValidationRejectsAnEmptyReleaseConstant()
    {
        string root = FindRepositoryRoot();
        string rootLiteral = QuotePowerShell(root);
        string output = RunPowerShell(
            CommonPrelude(root) +
            $"$constants=Get-DshReleaseConstants -RepositoryRoot {rootLiteral};" +
            "$constants.releaseStatus='candidate';" +
            "$constants.distribution.signing.publisher=$null;" +
            $"try {{ Assert-DshReleaseConstants -RepositoryRoot {rootLiteral} -Constants $constants -RequireCandidate; 'ACCEPTED' }} " +
            "catch { 'REJECTED' }");

        Assert.Equal("REJECTED", output.Trim());
    }

    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-08")]
    public void CandidateValidationAcceptsOnlyTheNoneSigningPolicy()
    {
        string root = FindRepositoryRoot();
        string rootLiteral = QuotePowerShell(root);
        string output = RunPowerShell(
            CommonPrelude(root) +
            $"$required=Get-DshReleaseConstants -RepositoryRoot {rootLiteral};" +
            "$required.distribution.signing.policy='required';" +
            $"$requiredResult=try {{ Assert-DshReleaseConstants -RepositoryRoot {rootLiteral} -Constants $required -RequireCandidate; 'ACCEPTED' }} catch {{ 'REJECTED' }};" +
            $"$optional=Get-DshReleaseConstants -RepositoryRoot {rootLiteral};" +
            "$optional.distribution.signing.policy='optional';" +
            $"$optionalResult=try {{ Assert-DshReleaseConstants -RepositoryRoot {rootLiteral} -Constants $optional -RequireCandidate; 'ACCEPTED' }} catch {{ 'REJECTED' }};" +
            $"$noneResult=try {{ Assert-DshReleaseConstants -RepositoryRoot {rootLiteral} -Constants (Get-DshReleaseConstants -RepositoryRoot {rootLiteral}) -RequireCandidate; 'ACCEPTED' }} catch {{ 'REJECTED' }};" +
            "\"$requiredResult;$optionalResult;$noneResult\"");

        Assert.Equal("REJECTED;REJECTED;ACCEPTED", output.Trim());
    }

    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-07")]
    public void ProductionProjectReferencesMatchTheFixedArchitecture()
    {
        string root = FindRepositoryRoot();
        string rootLiteral = QuotePowerShell(root);
        string output = RunPowerShell(
            CommonPrelude(root) +
            $"$violations=@(Get-DshArchitectureViolations -RepositoryRoot {rootLiteral});" +
            "if($violations.Count -eq 0){'PASS'}else{$violations -join [Environment]::NewLine}");

        Assert.Equal("PASS", output.Trim());
    }

    [Fact]
    [Trait("triggerTags", "VFY-08,smoke-matrix")]
    public void SmokeSelectionUsesStableIdsAndTriggerTags()
    {
        using JsonDocument result = RunCommonJson(
            "[pscustomobject]@{" +
            "First=@(Get-DshRequiredSmokeIds -ReleaseKind FirstRelease);" +
            "Regular=@(Get-DshRequiredSmokeIds -ReleaseKind RegularPatch);" +
            "Installer=@(Get-DshRequiredSmokeIds -ReleaseKind RegularPatch -TriggerTags installer);" +
            "ThirdPartyUi=@(Get-DshRequiredSmokeIds -ReleaseKind RegularPatch -TriggerTags third-party-ui);" +
            "Matrix=@(Get-DshSmokeMatrix)" +
            "}");

        JsonElement root = result.RootElement;
        Assert.Equal(15, root.GetProperty("First").GetArrayLength());
        Assert.Equal(10, root.GetProperty("Regular").GetArrayLength());
        Assert.Contains(
            root.GetProperty("Installer").EnumerateArray().Select(static value => value.GetString()),
            static id => id == "RS-03");
        string?[] thirdPartyUi = root.GetProperty("ThirdPartyUi")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        Assert.Contains("RS-07", thirdPartyUi);
        Assert.Contains("RS-08", thirdPartyUi);
        Assert.Contains("RS-12", thirdPartyUi);
        Assert.Contains("RS-15", thirdPartyUi);
        Assert.All(
            root.GetProperty("Matrix").EnumerateArray(),
            static item =>
            {
                Assert.Matches(
                    "^RS-(0[1-9]|1[0-5])$",
                    item.GetProperty("Id").GetString() ?? string.Empty);
                Assert.True(item.GetProperty("TriggerTags").GetArrayLength() > 0);
            });
    }

    [Fact]
    [Trait("triggerTags", "VFY-08,smoke-matrix")]
    public void ReleaseHostEvidenceRequiresMatchingMeasuredUnsignedExecutableIdentity()
    {
        using JsonDocument result = RunCommonJson(
            "$candidate=[pscustomobject]@{Path='C:\\candidate\\DshWindowsLauncher.exe';" +
            "FileName='DshWindowsLauncher.exe';Size=4096;FileVersion='1.0.0.0';" +
            "Sha256=('A'*64);AuthenticodeStatus='NotSigned'};" +
            "$installed=[pscustomobject]@{Path='C:\\installed\\DshWindowsLauncher.exe';" +
            "FileName='DshWindowsLauncher.exe';Size=4096;FileVersion='1.0.0.0';" +
            "Sha256=('A'*64);AuthenticodeStatus='NotSigned'};" +
            "$os=[pscustomobject]@{Platform='Win32NT';Version='10.0.26100.0';" +
            "BuildNumber='26100';DisplayVersion='25H2';Architecture='X64';" +
            "LogicalProcessorCount=4};" +
            "$valid=[pscustomobject]@{SchemaVersion=1;CapturedAtUtc='2026-08-30T00:00:00Z';" +
            "Os=$os;CandidateExecutable=$candidate;InstalledExecutable=$installed};" +
            "$mismatch=[pscustomobject]@{SchemaVersion=1;CapturedAtUtc='2026-08-30T00:00:00Z';" +
            "Os=$os;CandidateExecutable=$candidate;InstalledExecutable=($installed.PSObject.Copy())};" +
            "$mismatch.InstalledExecutable.Sha256=('B'*64);" +
            "$signed=[pscustomobject]@{SchemaVersion=1;CapturedAtUtc='2026-08-30T00:00:00Z';" +
            "Os=$os;CandidateExecutable=$candidate;InstalledExecutable=($installed.PSObject.Copy())};" +
            "$signed.InstalledExecutable.AuthenticodeStatus='Valid';" +
            "$missing=[pscustomobject]@{SchemaVersion=1;CapturedAtUtc='2026-08-30T00:00:00Z';" +
            "Os=$os;CandidateExecutable=$candidate;InstalledExecutable=($installed.PSObject.Copy())};" +
            "$missing.InstalledExecutable.AuthenticodeStatus=$null;" +
            "$samePath=[pscustomobject]@{SchemaVersion=1;CapturedAtUtc='2026-08-30T00:00:00Z';" +
            "Os=$os;CandidateExecutable=$candidate;InstalledExecutable=($installed.PSObject.Copy())};" +
            "$samePath.InstalledExecutable.Path=$candidate.Path;" +
            "[pscustomobject]@{" +
            "Valid=(Test-DshReleaseHostEvidence -Evidence $valid -ExpectedVersion '1.0.0.0' " +
            "-ExpectedExecutableName 'DshWindowsLauncher.exe' -ExpectedSha256 ('A'*64));" +
            "FrozenMismatch=(Test-DshReleaseHostEvidence -Evidence $valid -ExpectedVersion '1.0.0.0' " +
            "-ExpectedExecutableName 'DshWindowsLauncher.exe' -ExpectedSha256 ('B'*64));" +
            "Mismatch=(Test-DshReleaseHostEvidence -Evidence $mismatch -ExpectedVersion '1.0.0.0' " +
            "-ExpectedExecutableName 'DshWindowsLauncher.exe');" +
            "SignedIsRejected=(Test-DshReleaseHostEvidence -Evidence $signed -ExpectedVersion '1.0.0.0' " +
            "-ExpectedExecutableName 'DshWindowsLauncher.exe');" +
            "SamePath=(Test-DshReleaseHostEvidence -Evidence $samePath -ExpectedVersion '1.0.0.0' " +
            "-ExpectedExecutableName 'DshWindowsLauncher.exe');" +
            "Missing=(Test-DshReleaseHostEvidence -Evidence $missing -ExpectedVersion '1.0.0.0' " +
            "-ExpectedExecutableName 'DshWindowsLauncher.exe')" +
            "}");

        Assert.True(result.RootElement.GetProperty("Valid").GetBoolean());
        Assert.False(result.RootElement.GetProperty("FrozenMismatch").GetBoolean());
        Assert.False(result.RootElement.GetProperty("Mismatch").GetBoolean());
        Assert.False(result.RootElement.GetProperty("SignedIsRejected").GetBoolean());
        Assert.False(result.RootElement.GetProperty("SamePath").GetBoolean());
        Assert.False(result.RootElement.GetProperty("Missing").GetBoolean());
    }

    [Fact]
    [Trait("triggerTags", "VFY-08,smoke-matrix")]
    public void Rs13VerdictIsComputedFromRawSamplesAndLockResults()
    {
        using JsonDocument result = RunCommonJson(
            "$started=[DateTimeOffset]::Parse('2026-08-30T00:00:00Z');" +
            "$samples=@(for($i=0;$i -le 360;$i++){" +
            "$at=$started.AddSeconds($i*5);" +
            "[pscustomobject]@{CapturedAtUtc=$at.ToString('O');RootProcessId=100;Processes=@(" +
            "[pscustomobject]@{ProcessId=100;ParentProcessId=0;Name='DshWindowsLauncher';" +
            "PrivateBytes=536870912;TotalProcessorSeconds=[double]$i}," +
            "[pscustomobject]@{ProcessId=101;ParentProcessId=100;Name='msedgewebview2';" +
            "PrivateBytes=536870912;TotalProcessorSeconds=[double]$i})}});" +
            "$locks=@(1..4|ForEach-Object{[pscustomobject]@{Path=('C:\\udf\\'+$_);" +
            "Released=$true;ReleaseSeconds=60.0;ReleasedAtUtc=$started.AddSeconds(1860).ToString('O')}});" +
            "$valid=[pscustomobject]@{SchemaVersion=1;StartedAtUtc=$started.ToString('O');" +
            "CompletedAtUtc=$started.AddSeconds(1800).ToString('O');SampleIntervalSeconds=5;" +
            "LogicalProcessorCount=4;Samples=$samples;" +
            "CloseRequestedAtUtc=$started.AddSeconds(1800).ToString('O');UdfLocks=$locks};" +
            "$privateExceeded=$valid.PSObject.Copy();$privateExceeded.Samples=@($samples|ForEach-Object{" +
            "$copy=$_.PSObject.Copy();$copy.Processes=@($_.Processes|ForEach-Object{$p=$_.PSObject.Copy();" +
            "$p.PrivateBytes=1400000000;$p});$copy});" +
            "$cpuExceeded=$valid.PSObject.Copy();$cpuExceeded.Samples=@(for($i=0;$i -le 360;$i++){" +
            "$at=$started.AddSeconds($i*5);[pscustomobject]@{CapturedAtUtc=$at.ToString('O');" +
            "RootProcessId=100;Processes=@(" +
            "[pscustomobject]@{ProcessId=100;ParentProcessId=0;Name='DshWindowsLauncher';" +
            "PrivateBytes=1;TotalProcessorSeconds=[double]($i*2)}," +
            "[pscustomobject]@{ProcessId=101;ParentProcessId=100;Name='msedgewebview2';" +
            "PrivateBytes=1;TotalProcessorSeconds=[double]($i*2)})}});" +
            "$locked=$valid.PSObject.Copy();$locked.UdfLocks=@($locks|ForEach-Object{" +
            "$copy=$_.PSObject.Copy();$copy});$locked.UdfLocks[0].Released=$false;" +
            "$slowUnlock=$valid.PSObject.Copy();$slowUnlock.UdfLocks=@($locks|ForEach-Object{" +
            "$copy=$_.PSObject.Copy();$copy});$slowUnlock.UdfLocks[0].ReleaseSeconds=60.001;" +
            "$missing=$valid.PSObject.Copy();$missing.Samples=@();" +
            "[pscustomobject]@{" +
            "Valid=(Get-DshRs13Verdict -Measurement $valid).Passed;" +
            "PrivateExceeded=(Get-DshRs13Verdict -Measurement $privateExceeded).Passed;" +
            "CpuExceeded=(Get-DshRs13Verdict -Measurement $cpuExceeded).Passed;" +
            "Locked=(Get-DshRs13Verdict -Measurement $locked).Passed;" +
            "SlowUnlock=(Get-DshRs13Verdict -Measurement $slowUnlock).Passed;" +
            "Missing=(Get-DshRs13Verdict -Measurement $missing).Passed" +
            "}");

        Assert.True(result.RootElement.GetProperty("Valid").GetBoolean());
        Assert.False(result.RootElement.GetProperty("PrivateExceeded").GetBoolean());
        Assert.False(result.RootElement.GetProperty("CpuExceeded").GetBoolean());
        Assert.False(result.RootElement.GetProperty("Locked").GetBoolean());
        Assert.False(result.RootElement.GetProperty("SlowUnlock").GetBoolean());
        Assert.False(result.RootElement.GetProperty("Missing").GetBoolean());
    }

    [Fact]
    [Trait("triggerTags", "VFY-08,smoke-matrix")]
    public void ManualPassCannotOverrideMissingOrFailedAutomatedEvidence()
    {
        using JsonDocument result = RunCommonJson(
            "[pscustomobject]@{" +
            "Passed=(Resolve-DshSmokeResult -AutomatedEvidence PASS -Attestation PASS);" +
            "Recorded=(Resolve-DshSmokeResult -AutomatedEvidence PASS -Attestation RECORD);" +
            "Failed=(Resolve-DshSmokeResult -AutomatedEvidence FAIL -Attestation PASS);" +
            "Missing=(Resolve-DshSmokeResult -AutomatedEvidence MISSING -Attestation PASS)" +
            "}");

        Assert.Equal("PASS", result.RootElement.GetProperty("Passed").GetString());
        Assert.Equal("RECORD", result.RootElement.GetProperty("Recorded").GetString());
        Assert.Equal("FAIL", result.RootElement.GetProperty("Failed").GetString());
        Assert.Equal("FAIL", result.RootElement.GetProperty("Missing").GetString());
    }

    [Fact]
    [Trait("triggerTags", "VFY-08,smoke-matrix")]
    public void ReleaseSmokePersistsMeasuredHostAndRs13Evidence()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(root, "eng", "release-smoke.ps1"));

        Assert.Contains("Get-CurrentHostEvidence", script, StringComparison.Ordinal);
        Assert.Contains("Get-ExecutableIdentityEvidence", script, StringComparison.Ordinal);
        Assert.Contains("Test-DshReleaseHostEvidence", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-Rs13Measurement", script, StringComparison.Ordinal);
        Assert.Contains("Get-DshRs13Verdict", script, StringComparison.Ordinal);
        Assert.Contains("Resolve-DshSmokeResult", script, StringComparison.Ordinal);
        Assert.Contains("release-structured-evidence.json", script, StringComparison.Ordinal);
        Assert.Contains("DshWindowsLauncher-rs13-", script, StringComparison.Ordinal);
        Assert.DoesNotContain("recorded by RS-01/RS-02 operator", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,VFY-08")]
    public void PersistentEvidenceRejectsCredentialBearingUrls()
    {
        using JsonDocument result = RunCommonJson(
            "[pscustomobject]@{" +
            "Safe=(Test-DshEvidenceTextSafe 'probe returned 401');" +
            "PublicQuery=(Test-DshEvidenceTextSafe 'https://go.microsoft.com/fwlink/?LinkId=392727');" +
            "Query=(Test-DshEvidenceTextSafe 'http://192.168.1.20:3080/?token=secret');" +
            "Cookie=(Test-DshEvidenceTextSafe 'Cookie=session-value')" +
            "}");

        Assert.True(result.RootElement.GetProperty("Safe").GetBoolean());
        Assert.True(result.RootElement.GetProperty("PublicQuery").GetBoolean());
        Assert.False(result.RootElement.GetProperty("Query").GetBoolean());
        Assert.False(result.RootElement.GetProperty("Cookie").GetBoolean());
    }

    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-08")]
    public void LockedPackageInventoryIsUniqueAndClassifiesDependencyScope()
    {
        string root = FindRepositoryRoot();
        using JsonDocument result = RunCommonJson(
            $"$packages=@(Get-DshLockedPackageInventory -RepositoryRoot {QuotePowerShell(root)});" +
            "[pscustomobject]@{" +
            "Count=$packages.Count;" +
            "DuplicateCount=@($packages | Group-Object Id,Version | Where-Object Count -gt 1).Count;" +
            "RuntimeCount=@($packages | Where-Object Scope -eq runtime).Count;" +
            "DevelopmentCount=@($packages | Where-Object Scope -eq development).Count" +
            "}");

        Assert.True(result.RootElement.GetProperty("Count").GetInt32() > 0);
        Assert.Equal(0, result.RootElement.GetProperty("DuplicateCount").GetInt32());
        Assert.Equal(
            result.RootElement.GetProperty("Count").GetInt32(),
            result.RootElement.GetProperty("RuntimeCount").GetInt32() +
            result.RootElement.GetProperty("DevelopmentCount").GetInt32());
        Assert.True(result.RootElement.GetProperty("DevelopmentCount").GetInt32() > 0);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,VFY-08,installer")]
    public void WebView2BootstrapperIsFrozenAndInstalledWithoutInnoExecution()
    {
        string root = FindRepositoryRoot();
        string packageScript = File.ReadAllText(Path.Combine(root, "eng", "package.ps1"));

        // The packaging tooling pins the WebView2 bootstrapper to the same
        // frozen upstream identity without reading a removed constants
        // section: the literals below are the single source of truth.
        Assert.Contains(
            "'17debf797a6c737959bc588236e897936ffac1af5f7e515e674ab32f9edfe719'",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains("'1.3.265.7'", packageScript, StringComparison.Ordinal);
        Assert.Contains("[string] $WebView2BootstrapperPath", packageScript, StringComparison.Ordinal);
        Assert.Contains("MicrosoftEdgeWebview2Setup.exe", packageScript, StringComparison.Ordinal);

        string innoScript = File.ReadAllText(
            Path.Combine(root, "installer", "DshWindowsLauncher.iss"));
        Assert.Contains(
            "Source: \"{#SourceRoot}\\{#RuntimeBootstrapperName}\"",
            innoScript,
            StringComparison.Ordinal);
        int runSectionStart = innoScript.IndexOf("[Run]", StringComparison.Ordinal);
        int codeSectionStart = innoScript.IndexOf("[Code]", StringComparison.Ordinal);
        Assert.True(runSectionStart >= 0 && codeSectionStart > runSectionStart);
        string runSection = innoScript[runSectionStart..codeSectionStart];
        Assert.DoesNotContain("RuntimeBootstrapperName", runSection, StringComparison.Ordinal);
        Assert.DoesNotContain("MicrosoftEdgeWebview2Setup.exe", runSection, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("triggerTags", "VFY-04,VFY-08")]
    public void PairingBaselineMatchesTheProtocolImplementation()
    {
        string root = FindRepositoryRoot();
        using JsonDocument constants = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "eng", "release-constants.json")));
        JsonElement pairing = constants.RootElement.GetProperty("pairingBaseline");

        Assert.Equal("@linxin666/dsh-remote-web-ui", pairing.GetProperty("plugin").GetString());
        Assert.Equal("dsh_pair", pairing.GetProperty("cookieName").GetString());
        Assert.Equal("/pair-accept", pairing.GetProperty("acceptPage").GetString());
        Assert.Equal("/api/pair/accept", pairing.GetProperty("acceptPath").GetString());
        Assert.Equal("/api/pair/heartbeat", pairing.GetProperty("heartbeatPath").GetString());
        Assert.Equal("/api/pair/status", pairing.GetProperty("statusPath").GetString());
        Assert.Equal(25, pairing.GetProperty("onlineWindowSeconds").GetInt32());

        // The protocol implementation must carry the exact same wire facts.
        string protocol = File.ReadAllText(
            Path.Combine(root, "src", "DshLauncher.Core", "Pairing", "PairingProtocol.cs"));
        Assert.Contains("DefaultCookieName = \"dsh_pair\"", protocol, StringComparison.Ordinal);
        Assert.Contains("AcceptPath = \"/api/pair/accept\"", protocol, StringComparison.Ordinal);
        Assert.Contains("HeartbeatPath = \"/api/pair/heartbeat\"", protocol, StringComparison.Ordinal);
        Assert.Contains("StatusPath = \"/api/pair/status\"", protocol, StringComparison.Ordinal);
        Assert.Contains("HostOnlineWindow = TimeSpan.FromSeconds(25)", protocol, StringComparison.Ordinal);

        // The link parser must accept exactly the advertised entry page.
        string parser = File.ReadAllText(
            Path.Combine(root, "src", "DshLauncher.Core", "Pairing", "PairingLink.cs"));
        Assert.Contains("\"/pair-accept\"", parser, StringComparison.Ordinal);

        // The keep-alive default must stay strictly below the host window.
        Assert.True(pairing.GetProperty("defaultHeartbeatIntervalSeconds").GetInt32()
            < pairing.GetProperty("onlineWindowSeconds").GetInt32());
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,VFY-08,installer")]
    public void InstallerLocksAndResolvesEveryInstallPathComponentBeforeFileReplacement()
    {
        string root = FindRepositoryRoot();
        string innoScript = File.ReadAllText(
            Path.Combine(root, "installer", "DshWindowsLauncher.iss"));

        Assert.Contains("CreateFileW", innoScript, StringComparison.Ordinal);
        Assert.Contains("GetFinalPathNameByHandleW", innoScript, StringComparison.Ordinal);
        Assert.Contains("FileFlagOpenReparsePoint", innoScript, StringComparison.Ordinal);
        Assert.Contains("FileFlagBackupSemantics", innoScript, StringComparison.Ordinal);
        Assert.Contains("FileShareRead or FileShareWrite", innoScript, StringComparison.Ordinal);
        Assert.Contains("LockExistingPathComponents", innoScript, StringComparison.Ordinal);
        Assert.Contains("IsPathWithinRoot", innoScript, StringComparison.Ordinal);
        Assert.Contains(
            "Re-read attributes only after the no-delete-share handle",
            innoScript,
            StringComparison.Ordinal);
        Assert.Contains("SecureInstallDirectory(WizardDirValue, Reason)", innoScript, StringComparison.Ordinal);
        Assert.Contains("ReleaseInstallPathHandles", innoScript, StringComparison.Ordinal);

        int repairIndex = innoScript.IndexOf(
            "RepairAndVerifyWebView2(NeedsRestart, Reason)",
            StringComparison.Ordinal);
        int secureDirectoryIndex = innoScript.IndexOf(
            "SecureInstallDirectory(WizardDirValue, Reason)",
            repairIndex,
            StringComparison.Ordinal);
        int fileSectionIndex = innoScript.IndexOf("[Files]", StringComparison.Ordinal);
        int lockedComponentsIndex = innoScript.IndexOf(
            "if not LockExistingPathComponents(Path, DriveRoot, Reason)",
            StringComparison.Ordinal);
        int writableProbeIndex = innoScript.IndexOf(
            "if not IsDirectoryWritable(Path)",
            StringComparison.Ordinal);
        Assert.True(fileSectionIndex >= 0 && repairIndex > fileSectionIndex);
        Assert.True(secureDirectoryIndex > repairIndex);
        Assert.True(lockedComponentsIndex >= 0 && writableProbeIndex > lockedComponentsIndex);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,VFY-08,installer,data-clear")]
    public void DataClearDeletesOnlyValidatedOwnedEntriesByNoFollowHandles()
    {
        string root = FindRepositoryRoot();
        string innoScript = File.ReadAllText(
            Path.Combine(root, "installer", "DshWindowsLauncher.iss"));

        Assert.Contains("GetFileInformationByHandle", innoScript, StringComparison.Ordinal);
        Assert.Contains("SetFileInformationByHandle", innoScript, StringComparison.Ordinal);
        Assert.Contains("FileDispositionInfoEx", innoScript, StringComparison.Ordinal);
        Assert.Contains("FileFlagOpenReparsePoint", innoScript, StringComparison.Ordinal);
        Assert.Contains("NumberOfLinks <> 1", innoScript, StringComparison.Ordinal);
        Assert.Contains("DeleteOwnedDirectoryByHandle", innoScript, StringComparison.Ordinal);
        Assert.Contains("DeleteOwnedEntryByHandle", innoScript, StringComparison.Ordinal);
        Assert.DoesNotContain("TreeContainsReparsePoint", innoScript, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteOwnedTree", innoScript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,VFY-08,installer,ipc")]
    public void SetupAndUninstallUseCurrentUserIpcWithoutExecutingRegisteredApplicationPath()
    {
        string root = FindRepositoryRoot();
        string innoScript = File.ReadAllText(
            Path.Combine(root, "installer", "DshWindowsLauncher.iss"));
        string ipcHelper = File.ReadAllText(
            Path.Combine(root, "installer", "maintenance-ipc.ps1"));

        Assert.Contains("MaintenanceHelperName", innoScript, StringComparison.Ordinal);
        Assert.Contains("RunMaintenanceIpcHelper", innoScript, StringComparison.Ordinal);
        Assert.Contains("function ReleaseMaintenanceIpcReservation: Boolean", innoScript, StringComparison.Ordinal);
        Assert.Contains("if not ReleaseMaintenanceIpcReservation then", innoScript, StringComparison.Ordinal);
        Assert.Contains("RaiseException('当前用户 IPC 维护占用未能确认释放。')", innoScript, StringComparison.Ordinal);
        Assert.Contains("ValidateRegisteredInstallation", innoScript, StringComparison.Ordinal);
        Assert.DoesNotContain("CertificateSubject", innoScript, StringComparison.Ordinal);
        Assert.DoesNotContain("SignTool=", innoScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplicationPath := AddBackslash(ExistingInstallPath)",
            innoScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Exec(\n    ApplicationPath",
            innoScript,
            StringComparison.Ordinal);

        Assert.Contains("WindowsIdentity", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("PipeAccessRule", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("SetAccessRuleProtection($true, $false)", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("NamedPipeServerStreamAcl]::Create", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("NamedPipeServerStream]::new", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("NamedPipeServerStreamAcl' -as [type]", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("function Test-PipeBusyException", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("($current.HResult -band 0xffff) -eq 231", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("function Get-SafeFailureCode", ipcHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("$_.Exception.Message", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("catch [IO.EndOfStreamException]", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("exclusive pipe reservation remains the authoritative gate", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("maintenance-exit", ipcHelper, StringComparison.Ordinal);
        Assert.Contains("Wait-ForPipeRelease", ipcHelper, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,VFY-08,installer,integrity")]
    public void FormalPackageBindsInstallerHelpersAndInstalledIdentityToFrozenInputs()
    {
        string root = FindRepositoryRoot();
        string packageScript = File.ReadAllText(Path.Combine(root, "eng", "package.ps1"));

        // 正式包不签名：流水线不得残留任何签名工具/证书入口，但必须反向断言 NotSigned。
        Assert.Contains("function Assert-Unsigned", packageScript, StringComparison.Ordinal);
        Assert.Contains("ownArtifactsSigned = $false", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-SignTool", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("CertificateThumbprint", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("SignToolPath", packageScript, StringComparison.Ordinal);
        Assert.Contains("MaintenanceHelperSha256", packageScript, StringComparison.Ordinal);
        Assert.Contains("IdentityHelperSha256", packageScript, StringComparison.Ordinal);
        Assert.Contains("InstallOwnershipMarkerSha256", packageScript, StringComparison.Ordinal);
        Assert.Contains("installer/maintenance-ipc.ps1", packageScript, StringComparison.Ordinal);
        Assert.Contains("installer/validate-installation.ps1", packageScript, StringComparison.Ordinal);
        Assert.Contains("installer/install-owner.txt", packageScript, StringComparison.Ordinal);

        string internalPackageScript = File.ReadAllText(
            Path.Combine(root, "eng", "package-internal.ps1"));
        string unsignedPackageScript = File.ReadAllText(
            Path.Combine(root, "eng", "package-unsigned.ps1"));
        string internalInstallerScript = File.ReadAllText(
            Path.Combine(root, "installer", "DshWindowsLauncher.InternalTest.iss"));

        Assert.Contains("releaseStatus -cne 'development'", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("^\\d+\\.\\d+\\.\\d+-internal-test\\.[1-9]\\d*$", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("Get-DshSourceState", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("WorktreeState -cne 'clean'", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("verify-summary.json", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("$verifySummary.source.commit -cne $sourceState.Commit", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("Assert-SignedMicrosoftTool", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains(
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            internalPackageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "CN=Pyrsys B.V., O=Pyrsys B.V., S=Noord-Holland, C=NL",
            internalPackageScript,
            StringComparison.Ordinal);
        Assert.Contains("[StringComparison]::Ordinal", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-notmatch '(?i)Microsoft'", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-notmatch '(?i)Pyrsys", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains(
            "'17debf797a6c737959bc588236e897936ffac1af5f7e515e674ab32f9edfe719'",
            internalPackageScript,
            StringComparison.Ordinal);
        Assert.Contains("'1.3.265.7'", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("function Get-InnoCompilerVersion", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains(
            "#pragma message \"DSH_INNO_VERSION=\" + DecodeVer(VER, 4)",
            internalPackageScript,
            StringComparison.Ordinal);
        Assert.Contains("$startInfo.ArgumentList.Add('/O-')", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("$innoIdentityVersion -cne $innoCompilerVersion", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains(
            "$innoCompilerSignature.SignerThumbprint",
            internalPackageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$innoIdentitySignature.SignerThumbprint",
            internalPackageScript,
            StringComparison.Ordinal);
        Assert.Contains("innoCompilerVersion = $innoCompilerVersion", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("innoIdentityVersion = $innoIdentityVersion", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetVersionInfo($InnoCompilerPath)",
            internalPackageScript,
            StringComparison.Ordinal);
        Assert.Contains("$buildFlavor = if ($OfficialUnsignedRelease) { 'Official' } else { 'InternalTest' }", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("OfficialUnsignedRelease = $true", unsignedPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:AssemblyName=", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("$internalProductName = 'DSH Windows Launcher (INTERNAL TEST)'", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("'--self-contained', 'true'", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("'-p:PublishTrimmed=false'", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("$applicationVersion.FileVersion -cne $fileVersion", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("FileVersion.StartsWith", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("[regex]::Escape($Version)", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains(
            "'(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$'",
            internalPackageScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ProductVersion.StartsWith", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("DshWindowsLauncher.InternalTest.exe", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("DshWindowsLauncher.dll", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("SignatureStatus]::NotSigned", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("internal-test-manifest.json", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("sbom.spdx.json", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("third-party-licenses.json", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("UNSIGNED INTERNAL TEST - NOT FOR PRODUCTION USE", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("INTERNAL-TEST-UNSIGNED-NOT-FOR-PRODUCTION-USE", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT FOR DISTRIBUTION", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("applicationDataId = $applicationDataId", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("singleInstanceBaseName = $singleInstanceBaseName", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("installDirectoryName = $installDirectoryName", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("$finalSourceState.WorktreeState -cne 'clean'", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("eng/package.ps1", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-FilePath $installerPath", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-FilePath $WebView2BootstrapperPath", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-FilePath $stagedBootstrapperPath", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", internalPackageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Item", internalPackageScript, StringComparison.Ordinal);

        const string version = "1.0.0-internal-test.1";
        Regex productVersionPattern = new(
            "^" + Regex.Escape(version) + @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.CultureInvariant);
        Assert.Matches(productVersionPattern, version);
        Assert.Matches(productVersionPattern, version + "+0123456789abcdef");
        Assert.Matches(productVersionPattern, version + "+build.7");
        Assert.DoesNotMatch(productVersionPattern, "1.0.0-internal-test.10");
        Assert.DoesNotMatch(productVersionPattern, version + ".0");
        Assert.DoesNotMatch(productVersionPattern, version + "+");

        int manifestStart = internalPackageScript.IndexOf(
            "$manifest = [ordered]@{",
            StringComparison.Ordinal);
        int manifestEnd = internalPackageScript.IndexOf(
            "$manifestPath =",
            manifestStart,
            StringComparison.Ordinal);
        Assert.True(manifestStart >= 0 && manifestEnd > manifestStart);
        string manifestBlock = internalPackageScript[manifestStart..manifestEnd];
        Assert.DoesNotContain("generatedAt", manifestBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdAt", manifestBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("capturedAt", manifestBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(
            @"(?im)^\s*[A-Za-z0-9_]*path[A-Za-z0-9_]*\s*=",
            manifestBlock);
        Assert.DoesNotMatch(@"[A-Za-z]:\\", manifestBlock);

        int checksumEntriesStart = internalPackageScript.IndexOf(
            "$checksumEntries = @(",
            StringComparison.Ordinal);
        int checksumEntriesEnd = internalPackageScript.IndexOf(
            ") | Sort-Object",
            checksumEntriesStart,
            StringComparison.Ordinal);
        Assert.True(checksumEntriesStart >= 0 && checksumEntriesEnd > checksumEntriesStart);
        string checksumEntries = internalPackageScript[checksumEntriesStart..checksumEntriesEnd];
        Assert.Contains("$installerPath", checksumEntries, StringComparison.Ordinal);
        Assert.Contains("$manifestPath", checksumEntries, StringComparison.Ordinal);
        Assert.DoesNotContain("$verifySummaryDestination", checksumEntries, StringComparison.Ordinal);
        Assert.DoesNotContain("$sbomDestination", checksumEntries, StringComparison.Ordinal);
        Assert.DoesNotContain("$licenseDestination", checksumEntries, StringComparison.Ordinal);

        Assert.Contains("F3418DD7-58B7-4E0D-B0F7-D77C52FDF91C", internalPackageScript, StringComparison.Ordinal);
        Assert.Contains("AppId={{F3418DD7-58B7-4E0D-B0F7-D77C52FDF91C}", internalInstallerScript, StringComparison.Ordinal);
        Assert.Contains("DSH Windows Launcher (INTERNAL TEST)", internalInstallerScript, StringComparison.Ordinal);
        Assert.Contains("DshWindowsLauncher.InternalTest", internalInstallerScript, StringComparison.Ordinal);
        Assert.Contains("SignedUninstaller=no", internalInstallerScript, StringComparison.Ordinal);
        Assert.Contains("DisableDirPage=yes", internalInstallerScript, StringComparison.Ordinal);
        Assert.Contains("CloseApplications=yes", internalInstallerScript, StringComparison.Ordinal);
        Assert.Contains("NOT FOR PRODUCTION USE", internalInstallerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT FOR DISTRIBUTION", internalInstallerScript, StringComparison.Ordinal);
        Assert.Contains("MicrosoftEdgeWebview2Setup.exe", internalInstallerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("SyntaxOnly", internalInstallerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("SignTool=", internalInstallerScript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,VFY-08,installer,runtime")]
    public void RuntimeRepairCreatesARealStableWebView2EnvironmentBeforeReplacingFiles()
    {
        string root = FindRepositoryRoot();
        string innoScript = File.ReadAllText(
            Path.Combine(root, "installer", "DshWindowsLauncher.iss"));
        string healthProbe = File.ReadAllText(
            Path.Combine(root, "installer", "webview2-health-probe.ps1"));
        string packageScript = File.ReadAllText(Path.Combine(root, "eng", "package.ps1"));

        Assert.Contains("VerifyWebView2EnvironmentHealth", innoScript, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web.WebView2.Core.dll", innoScript, StringComparison.Ordinal);
        Assert.Contains("WebView2Loader.dll", innoScript, StringComparison.Ordinal);
        Assert.Contains("WebView2HealthHelperSha256", innoScript, StringComparison.Ordinal);
        Assert.Contains("WebView2CoreAssemblySha256", innoScript, StringComparison.Ordinal);
        Assert.Contains("WebView2LoaderSha256", innoScript, StringComparison.Ordinal);
        Assert.Contains("WebView2HealthHelperSha256", packageScript, StringComparison.Ordinal);

        int repairFunction = innoScript.IndexOf(
            "function RepairAndVerifyWebView2",
            StringComparison.Ordinal);
        int satisfiedRegistryCheck = innoScript.IndexOf(
            "CompareNumericVersions(InstalledVersion, '{#WebView2MinimumVersion}')",
            repairFunction,
            StringComparison.Ordinal);
        int satisfiedEnvironmentCheck = innoScript.IndexOf(
            "VerifyWebView2EnvironmentHealth(Reason)",
            satisfiedRegistryCheck,
            StringComparison.Ordinal);
        int offlineInstaller = innoScript.IndexOf(
            "ExtractTemporaryFile('{#RuntimeInstallerName}')",
            repairFunction,
            StringComparison.Ordinal);
        int repairedEnvironmentCheck = innoScript.IndexOf(
            "VerifyWebView2EnvironmentHealth(Reason)",
            offlineInstaller,
            StringComparison.Ordinal);
        int secureDirectory = innoScript.IndexOf(
            "SecureInstallDirectory(WizardDirValue, Reason)",
            repairedEnvironmentCheck,
            StringComparison.Ordinal);
        Assert.True(repairFunction >= 0 && satisfiedRegistryCheck > repairFunction);
        Assert.True(satisfiedEnvironmentCheck > satisfiedRegistryCheck);
        Assert.True(offlineInstaller > satisfiedEnvironmentCheck);
        Assert.True(repairedEnvironmentCheck > offlineInstaller);
        Assert.True(secureDirectory > repairedEnvironmentCheck);

        Assert.Contains("CoreWebView2Environment]::CreateAsync", healthProbe, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2ReleaseChannels]::Stable", healthProbe, StringComparison.Ordinal);
        Assert.Contains("GetAvailableBrowserVersionString", healthProbe, StringComparison.Ordinal);
        Assert.Contains("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", healthProbe, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-08,release-identity")]
    public void FormalPackageRequiresOneCleanSourceIdentityMatchingVerification()
    {
        string root = FindRepositoryRoot();
        string packageScript = File.ReadAllText(Path.Combine(root, "eng", "package.ps1"));

        Assert.Contains("Get-DshSourceState", packageScript, StringComparison.Ordinal);
        Assert.Contains("'^(?:[0-9a-f]{40}|[0-9a-f]{64})$'", packageScript, StringComparison.Ordinal);
        Assert.Contains("WorktreeState -cne 'clean'", packageScript, StringComparison.Ordinal);
        Assert.Contains("$verifySummary.source.available -ne $sourceState.Available", packageScript, StringComparison.Ordinal);
        Assert.Contains("$verifySummary.source.commit -cne $sourceState.Commit", packageScript, StringComparison.Ordinal);
        Assert.Contains("$verifySummary.source.worktreeState -cne $sourceState.WorktreeState", packageScript, StringComparison.Ordinal);
        Assert.Contains("source = [ordered]@{", packageScript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("triggerTags", "VFY-08,release-identity")]
    public void InstalledVersionValidationAcceptsBuildMetadataButRejectsDifferentVersions()
    {
        string assemblyPath = typeof(DshLauncher.Desktop.App).Assembly.Location;
        string productVersion = FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion!;
        Assert.Contains("+", productVersion, StringComparison.Ordinal);
        string version = productVersion.Split('+', 2)[0];
        string scriptPath = Path.Combine(FindRepositoryRoot(), "installer", "validate-installation.ps1");
        string invocation = $"& {QuotePowerShell(scriptPath)} -ApplicationPath {QuotePowerShell(assemblyPath)} " +
            $"-UninstallerPath {QuotePowerShell(assemblyPath)} -ExpectedProductVersion ";
        using JsonDocument result = RunCommonJson(
            "$accepted=$false; $rejected=$false; " +
            $"try {{ {invocation}{QuotePowerShell(version)}; $accepted=$true }} catch {{ }}; " +
            $"try {{ {invocation}'999.0.0' }} catch {{ $rejected=$true }}; " +
            "[pscustomobject]@{ Accepted=$accepted; Rejected=$rejected }");
        Assert.True(result.RootElement.GetProperty("Accepted").GetBoolean());
        Assert.True(result.RootElement.GetProperty("Rejected").GetBoolean());
    }

    private static JsonDocument RunCommonJson(string expression)
    {
        string root = FindRepositoryRoot();
        string output = RunPowerShell(
            CommonPrelude(root) +
            $"{expression} | ConvertTo-Json -Depth 20 -Compress");
        return JsonDocument.Parse(output);
    }

    private static string CommonPrelude(string repositoryRoot)
    {
        string commonPath = Path.Combine(repositoryRoot, "eng", "common.ps1");
        return $". {QuotePowerShell(commonPath)};";
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

    private static string RunPowerShell(string script)
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

        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);

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
