using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DshLauncher.Core;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-03")]
public sealed class JsonCompatibilityStateStoreTests : IAsyncLifetime
{
    private const string DigestA =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string DigestB =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static readonly string[] UnsortedPurposes =
        ["turnstile frame", "market passive images"];
    private static readonly string[] ExpectedPurposes =
        ["market passive images", "turnstile frame"];
    private static readonly string[] UnsortedRuleVersions =
        ["turnstile@1.0.0", "market@2.0.0"];
    private static readonly string[] ExpectedRuleVersions =
        ["market@2.0.0", "turnstile@1.0.0"];
    private static readonly string[] DiagnosticFieldNames =
    [
        "schemaVersion",
        "contractVersion",
        "descriptorSchemaVersion",
        "registrySchemaVersion",
        "registryVersion",
        "ruleVersions",
        "snapshotDigest",
        "redactedOriginName",
        "reasonCode",
        "phase",
    ];
    private static readonly string[] MarketPurpose = ["market passive images"];
    private static readonly string[] MarketRuleVersion = ["market@1.0.0"];

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "DshLauncherJsonCompatibilityStateStoreTests",
        Guid.NewGuid().ToString("N"));

    private ApplicationDataStore? _applicationDataStore;
    private JsonCompatibilityStateStore? _compatibilityStateStore;

    private ApplicationDataStore ApplicationDataStore =>
        _applicationDataStore ??
        throw new InvalidOperationException("The application data store is unavailable.");

    private JsonCompatibilityStateStore CompatibilityStateStore =>
        _compatibilityStateStore ??
        throw new InvalidOperationException("The compatibility state store is unavailable.");

    private ApplicationDataLayout Layout => ApplicationDataStore.Layout;

    public async ValueTask InitializeAsync()
    {
        _applicationDataStore = new ApplicationDataStore(
            new ApplicationDataLayout(_testRoot));
        await _applicationDataStore.InitializeAsync(TestContext.Current.CancellationToken);
        _compatibilityStateStore = new JsonCompatibilityStateStore(
            _applicationDataStore);
    }

    public ValueTask DisposeAsync()
    {
        _applicationDataStore?.Dispose();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RegistryWatermarkOnlyAdvancesAndDetectsAnOlderBuiltInRegistry()
    {
        await CompatibilityStateStore.AdvanceRegistryWatermarkAsync(
            3,
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.AdvanceRegistryWatermarkAsync(
            7,
            TestContext.Current.CancellationToken);

        var current = await CompatibilityStateStore.ReadRegistryWatermarkAsync(
            7,
            TestContext.Current.CancellationToken);
        var rollback = await CompatibilityStateStore.ReadRegistryWatermarkAsync(
            6,
            TestContext.Current.CancellationToken);

        Assert.Equal(CompatibilityStateReadStatus.Loaded, current.Status);
        Assert.Equal(7, current.State?.RegistryVersion);
        Assert.Equal(
            CompatibilityStateReadStatus.RegistryRollbackDetected,
            rollback.Status);
        Assert.Equal(7, rollback.State?.RegistryVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CompatibilityStateStore.AdvanceRegistryWatermarkAsync(
                4,
                TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Layout.RegistryWatermarkPath));
        Assert.True(File.Exists(Layout.RegistryWatermarkBackupPath));
        Assert.Contains(
            "\"registryVersion\": 3",
            await File.ReadAllTextAsync(
                Layout.RegistryWatermarkBackupPath,
                TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":99,\"registryVersion\":8}", CompatibilityStateReadStatus.UnknownSchema)]
    [InlineData("{\"schemaVersion\":1,\"registryVersion\":", CompatibilityStateReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"registryVersion\":8}", CompatibilityStateReadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1,\"registryVersion\":8,\"extra\":true}", CompatibilityStateReadStatus.Corrupt)]
    public async Task InvalidWatermarkFailsClosedWithoutChangingTargetData(
        string invalidJson,
        CompatibilityStateReadStatus expectedStatus)
    {
        var targetId = Guid.Parse("472190c8-3c92-4aed-83b0-a44d44f50b41");
        await ApplicationDataStore.PrepareTargetDataAsync(
            targetId,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Layout.RegistryWatermarkPath,
            invalidJson,
            TestContext.Current.CancellationToken);

        var result = await CompatibilityStateStore.ReadRegistryWatermarkAsync(
            8,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.Status);
        Assert.True(Directory.Exists(Layout.GetTargetRoot(targetId)));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CompatibilityStateStore.AdvanceRegistryWatermarkAsync(
                9,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfirmationAndDiagnosticUseIndependentStrictCanonicalDocuments()
    {
        var targetId = Guid.Parse("56109456-670b-4db3-b53b-447490993e51");
        var confirmation = new ExternalCapabilityConfirmationState(
            targetId,
            DigestA,
            UnsortedPurposes,
            ExternalCapabilityDecision.Accepted,
            new DateTimeOffset(2026, 8, 31, 1, 2, 3, TimeSpan.FromHours(8)));
        var diagnostic = new CompatibilityDiagnosticState(
            "1.0.0",
            1,
            1,
            8,
            UnsortedRuleVersions,
            DigestA,
            "private-target-01",
            "EXTERNAL_FRAME_BLOCKED",
            CompatibilityLifecyclePhase.Ready);

        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            confirmation,
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteCompatibilityDiagnosticAsync(
            targetId,
            diagnostic,
            TestContext.Current.CancellationToken);
        var confirmationRead = await CompatibilityStateStore
            .ReadExternalCapabilityConfirmationAsync(
                targetId,
                TestContext.Current.CancellationToken);
        var diagnosticRead = await CompatibilityStateStore
            .ReadCompatibilityDiagnosticAsync(
                targetId,
                TestContext.Current.CancellationToken);

        Assert.Equal(CompatibilityStateReadStatus.Loaded, confirmationRead.Status);
        Assert.Equal(
            ExpectedPurposes,
            confirmationRead.State?.NormalizedPurposes);
        Assert.Equal(TimeSpan.Zero, confirmationRead.State?.DecidedAtUtc.Offset);
        Assert.Equal(CompatibilityStateReadStatus.Loaded, diagnosticRead.Status);
        Assert.Equal(
            ExpectedRuleVersions,
            diagnosticRead.State?.RuleVersions);

        using var diagnosticJson = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Layout.GetCompatibilityDiagnosticPath(targetId),
            TestContext.Current.CancellationToken));
        var fields = diagnosticJson.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(
            DiagnosticFieldNames,
            fields);
        Assert.DoesNotContain(
            fields,
            field => field.Contains("descriptor", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(
                         field,
                         "descriptorSchemaVersion",
                         StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptConfirmationPrimaryRecoversOnlyThePreviousValidSnapshot()
    {
        var targetId = Guid.Parse("4ca0a478-59c3-4274-828a-b1ee31c6122b");
        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            Confirmation(targetId, DigestA, ExternalCapabilityDecision.Accepted),
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            Confirmation(targetId, DigestB, ExternalCapabilityDecision.Rejected),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Layout.GetExternalCapabilityConfirmationPath(targetId),
            "{corrupt-primary",
            TestContext.Current.CancellationToken);

        var recovered = await CompatibilityStateStore
            .ReadExternalCapabilityConfirmationAsync(
                targetId,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            CompatibilityStateReadStatus.RecoveredFromBackup,
            recovered.Status);
        Assert.Equal(DigestA, recovered.State?.SnapshotDigest);
        Assert.Equal(ExternalCapabilityDecision.Accepted, recovered.State?.Decision);
        Assert.Equal(
            await File.ReadAllTextAsync(
                Layout.GetExternalCapabilityConfirmationBackupPath(targetId),
                TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(
                Layout.GetExternalCapabilityConfirmationPath(targetId),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownConfirmationSchemaDoesNotFallBackToAnOlderDecision()
    {
        var targetId = Guid.Parse("f2c365e2-3e51-41b1-b4ea-c252f60e9541");
        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            Confirmation(targetId, DigestA, ExternalCapabilityDecision.Accepted),
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            Confirmation(targetId, DigestB, ExternalCapabilityDecision.Rejected),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Layout.GetExternalCapabilityConfirmationPath(targetId),
            $$"""
            {
              "schemaVersion": 99,
              "targetId": "{{targetId}}",
              "snapshotDigest": "{{DigestB}}",
              "normalizedPurposes": ["market passive images"],
              "decision": "Rejected",
              "decidedAtUtc": "2026-08-31T00:00:00+00:00"
            }
            """,
            TestContext.Current.CancellationToken);

        var result = await CompatibilityStateStore
            .ReadExternalCapabilityConfirmationAsync(
                targetId,
                TestContext.Current.CancellationToken);

        Assert.Equal(CompatibilityStateReadStatus.UnknownSchema, result.Status);
        Assert.Equal(99, result.FoundSchemaVersion);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task RevokingConfirmationDeletesOnlyItsPrimaryAndBackup()
    {
        var targetId = Guid.Parse("bc990e0e-a533-427e-9844-fdf82a89f11d");
        await CompatibilityStateStore.AdvanceRegistryWatermarkAsync(
            4,
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.AdvanceRegistryWatermarkAsync(
            5,
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            Confirmation(targetId, DigestA, ExternalCapabilityDecision.Accepted),
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            Confirmation(targetId, DigestB, ExternalCapabilityDecision.Rejected),
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteCompatibilityDiagnosticAsync(
            targetId,
            Diagnostic(),
            TestContext.Current.CancellationToken);

        await CompatibilityStateStore.DeleteExternalCapabilityConfirmationAsync(
            targetId,
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.DeleteExternalCapabilityConfirmationAsync(
            targetId,
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(
            Layout.GetExternalCapabilityConfirmationPath(targetId)));
        Assert.False(File.Exists(
            Layout.GetExternalCapabilityConfirmationBackupPath(targetId)));
        Assert.True(File.Exists(Layout.GetCompatibilityDiagnosticPath(targetId)));
        Assert.True(Directory.Exists(Layout.GetUdfPath(targetId)));
        Assert.True(Directory.Exists(Layout.GetTargetRoot(targetId)));
        Assert.Equal(
            CompatibilityStateReadStatus.Missing,
            (await CompatibilityStateStore.ReadExternalCapabilityConfirmationAsync(
                targetId,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            CompatibilityStateReadStatus.Loaded,
            (await CompatibilityStateStore.ReadCompatibilityDiagnosticAsync(
                targetId,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            5,
            (await CompatibilityStateStore.ReadRegistryWatermarkAsync(
                5,
                TestContext.Current.CancellationToken)).State?.RegistryVersion);
    }

    [Fact]
    public async Task ConfirmationReadAndRevocationNeverFollowAReparsePoint()
    {
        var targetId = Guid.Parse("af980b85-18ca-4131-897c-a98ea48792dd");
        await ApplicationDataStore.PrepareTargetDataAsync(
            targetId,
            TestContext.Current.CancellationToken);
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "DshLauncherCompatibilityStateOutsideTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var sentinelPath = Path.Combine(externalRoot, "must-survive.txt");
        await File.WriteAllTextAsync(
            sentinelPath,
            "must-survive",
            TestContext.Current.CancellationToken);
        var junctionPath = Layout.GetExternalCapabilityConfirmationPath(targetId);
        try
        {
            await CreateJunctionAsync(junctionPath, externalRoot);

            var read = await CompatibilityStateStore
                .ReadExternalCapabilityConfirmationAsync(
                    targetId,
                    TestContext.Current.CancellationToken);

            Assert.Equal(CompatibilityStateReadStatus.Corrupt, read.Status);
            await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
                await CompatibilityStateStore.DeleteExternalCapabilityConfirmationAsync(
                    targetId,
                    TestContext.Current.CancellationToken));
            Assert.True(File.Exists(sentinelPath));
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath, recursive: false);
            }

            if (Directory.Exists(externalRoot))
            {
                Directory.Delete(externalRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DiagnosticRejectsSensitiveShapesAndOversizedDocuments()
    {
        var targetId = Guid.Parse("ed1da707-f12a-4085-b6d6-46fd88a360bc");
        var invalidDiagnostic = Diagnostic() with
        {
            RedactedOriginName = "http://10.0.0.8:3080/path?token=secret",
        };
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CompatibilityStateStore.WriteCompatibilityDiagnosticAsync(
                targetId,
                invalidDiagnostic,
                TestContext.Current.CancellationToken));

        await ApplicationDataStore.PrepareTargetDataAsync(
            targetId,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Layout.GetCompatibilityDiagnosticPath(targetId),
            new string('x', 32 * 1024 + 1),
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        var result = await CompatibilityStateStore.ReadCompatibilityDiagnosticAsync(
            targetId,
            TestContext.Current.CancellationToken);

        Assert.Equal(CompatibilityStateReadStatus.Corrupt, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task SessionForgetDeletesBothTargetStatesButPreservesGlobalWatermark()
    {
        var targetId = Guid.Parse("1a953d77-4c6e-4f20-86d9-01a18ecfbb3f");
        await CompatibilityStateStore.AdvanceRegistryWatermarkAsync(
            4,
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.AdvanceRegistryWatermarkAsync(
            5,
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            Confirmation(targetId, DigestA, ExternalCapabilityDecision.Accepted),
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteExternalCapabilityConfirmationAsync(
            Confirmation(targetId, DigestB, ExternalCapabilityDecision.Rejected),
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteCompatibilityDiagnosticAsync(
            targetId,
            Diagnostic(),
            TestContext.Current.CancellationToken);
        await CompatibilityStateStore.WriteCompatibilityDiagnosticAsync(
            targetId,
            Diagnostic() with { ReasonCode = "EXTENSION_REJECTED" },
            TestContext.Current.CancellationToken);

        var sessions = new JsonSessionPort(ApplicationDataStore);
        await sessions.DeleteAsync(
            new TargetId(targetId),
            TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Layout.GetTargetRoot(targetId)));
        Assert.True(File.Exists(Layout.RegistryWatermarkPath));
        Assert.True(File.Exists(Layout.RegistryWatermarkBackupPath));
        var watermark = await CompatibilityStateStore.ReadRegistryWatermarkAsync(
            5,
            TestContext.Current.CancellationToken);
        Assert.Equal(CompatibilityStateReadStatus.Loaded, watermark.Status);
        Assert.Equal(5, watermark.State?.RegistryVersion);
        Assert.Equal(
            CompatibilityStateReadStatus.Missing,
            (await CompatibilityStateStore.ReadExternalCapabilityConfirmationAsync(
                targetId,
                TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            CompatibilityStateReadStatus.Missing,
            (await CompatibilityStateStore.ReadCompatibilityDiagnosticAsync(
                targetId,
                TestContext.Current.CancellationToken)).Status);
    }

    private static ExternalCapabilityConfirmationState Confirmation(
        Guid targetId,
        string digest,
        ExternalCapabilityDecision decision) =>
        new(
            targetId,
            digest,
            MarketPurpose,
            decision,
            new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));

    private static CompatibilityDiagnosticState Diagnostic() =>
        new(
            "1.0.0",
            1,
            1,
            5,
            MarketRuleVersion,
            DigestA,
            "private-target-01",
            "READY",
            CompatibilityLifecyclePhase.Ready);

    private static async Task CreateJunctionAsync(string junction, string target)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junction);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start mklink.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink failed: {await standardOutput} {await standardError}");
        }
    }
}
