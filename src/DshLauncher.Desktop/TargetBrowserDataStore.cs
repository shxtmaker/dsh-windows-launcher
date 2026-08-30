using System.IO;
using DshLauncher.Core;
using DshLauncher.Platform.Windows;
using DshLauncher.WebView;

namespace DshLauncher.Desktop;

public sealed class TargetBrowserDataStore : ITargetBrowserDataStore
{
    private readonly ApplicationDataStore _store;

    public TargetBrowserDataStore(ApplicationDataStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string GetUserDataFolder(TargetId targetId) =>
        _store.Layout.GetUdfPath(Validate(targetId));

    public ValueTask PrepareAsync(TargetId targetId, CancellationToken cancellationToken) =>
        _store.PrepareTargetDataAsync(Validate(targetId), cancellationToken);

    public async ValueTask VerifyAsync(TargetId targetId, CancellationToken cancellationToken)
    {
        if (!await _store.TargetUdfExistsAsync(Validate(targetId), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new IOException("Target browser data is missing.");
        }
    }

    public ValueTask DeleteAsync(TargetId targetId, CancellationToken cancellationToken) =>
        _store.DeleteTargetUdfAsync(Validate(targetId), cancellationToken);

    public ValueTask<bool> ExistsAsync(TargetId targetId, CancellationToken cancellationToken) =>
        _store.TargetUdfExistsAsync(Validate(targetId), cancellationToken);

    private static Guid Validate(TargetId targetId)
    {
        if (targetId.Value == Guid.Empty)
        {
            throw new ArgumentException("Target identity must not be empty.", nameof(targetId));
        }

        return targetId.Value;
    }
}
