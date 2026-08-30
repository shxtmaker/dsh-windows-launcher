using System.Text;
using DshLauncher.Core;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-03")]
public sealed class JsonTargetStorageTests : IAsyncLifetime
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "DshLauncherJsonTargetStorageTests",
        Guid.NewGuid().ToString("N"));

    private ApplicationDataStore? _store;
    private JsonTargetStorage? _storage;

    private ApplicationDataLayout Layout => _store?.Layout ??
        throw new InvalidOperationException("The test store has not been initialized.");

    private JsonTargetStorage Storage => _storage ??
        throw new InvalidOperationException("The target storage has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        _store = new ApplicationDataStore(new ApplicationDataLayout(_testRoot));
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        _storage = new JsonTargetStorage(_store);
    }

    public ValueTask DisposeAsync()
    {
        _store?.Dispose();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CorruptPrimaryRecoversThePreviousValidSnapshotAndRepairsBothCopies()
    {
        var first = Catalog(
            Guid.Parse("03fc55ba-0425-44e6-93ed-69bd0742a297"),
            "192.168.1.20",
            3080);
        var second = Catalog(
            Guid.Parse("45423129-9434-418b-9ef7-73e8c4d10272"),
            "192.168.1.21",
            3180);
        await Storage.WriteCatalogAsync(first, TestContext.Current.CancellationToken);
        await Storage.WriteCatalogAsync(second, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Layout.CatalogPath,
            "{corrupt-primary",
            TestContext.Current.CancellationToken);

        var recovered = await Storage.ReadCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CatalogReadStatus.RecoveredFromBackup, recovered.Status);
        Assert.Equal(first.DefaultTargetId, recovered.Document?.DefaultTargetId);
        var snapshots = await _store!.ReadCatalogSnapshotsAsync(
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshots.Primary);
        Assert.Equal(snapshots.Primary, snapshots.Backup);

        var repairedRead = await Storage.ReadCatalogAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CatalogReadStatus.Loaded, repairedRead.Status);
        Assert.Equal(first.DefaultTargetId, repairedRead.Document?.DefaultTargetId);
    }

    [Fact]
    public async Task UnknownPrimarySchemaBlocksFallbackAndPreservesBothOriginalSnapshots()
    {
        var known = Catalog(
            Guid.Parse("22e68720-d2a0-4e90-a6ae-9f12516d20c6"),
            "10.20.30.40",
            3080);
        await Storage.WriteCatalogAsync(known, TestContext.Current.CancellationToken);
        var unknown = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":99,\"defaultTargetId\":null,\"targets\":[]}");
        await _store!.WriteCatalogSnapshotAsync(
            unknown,
            TestContext.Current.CancellationToken);
        var before = await _store.ReadCatalogSnapshotsAsync(TestContext.Current.CancellationToken);

        var result = await Storage.ReadCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CatalogReadStatus.UnknownSchema, result.Status);
        Assert.Equal(99, result.FoundSchemaVersion);
        var after = await _store.ReadCatalogSnapshotsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before.Primary, after.Primary);
        Assert.Equal(before.Backup, after.Backup);
    }

    [Fact]
    public async Task CorruptPrimaryAndBackupAreReportedWithoutRewritingEitherCopy()
    {
        await _store!.WriteCatalogSnapshotAsync(
            "{first-corrupt"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await _store.WriteCatalogSnapshotAsync(
            "{second-corrupt"u8.ToArray(),
            TestContext.Current.CancellationToken);
        var before = await _store.ReadCatalogSnapshotsAsync(TestContext.Current.CancellationToken);

        var result = await Storage.ReadCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CatalogReadStatus.Corrupt, result.Status);
        Assert.Null(result.Document);
        var after = await _store.ReadCatalogSnapshotsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before.Primary, after.Primary);
        Assert.Equal(before.Backup, after.Backup);
    }

    [Fact]
    public async Task WritingAForgetSnapshotTwicePreventsThePreviousTargetFromBackupRecovery()
    {
        var removed = Catalog(
            Guid.Parse("83acf98d-55a2-447b-879b-e8c59e99e6aa"),
            "10.20.30.41",
            3080);
        var retained = Catalog(
            Guid.Parse("ec3785f8-34ab-4c6b-a406-e988237494d0"),
            "10.20.30.42",
            3080);
        await Storage.WriteCatalogAsync(removed, TestContext.Current.CancellationToken);
        await Storage.WriteCatalogAsync(retained, TestContext.Current.CancellationToken);
        await Storage.WriteCatalogAsync(retained, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Layout.CatalogPath,
            "{corrupt-primary",
            TestContext.Current.CancellationToken);

        var recovered = await Storage.ReadCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CatalogReadStatus.RecoveredFromBackup, recovered.Status);
        Assert.Equal(retained.DefaultTargetId, recovered.Document?.DefaultTargetId);
        Assert.DoesNotContain(
            recovered.Document?.Targets ?? [],
            target => target.TargetId == removed.DefaultTargetId);
    }

    [Fact]
    public async Task ForgetTombstoneRoundTripsExactIdentitiesAndCanBeClearedIdempotently()
    {
        var targetId = new TargetId(
            Guid.Parse("88931586-36a3-476d-b22c-9a790a8f9d1b"));
        var successorId = new TargetId(
            Guid.Parse("1bec2821-4017-45ad-ac66-d838f23c99c3"));
        var tombstone = new ForgetTombstone(targetId, successorId);

        await Storage.WriteForgetTombstoneAsync(
            tombstone,
            TestContext.Current.CancellationToken);
        var stored = Assert.Single(await Storage.ReadForgetTombstonesAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(tombstone, stored);
        Assert.Equal(
            $"forget-{targetId.Value:N}.json",
            Assert.Single(Directory.EnumerateFiles(_testRoot, "forget-*.json")) is { } path
                ? Path.GetFileName(path)
                : null);

        await Storage.ClearForgetTombstoneAsync(
            targetId,
            TestContext.Current.CancellationToken);
        await Storage.ClearForgetTombstoneAsync(
            targetId,
            TestContext.Current.CancellationToken);
        Assert.Empty(await Storage.ReadForgetTombstonesAsync(
            TestContext.Current.CancellationToken));
    }

    private static TargetCatalogDocument Catalog(Guid targetId, string ipv4, int port)
    {
        var id = new TargetId(targetId);
        return new TargetCatalogDocument(
            TargetManager.CurrentCatalogSchemaVersion,
            id,
            new[]
            {
                new StoredTarget(
                    id,
                    ipv4,
                    port,
                    null,
                    TargetManager.CurrentTrustPolicyVersion,
                    new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero)),
            });
    }
}
