using System.Globalization;
using DshLauncher.Core.Hub;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-06")]
public sealed class JsonTargetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dsh-hub-store-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ApplicationDataStore _dataStore;
    private readonly JsonTargetStore _store;

    public JsonTargetStoreTests()
    {
        _dataStore = new ApplicationDataStore(new ApplicationDataLayout(_root));
        _dataStore.InitializeAsync().AsTask().GetAwaiter().GetResult();
        _store = new JsonTargetStore(_dataStore);
    }

    public void Dispose()
    {
        _dataStore.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingDocumentReadsAsMissing()
    {
        var result = await _store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HubStorageStatus.Missing, result.Status);
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsTheWholeDirectory()
    {
        var document = new StoredHubDocument(StoredHubDocument.CurrentSchemaVersion,
        [
            new StoredTarget(
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "http://192.168.10.8:3080",
                "工作台",
                "dsh_pair",
                "device-1",
                DateTimeOffset.Parse("2026-09-01T12:00:00Z", CultureInfo.InvariantCulture).ToOffset(TimeSpan.Zero),
                KeepAliveEnabled: true),
            new StoredTarget(
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                "https://tunnel.example.com",
                null,
                null,
                null,
                null,
                KeepAliveEnabled: true),
        ]);

        await _store.SaveAsync(document, TestContext.Current.CancellationToken);
        var result = await _store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HubStorageStatus.Loaded, result.Status);
        var loaded = result.Document!;
        Assert.Equal(2, loaded.Targets.Count);
        Assert.Equal("http://192.168.10.8:3080", loaded.Targets[0].BaseUrl);
        Assert.Equal("device-1", loaded.Targets[0].DeviceId);
        Assert.True(loaded.Targets[0].HasCredential);
        Assert.False(loaded.Targets[1].HasCredential);
        Assert.Equal("工作台", loaded.Targets[0].DisplayName);
    }

    [Fact]
    public async Task ACorruptPrimaryFallsBackToTheBackup()
    {
        var document = new StoredHubDocument(StoredHubDocument.CurrentSchemaVersion,
        [
            new StoredTarget(
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "http://192.168.10.8:3080",
                null,
                "dsh_pair",
                "device-1",
                DateTimeOffset.UtcNow,
                KeepAliveEnabled: true),
        ]);
        // Two writes rotate the first document into the backup slot.
        await _store.SaveAsync(document, TestContext.Current.CancellationToken);
        await _store.SaveAsync(document, TestContext.Current.CancellationToken);

        // Corrupt the primary in place; the backup still holds a good copy.
        var layout = new ApplicationDataLayout(_root);
        await File.WriteAllTextAsync(layout.HubDocumentPath, "{ not json", TestContext.Current.CancellationToken);

        var result = await _store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HubStorageStatus.Loaded, result.Status);
        Assert.Single(result.Document!.Targets);
    }

    [Fact]
    public async Task DuplicateUrlsAreRejected()
    {
        var document = new StoredHubDocument(StoredHubDocument.CurrentSchemaVersion,
        [
            new StoredTarget(
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "http://192.168.10.8:3080",
                null, null, null, null, KeepAliveEnabled: true),
            new StoredTarget(
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                "http://192.168.10.8:3080",
                null, null, null, null, KeepAliveEnabled: true),
        ]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.SaveAsync(document, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task APartiallyPairedTargetIsRejected()
    {
        var document = new StoredHubDocument(StoredHubDocument.CurrentSchemaVersion,
        [
            new StoredTarget(
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                "http://192.168.10.8:3080",
                null,
                "dsh_pair",
                null,
                null,
                KeepAliveEnabled: true),
        ]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.SaveAsync(document, TestContext.Current.CancellationToken).AsTask());
    }
}
