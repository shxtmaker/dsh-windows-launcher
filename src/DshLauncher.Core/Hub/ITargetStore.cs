using DshLauncher.Core.Hub;

namespace DshLauncher.Core.Hub;

public enum HubStorageStatus
{
    Loaded,
    Missing,
    Corrupt,
}

public sealed record HubStorageReadResult(HubStorageStatus Status, StoredHubDocument? Document = null);

/// <summary>
/// Durable target store port. Implementations persist the hub document
/// atomically (primary + backup) in the application data root.
/// </summary>
public interface ITargetStore
{
    ValueTask<HubStorageReadResult> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(StoredHubDocument document, CancellationToken cancellationToken = default);
}

/// <summary>Raised when the store cannot persist or load safely.</summary>
public sealed class HubStorageException : InvalidOperationException
{
    public HubStorageException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
