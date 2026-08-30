using System.Diagnostics;
using System.Text;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-03,VFY-07")]
public sealed class ApplicationDataStoreTests : IAsyncLifetime
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "DshLauncherTests",
        Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    private readonly List<string> _externalRoots = [];

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

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
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
    public async Task InitializeClaimsAnEmptyRootWithTheExactOwnershipMarker()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        using var store = new ApplicationDataStore(layout);

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ".dsh-windows-launcher-owner",
            Path.GetFileName(layout.OwnershipMarkerPath));
        Assert.Equal(
            "DshWindowsLauncher:v1",
            await File.ReadAllTextAsync(
                layout.OwnershipMarkerPath,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistingNonEmptyRootWithoutAValidMarkerIsNeverClaimed()
    {
        Directory.CreateDirectory(_testRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_testRoot, "foreign.txt"),
            "foreign",
            TestContext.Current.CancellationToken);
        using var store = new ApplicationDataStore(
            new ApplicationDataLayout(_testRoot));

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await store.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(
            Path.Combine(_testRoot, ".dsh-windows-launcher-owner")));
    }

    [Fact]
    public void TargetPathsAreDerivedOnlyFromTheImmutableTargetId()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        var targetId = Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");

        var targetRoot = layout.GetTargetRoot(targetId);

        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(_testRoot),
                "targets",
                "54f02e802c9244d88f7fb0fc118c93fa"),
            targetRoot);
        Assert.Equal(Path.Combine(targetRoot, "udf"), layout.GetUdfPath(targetId));
        Assert.Equal(
            Path.Combine(targetRoot, "external-capability-confirmation.json"),
            layout.GetExternalCapabilityConfirmationPath(targetId));
        Assert.Equal(
            Path.Combine(targetRoot, "external-capability-confirmation.json.bak"),
            layout.GetExternalCapabilityConfirmationBackupPath(targetId));
        Assert.Equal(
            Path.Combine(targetRoot, "compatibility-diagnostic.json"),
            layout.GetCompatibilityDiagnosticPath(targetId));
        Assert.Equal(
            Path.Combine(targetRoot, "compatibility-diagnostic.json.bak"),
            layout.GetCompatibilityDiagnosticBackupPath(targetId));
        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(_testRoot),
                "compatibility-registry-watermark.json"),
            layout.RegistryWatermarkPath);
        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(_testRoot),
                "compatibility-registry-watermark.json.bak"),
            layout.RegistryWatermarkBackupPath);
    }

    [Fact]
    public async Task CatalogWritesAtomicallyKeepExactlyThePreviousValidSnapshot()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        using var store = new ApplicationDataStore(layout);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.WriteCatalogSnapshotAsync(
            Encoding.UTF8.GetBytes("first"),
            TestContext.Current.CancellationToken);
        await store.WriteCatalogSnapshotAsync(
            Encoding.UTF8.GetBytes("second"),
            TestContext.Current.CancellationToken);

        var snapshots = await store.ReadCatalogSnapshotsAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("second", Encoding.UTF8.GetString(snapshots.Primary!));
        Assert.Equal("first", Encoding.UTF8.GetString(snapshots.Backup!));
        Assert.Empty(Directory.EnumerateFiles(_testRoot, "*.tmp"));
    }

    [Fact]
    public async Task ExactTargetDeletionRequiresItsOwnMarkerAndLeavesOtherTargets()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        using var store = new ApplicationDataStore(layout);
        var forgotten = Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");
        var retained = Guid.Parse("eeaa8875-650a-4bbb-b296-d7fb3f441424");
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.PrepareTargetDataAsync(
            forgotten,
            TestContext.Current.CancellationToken);
        await store.PrepareTargetDataAsync(
            retained,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(layout.GetUdfPath(forgotten), "cookie.db"),
            "not-a-real-cookie",
            TestContext.Current.CancellationToken);

        await store.DeleteTargetDataAsync(
            forgotten,
            TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(layout.GetTargetRoot(forgotten)));
        Assert.True(Directory.Exists(layout.GetTargetRoot(retained)));
    }

    [Fact]
    public async Task TargetDirectoryWithoutItsExactMarkerIsNeverRecursivelyDeleted()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        using var store = new ApplicationDataStore(layout);
        var targetId = Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        Directory.CreateDirectory(layout.GetTargetRoot(targetId));
        await File.WriteAllTextAsync(
            Path.Combine(layout.GetTargetRoot(targetId), "foreign.txt"),
            "foreign",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await store.DeleteTargetDataAsync(
                targetId,
                TestContext.Current.CancellationToken));

        Assert.True(File.Exists(
            Path.Combine(layout.GetTargetRoot(targetId), "foreign.txt")));
    }

    [Fact]
    public async Task TargetsJunctionOutsideTheApplicationRootIsNeverFollowed()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        using var store = new ApplicationDataStore(layout);
        var targetId = Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        var externalTargetsRoot = CreateExternalRoot();
        var externalTargetRoot = Path.Combine(
            externalTargetsRoot,
            targetId.ToString("N"));
        var externalUdf = Path.Combine(externalTargetRoot, "udf");
        Directory.CreateDirectory(externalUdf);
        await File.WriteAllTextAsync(
            Path.Combine(
                externalTargetRoot,
                ApplicationDataLayout.TargetOwnershipMarkerName),
            ApplicationDataLayout.GetTargetOwnershipMarkerContent(targetId),
            TestContext.Current.CancellationToken);
        var externalCookie = Path.Combine(externalUdf, "cookie.db");
        await File.WriteAllTextAsync(
            externalCookie,
            "must-survive",
            TestContext.Current.CancellationToken);
        await CreateJunctionAsync(layout.TargetsRoot, externalTargetsRoot);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await store.DeleteTargetDataAsync(
                targetId,
                TestContext.Current.CancellationToken));

        Assert.True(File.Exists(externalCookie));
        Assert.True(Directory.Exists(externalTargetRoot));
    }

    [Fact]
    public async Task TargetRootJunctionOutsideTheTargetsRootIsNeverFollowed()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        using var store = new ApplicationDataStore(layout);
        var targetId = Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        Directory.CreateDirectory(layout.TargetsRoot);

        var externalTargetRoot = CreateExternalRoot();
        Directory.CreateDirectory(Path.Combine(externalTargetRoot, "udf"));
        await File.WriteAllTextAsync(
            Path.Combine(
                externalTargetRoot,
                ApplicationDataLayout.TargetOwnershipMarkerName),
            ApplicationDataLayout.GetTargetOwnershipMarkerContent(targetId),
            TestContext.Current.CancellationToken);
        var externalCookie = Path.Combine(externalTargetRoot, "udf", "cookie.db");
        await File.WriteAllTextAsync(
            externalCookie,
            "must-survive",
            TestContext.Current.CancellationToken);
        await CreateJunctionAsync(
            layout.GetTargetRoot(targetId),
            externalTargetRoot);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await store.DeleteTargetDataAsync(
                targetId,
                TestContext.Current.CancellationToken));

        Assert.True(File.Exists(externalCookie));
        Assert.True(Directory.Exists(externalTargetRoot));
    }

    [Fact]
    public async Task UdfJunctionOutsideTheTargetRootIsNeverFollowed()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        using var store = new ApplicationDataStore(layout);
        var targetId = Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.PrepareTargetDataAsync(
            targetId,
            TestContext.Current.CancellationToken);
        Directory.Delete(layout.GetUdfPath(targetId), recursive: false);

        var externalUdf = CreateExternalRoot();
        var externalCookie = Path.Combine(externalUdf, "cookie.db");
        await File.WriteAllTextAsync(
            externalCookie,
            "must-survive",
            TestContext.Current.CancellationToken);
        await CreateJunctionAsync(layout.GetUdfPath(targetId), externalUdf);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await store.TargetUdfExistsAsync(
                targetId,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(async () =>
            await store.DeleteTargetUdfAsync(
                targetId,
                TestContext.Current.CancellationToken));

        Assert.True(File.Exists(externalCookie));
        Assert.True(Directory.Exists(externalUdf));
    }

    [Fact]
    public async Task NormalTargetAndUdfPathsKeepTheirExistingLifecycleSemantics()
    {
        var layout = new ApplicationDataLayout(_testRoot);
        using var store = new ApplicationDataStore(layout);
        var targetId = Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.PrepareTargetDataAsync(
            targetId,
            TestContext.Current.CancellationToken);

        Assert.True(await store.TargetUdfExistsAsync(
            targetId,
            TestContext.Current.CancellationToken));
        await store.DeleteTargetUdfAsync(
            targetId,
            TestContext.Current.CancellationToken);
        Assert.False(await store.TargetUdfExistsAsync(
            targetId,
            TestContext.Current.CancellationToken));

        await store.PrepareTargetDataAsync(
            targetId,
            TestContext.Current.CancellationToken);
        await store.DeleteTargetDataAsync(
            targetId,
            TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(layout.GetTargetRoot(targetId)));
    }

    private string CreateExternalRoot()
    {
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "DshLauncherOutsideTests",
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

        _junctions.Add(junction);
    }
}
