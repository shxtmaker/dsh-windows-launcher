using System.Text.Json;
using System.Text.Json.Serialization;
using DshLauncher.Core.Hub;

namespace DshLauncher.Platform.Windows;

/// <summary>
/// JSON persistence of the hub document in the owned application data root.
/// Reads tolerate a missing file; a corrupt primary falls back to the
/// backup, and unknown schemas surface as their own status so a newer
/// build's data is never silently reinterpreted.
/// </summary>
public sealed class JsonTargetStore : ITargetStore
{
    private const int MaximumDocumentBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly ApplicationDataStore _store;

    public JsonTargetStore(ApplicationDataStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<HubStorageReadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await _store.ReadDocumentSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshots.Primary is null && snapshots.Backup is null)
        {
            return new HubStorageReadResult(HubStorageStatus.Missing);
        }

        var primary = ParseDocument(snapshots.Primary);
        if (primary.Status != HubStorageStatus.Corrupt)
        {
            return primary;
        }

        var backup = ParseDocument(snapshots.Backup);
        if (backup.Status != HubStorageStatus.Corrupt)
        {
            return backup;
        }

        return new HubStorageReadResult(HubStorageStatus.Corrupt);
    }

    public async ValueTask SaveAsync(StoredHubDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        await _store.WriteDocumentSnapshotAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static HubStorageReadResult ParseDocument(byte[]? bytes)
    {
        if (bytes is null)
        {
            return new HubStorageReadResult(HubStorageStatus.Corrupt);
        }

        if (bytes.Length is 0 or > MaximumDocumentBytes)
        {
            return new HubStorageReadResult(HubStorageStatus.Corrupt);
        }

        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (HasDuplicateObjectProperties(json.RootElement) ||
                !json.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return new HubStorageReadResult(HubStorageStatus.Corrupt);
            }

            if (schemaVersion > StoredHubDocument.CurrentSchemaVersion)
            {
                return new HubStorageReadResult(HubStorageStatus.Corrupt);
            }

            var document = JsonSerializer.Deserialize<StoredHubDocument>(bytes, JsonOptions);
            if (document is null || document.Targets is null ||
                document.SchemaVersion != StoredHubDocument.CurrentSchemaVersion)
            {
                return new HubStorageReadResult(HubStorageStatus.Corrupt);
            }

            ValidateDocument(document);
            return new HubStorageReadResult(HubStorageStatus.Loaded, document);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or
                                          ArgumentException or OverflowException or InvalidOperationException)
        {
            return new HubStorageReadResult(HubStorageStatus.Corrupt);
        }
    }

    private static void ValidateDocument(StoredHubDocument document)
    {
        if (document.SchemaVersion != StoredHubDocument.CurrentSchemaVersion || document.Targets is null)
        {
            throw new InvalidDataException("The hub document schema is invalid.");
        }

        var ids = new HashSet<Guid>();
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in document.Targets)
        {
            if (target.TargetId == Guid.Empty || !ids.Add(target.TargetId) ||
                !Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !urls.Add(target.BaseUrl) ||
                target.DisplayName is { Length: > 256 } ||
                (target.CookieName is null) != (target.DeviceId is null) ||
                target.HasCredential != (target.PairedAtUtc is not null))
            {
                throw new InvalidDataException("The hub document invariant is invalid.");
            }
        }
    }

    private static bool HasDuplicateObjectProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateObjectProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateObjectProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
