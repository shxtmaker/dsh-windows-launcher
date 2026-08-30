using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-07,VFY-08")]
public sealed class DiagnosticsPolicyTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Timestamp = new(
        2026,
        8,
        30,
        1,
        2,
        3,
        TimeSpan.Zero);

    private readonly string _logRoot = Path.Combine(
        Path.GetTempPath(),
        "DshLauncherDiagnosticTests",
        Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    private readonly List<string> _externalRoots = [];
    private readonly List<string> _hardLinks = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach (var junction in _junctions.AsEnumerable().Reverse())
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }
        }

        foreach (var hardLink in _hardLinks.AsEnumerable().Reverse())
        {
            try
            {
                File.Delete(hardLink);
            }
            catch (FileNotFoundException)
            {
            }
        }

        if (Directory.Exists(_logRoot))
        {
            Directory.Delete(_logRoot, recursive: true);
        }

        foreach (var externalRoot in _externalRoots)
        {
            if (Directory.Exists(externalRoot))
            {
                Directory.Delete(externalRoot, recursive: true);
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public void ExternalErrorsNeverRetainUrisTokensCookiesOrAuthorizationValues()
    {
        const string input =
            "GET http://192.168.1.20:3080/?token=launch-secret " +
            "Authorization: Bearer bearer-secret Cookie: sid=cookie-secret";

        var redacted = DiagnosticRedactor.RedactExternalError(input);

        Assert.DoesNotContain("launch-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("?token=", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("product.json", true)]
    [InlineData("os.json", true)]
    [InlineData("runtime.json", true)]
    [InlineData("dependency-baseline.json", true)]
    [InlineData("target-status.json", true)]
    [InlineData("logs/launcher.log", true)]
    [InlineData("targets.json", false)]
    [InlineData("udf/Cookies", false)]
    [InlineData("../targets.json", false)]
    [InlineData("page.html", false)]
    [InlineData("clipboard.txt", false)]
    public void DiagnosticExportUsesAnExplicitEntryWhitelist(
        string entryName,
        bool expected)
    {
        Assert.Equal(
            expected,
            DiagnosticExportWhitelist.IsAllowed(entryName));
    }

    [Fact]
    public void PrivateEndpointIsIncludedOnlyAfterExplicitConsent()
    {
        var withoutConsent = DiagnosticRecord.Create(
            Timestamp,
            DiagnosticEventCode.TargetProbeClassified,
            statusCode: 401,
            classification: "SupportedAuthenticatedHarness",
            normalizedPrivateEndpoint: "192.168.1.20:3080",
            includePrivateEndpoint: false);
        var withConsent = DiagnosticRecord.Create(
            Timestamp,
            DiagnosticEventCode.TargetProbeClassified,
            statusCode: 401,
            classification: "SupportedAuthenticatedHarness",
            normalizedPrivateEndpoint: "192.168.1.20:3080",
            includePrivateEndpoint: true);

        Assert.Null(withoutConsent.NormalizedPrivateEndpoint);
        Assert.Equal(
            "192.168.1.20:3080",
            withConsent.NormalizedPrivateEndpoint);
    }

    [Fact]
    public void RollingPolicyCannotExceedSevenFilesOrFiveMebibytes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RollingDiagnosticLogPolicy(8, 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RollingDiagnosticLogPolicy(7, (5 * 1024 * 1024) + 1));
    }

    [Fact]
    public async Task DiagnosticExportReaderRejectsOversizedFilesBeforeExport()
    {
        var layout = new ApplicationDataLayout(_logRoot);
        using var applicationData = new ApplicationDataStore(layout);
        await applicationData.InitializeAsync(TestContext.Current.CancellationToken);
        var logDirectory = Path.Combine(layout.RootPath, "logs");
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, "launcher.log");
        var expected = Enumerable.Range(0, 128)
            .Select(index => (byte)index)
            .ToArray();
        await using var log = new RollingDiagnosticLog(
            layout,
            new RollingDiagnosticLogPolicy(maxFiles: 7, maxFileBytes: expected.Length));
        await File.WriteAllBytesAsync(
            path,
            expected,
            TestContext.Current.CancellationToken);

        var exact = await log.ReadForExportAsync(
            0,
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, exact);

        await File.WriteAllBytesAsync(
            path,
            new byte[expected.Length + 1],
            TestContext.Current.CancellationToken);
        var oversized = await log.ReadForExportAsync(
            0,
            TestContext.Current.CancellationToken);

        Assert.Null(oversized);
    }

    [Fact]
    public async Task RollingLogNeverExceedsItsFileCountOrPerFileByteLimit()
    {
        var layout = new ApplicationDataLayout(_logRoot);
        using var applicationData = new ApplicationDataStore(layout);
        await applicationData.InitializeAsync(TestContext.Current.CancellationToken);
        var policy = new RollingDiagnosticLogPolicy(
            maxFiles: 3,
            maxFileBytes: 256);
        await using var log = new RollingDiagnosticLog(layout, policy);

        for (var index = 0; index < 20; index++)
        {
            var record = DiagnosticRecord.Create(
                Timestamp.AddSeconds(index),
                DiagnosticEventCode.TargetProbeClassified,
                statusCode: 401,
                classification: "SupportedAuthenticatedHarness",
                normalizedPrivateEndpoint: null,
                includePrivateEndpoint: false);
            await log.WriteAsync(record, TestContext.Current.CancellationToken);
        }

        var files = Directory.GetFiles(
            Path.Combine(layout.RootPath, "logs"),
            "launcher*.log");
        Assert.InRange(files.Length, 1, 3);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 256));
        var text = string.Join(
            string.Empty,
            files.Select(File.ReadAllText));
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clipboard", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            files,
            file => Assert.True(Encoding.UTF8.GetByteCount(File.ReadAllText(file)) <= 256));
    }

    [Fact]
    public async Task RotationPreservesTheNewestRecordsInOrder()
    {
        var layout = new ApplicationDataLayout(_logRoot);
        using var applicationData = new ApplicationDataStore(layout);
        await applicationData.InitializeAsync(TestContext.Current.CancellationToken);
        await using var log = new RollingDiagnosticLog(
            layout,
            new RollingDiagnosticLogPolicy(maxFiles: 3, maxFileBytes: 256));

        for (var index = 0; index < 5; index++)
        {
            await log.WriteAsync(
                DiagnosticRecord.Create(
                    Timestamp.AddSeconds(index),
                    DiagnosticEventCode.ApplicationStarted,
                    statusCode: null,
                    classification: "startup",
                    normalizedPrivateEndpoint: null,
                    includePrivateEndpoint: false),
                TestContext.Current.CancellationToken);
        }

        var timestamps = new List<DateTimeOffset>();
        for (var index = 0; index < 3; index++)
        {
            var content = await log.ReadForExportAsync(
                index,
                TestContext.Current.CancellationToken);
            Assert.NotNull(content);
            using var document = JsonDocument.Parse(content);
            timestamps.Add(
                document.RootElement.GetProperty("timestampUtc").GetDateTimeOffset());
        }

        Assert.Equal(
            [Timestamp.AddSeconds(4), Timestamp.AddSeconds(3), Timestamp.AddSeconds(2)],
            timestamps);
    }

    [Fact]
    public async Task IndependentRollingLogsCanRotateConcurrently()
    {
        var tasks = Enumerable.Range(0, 8).Select(async worker =>
        {
            var root = Path.Combine(_logRoot, $"worker-{worker}");
            var layout = new ApplicationDataLayout(root);
            using var applicationData = new ApplicationDataStore(layout);
            await applicationData.InitializeAsync(TestContext.Current.CancellationToken);
            await using var log = new RollingDiagnosticLog(
                layout,
                new RollingDiagnosticLogPolicy(maxFiles: 3, maxFileBytes: 256));

            for (var index = 0; index < 20; index++)
            {
                await log.WriteAsync(
                    DiagnosticRecord.Create(
                        Timestamp.AddSeconds(index),
                        DiagnosticEventCode.ApplicationStarted,
                        statusCode: null,
                        classification: "concurrent-rotation",
                        normalizedPrivateEndpoint: null,
                        includePrivateEndpoint: false),
                    TestContext.Current.CancellationToken);
            }
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task LogsJunctionOutsideTheApplicationRootIsNeverReadOrWritten()
    {
        var applicationRoot = Path.Combine(_logRoot, "application");
        var layout = new ApplicationDataLayout(applicationRoot);
        using var applicationData = new ApplicationDataStore(layout);
        await applicationData.InitializeAsync(TestContext.Current.CancellationToken);

        var externalRoot = CreateExternalRoot();
        var externalLog = Path.Combine(externalRoot, "launcher.log");
        await File.WriteAllTextAsync(
            externalLog,
            "must-survive",
            TestContext.Current.CancellationToken);
        var logDirectory = Path.Combine(applicationRoot, "logs");
        await CreateJunctionAsync(logDirectory, externalRoot);

        await using var log = new RollingDiagnosticLog(layout);
        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.WriteAsync(
                DiagnosticRecord.Create(
                    Timestamp,
                    DiagnosticEventCode.ApplicationStarted,
                    statusCode: null,
                    classification: "startup",
                    normalizedPrivateEndpoint: null,
                    includePrivateEndpoint: false),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.ReadForExportAsync(
                0,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "must-survive",
            await File.ReadAllTextAsync(
                externalLog,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplicationRootJunctionIsRejectedEvenWithACopiedOwnershipMarker()
    {
        var externalRoot = CreateExternalRoot();
        await File.WriteAllTextAsync(
            Path.Combine(externalRoot, ApplicationDataLayout.OwnershipMarkerName),
            ApplicationDataLayout.OwnershipMarkerContent,
            TestContext.Current.CancellationToken);
        var externalLogs = Path.Combine(externalRoot, "logs");
        Directory.CreateDirectory(externalLogs);
        var externalLog = Path.Combine(externalLogs, "launcher.log");
        await File.WriteAllTextAsync(
            externalLog,
            "must-survive",
            TestContext.Current.CancellationToken);

        var applicationRoot = Path.Combine(_logRoot, "application");
        await CreateJunctionAsync(applicationRoot, externalRoot);
        var layout = new ApplicationDataLayout(applicationRoot);
        await using var log = new RollingDiagnosticLog(layout);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.WriteAsync(
                CreateStartupRecord(),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.ReadForExportAsync(
                0,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "must-survive",
            await File.ReadAllTextAsync(
                externalLog,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplicationRootWithoutItsExactOwnershipMarkerIsNeverUsedForLogs()
    {
        var applicationRoot = Path.Combine(_logRoot, "application");
        var layout = new ApplicationDataLayout(applicationRoot);
        Directory.CreateDirectory(applicationRoot);
        await File.WriteAllTextAsync(
            layout.OwnershipMarkerPath,
            "foreign-owner",
            TestContext.Current.CancellationToken);
        await using var log = new RollingDiagnosticLog(layout);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.WriteAsync(
                CreateStartupRecord(),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.ReadForExportAsync(
                0,
                TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.Combine(applicationRoot, "logs")));
    }

    [Fact]
    public async Task LogFileHardLinkOutsideTheApplicationRootIsNeverReadOrWritten()
    {
        var applicationRoot = Path.Combine(_logRoot, "application");
        var layout = new ApplicationDataLayout(applicationRoot);
        using var applicationData = new ApplicationDataStore(layout);
        await applicationData.InitializeAsync(TestContext.Current.CancellationToken);
        var logDirectory = Path.Combine(applicationRoot, "logs");
        Directory.CreateDirectory(logDirectory);

        var externalRoot = CreateExternalRoot();
        var externalLog = Path.Combine(externalRoot, "outside.log");
        await File.WriteAllTextAsync(
            externalLog,
            "must-survive",
            TestContext.Current.CancellationToken);
        var hardLink = Path.Combine(logDirectory, "launcher.log");
        await CreateHardLinkAsync(hardLink, externalLog);
        await using var log = new RollingDiagnosticLog(layout);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.WriteAsync(
                CreateStartupRecord(),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.ReadForExportAsync(
                0,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "must-survive",
            await File.ReadAllTextAsync(
                externalLog,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LogsDirectoryExchangedAfterConstructionIsRejectedBeforeTheNextOperation()
    {
        var applicationRoot = Path.Combine(_logRoot, "application");
        var layout = new ApplicationDataLayout(applicationRoot);
        using var applicationData = new ApplicationDataStore(layout);
        await applicationData.InitializeAsync(TestContext.Current.CancellationToken);
        await using var log = new RollingDiagnosticLog(layout);
        await log.WriteAsync(
            CreateStartupRecord(),
            TestContext.Current.CancellationToken);

        var logDirectory = Path.Combine(applicationRoot, "logs");
        Directory.Move(logDirectory, Path.Combine(applicationRoot, "logs.original"));
        var externalRoot = CreateExternalRoot();
        var externalLog = Path.Combine(externalRoot, "launcher.log");
        await File.WriteAllTextAsync(
            externalLog,
            "must-survive",
            TestContext.Current.CancellationToken);
        await CreateJunctionAsync(logDirectory, externalRoot);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.WriteAsync(
                CreateStartupRecord(),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await log.ReadForExportAsync(
                0,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "must-survive",
            await File.ReadAllTextAsync(
                externalLog,
                TestContext.Current.CancellationToken));
    }

    private static DiagnosticRecord CreateStartupRecord() =>
        DiagnosticRecord.Create(
            Timestamp,
            DiagnosticEventCode.ApplicationStarted,
            statusCode: null,
            classification: "startup",
            normalizedPrivateEndpoint: null,
            includePrivateEndpoint: false);

    private string CreateExternalRoot()
    {
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "DshLauncherDiagnosticOutsideTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        _externalRoots.Add(externalRoot);
        return externalRoot;
    }

    private async Task CreateJunctionAsync(string junction, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
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
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink failed: {await standardOutput} {await standardError}");
        }

        _junctions.Add(junction);
    }

    private async Task CreateHardLinkAsync(string hardLink, string target)
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
        startInfo.ArgumentList.Add("/H");
        startInfo.ArgumentList.Add(hardLink);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start mklink.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink failed: {await standardOutput} {await standardError}");
        }

        _hardLinks.Add(hardLink);
    }
}
