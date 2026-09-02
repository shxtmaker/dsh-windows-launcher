using System.Text.Json;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-06")]
public sealed class ApplicationDataStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dsh-hub-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ApplicationDataStore _store;

    public ApplicationDataStoreTests()
    {
        _store = new ApplicationDataStore(new ApplicationDataLayout(_root));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeCreatesTheRootWithAnOwnershipMarker()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ApplicationDataLayout.OwnershipMarkerContent,
            await File.ReadAllTextAsync(new ApplicationDataLayout(_root).OwnershipMarkerPath,
                TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task InitializeIsIdempotentOnAnOwnedRoot()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InitializeRefusesAForeignNonEmptyRoot()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "some-foreign-file.txt"),
            "not ours",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(
            () => _store.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task InitializeAdoptsAV1RootByUpgradingTheMarkerInPlace()
    {
        var layout = new ApplicationDataLayout(_root);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            layout.OwnershipMarkerPath,
            ApplicationDataLayout.LegacyOwnershipMarkerContent,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "targets.json"),
            "{ old v1 payload }",
            TestContext.Current.CancellationToken);

        await _store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ApplicationDataLayout.OwnershipMarkerContent,
            await File.ReadAllTextAsync(layout.OwnershipMarkerPath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(_root, "targets.json")));
        await _store.WriteDocumentSnapshotAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
        var snapshots = await _store.ReadDocumentSnapshotsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1 }, snapshots.Primary);
    }

    [Fact]
    public async Task InitializeRefusesAnUnknownMarkerGeneration()
    {
        var layout = new ApplicationDataLayout(_root);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            layout.OwnershipMarkerPath,
            "DshWindowsLauncher:v99",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ApplicationDataOwnershipException>(
            () => _store.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task DocumentWritesRotateThePrimaryIntoTheBackupSlot()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        var layout = new ApplicationDataLayout(_root);

        await _store.WriteDocumentSnapshotAsync(
            JsonSerializer.SerializeToUtf8Bytes(new { version = 1 }),
            TestContext.Current.CancellationToken);
        await _store.WriteDocumentSnapshotAsync(
            JsonSerializer.SerializeToUtf8Bytes(new { version = 2 }),
            TestContext.Current.CancellationToken);

        var primary = await File.ReadAllTextAsync(layout.HubDocumentPath, TestContext.Current.CancellationToken);
        var backup = await File.ReadAllTextAsync(layout.HubDocumentBackupPath, TestContext.Current.CancellationToken);
        Assert.Contains("\"version\":2", primary.Replace(" ", string.Empty));
        Assert.Contains("\"version\":1", backup.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task DocumentReadsReturnPrimaryAndBackupSnapshots()
    {
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        await _store.WriteDocumentSnapshotAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);

        var snapshots = await _store.ReadDocumentSnapshotsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2, 3 }, snapshots.Primary);
        Assert.Null(snapshots.Backup);
    }
}
