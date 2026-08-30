using System.Text.Json;
using System.Text.Json.Serialization;
using DshLauncher.Core;

namespace DshLauncher.Platform.Windows;

public sealed class JsonSessionPort : ISessionPort
{
    public const int CurrentSchemaVersion = 1;
    private const string SessionFileName = "session.json";
    private const string PairingFileName = "pairing-in-progress.json";
    private const int MaximumDocumentBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly ApplicationDataStore _store;

    public JsonSessionPort(ApplicationDataStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<SessionMetadataReadResult> ReadAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(targetId, endpoint);
        var targetRoot = _store.Layout.GetTargetRoot(targetId.Value);
        if (!Directory.Exists(targetRoot))
        {
            return new SessionMetadataReadResult(SessionMetadataStatus.Missing);
        }

        await _store.PrepareTargetDataAsync(targetId.Value, cancellationToken)
            .ConfigureAwait(false);
        var pairingPath = Path.Combine(targetRoot, PairingFileName);
        if (File.Exists(pairingPath))
        {
            return await ReadDocumentAsync(pairingPath, targetId, endpoint, pairing: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var sessionPath = Path.Combine(targetRoot, SessionFileName);
        if (!File.Exists(sessionPath))
        {
            return new SessionMetadataReadResult(SessionMetadataStatus.Missing);
        }

        return await ReadDocumentAsync(sessionPath, targetId, endpoint, pairing: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask MarkPairingInProgressAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(targetId, endpoint);
        await _store.PrepareTargetDataAsync(targetId.Value, cancellationToken)
            .ConfigureAwait(false);
        var document = new SessionDto
        {
            SchemaVersion = CurrentSchemaVersion,
            TargetId = targetId.Value,
            Origin = endpoint.Origin,
            State = "pairing",
            CommittedAtUtc = null,
        };
        await WriteAsync(targetId, PairingFileName, document, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CommitAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(targetId, endpoint);
        await _store.PrepareTargetDataAsync(targetId.Value, cancellationToken)
            .ConfigureAwait(false);
        var document = new SessionDto
        {
            SchemaVersion = CurrentSchemaVersion,
            TargetId = targetId.Value,
            Origin = endpoint.Origin,
            State = "committed",
            CommittedAtUtc = committedAtUtc,
        };
        await WriteAsync(targetId, SessionFileName, document, cancellationToken).ConfigureAwait(false);
        DeleteIfRegular(Path.Combine(_store.Layout.GetTargetRoot(targetId.Value), PairingFileName));
    }

    public async ValueTask DeleteAsync(TargetId targetId, CancellationToken cancellationToken)
    {
        if (targetId.Value == Guid.Empty)
        {
            throw new ArgumentException("Target identity must not be empty.", nameof(targetId));
        }

        var targetRoot = _store.Layout.GetTargetRoot(targetId.Value);
        if (!Directory.Exists(targetRoot))
        {
            return;
        }

        await _store.DeleteTargetDataAsync(targetId.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DeletePairingStateAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        if (targetId.Value == Guid.Empty)
        {
            throw new ArgumentException("Target identity must not be empty.", nameof(targetId));
        }

        var targetRoot = _store.Layout.GetTargetRoot(targetId.Value);
        if (!Directory.Exists(targetRoot))
        {
            return;
        }

        await _store.PrepareTargetDataAsync(targetId.Value, cancellationToken)
            .ConfigureAwait(false);
        DeleteIfRegular(Path.Combine(targetRoot, PairingFileName));
        DeleteIfRegular(Path.Combine(targetRoot, SessionFileName));
    }

    private static async ValueTask<SessionMetadataReadResult> ReadDocumentAsync(
        string path,
        TargetId targetId,
        TargetEndpoint endpoint,
        bool pairing,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await SecureApplicationFile.ReadBoundedAsync(
                path,
                MaximumDocumentBytes,
                cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            if (HasDuplicateObjectProperties(json.RootElement) ||
                !json.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var version))
            {
                return new SessionMetadataReadResult(SessionMetadataStatus.Corrupt);
            }

            if (version > CurrentSchemaVersion)
            {
                return new SessionMetadataReadResult(SessionMetadataStatus.UnknownSchema);
            }

            if (version != CurrentSchemaVersion)
            {
                return new SessionMetadataReadResult(SessionMetadataStatus.Corrupt);
            }

            var document = JsonSerializer.Deserialize<SessionDto>(bytes, JsonOptions);
            if (document is null || document.TargetId != targetId.Value ||
                !string.Equals(document.Origin, endpoint.Origin, StringComparison.Ordinal))
            {
                return new SessionMetadataReadResult(SessionMetadataStatus.OriginMismatch);
            }

            if (pairing)
            {
                return document.State == "pairing" && document.CommittedAtUtc is null
                    ? new SessionMetadataReadResult(SessionMetadataStatus.PairingInProgress)
                    : new SessionMetadataReadResult(SessionMetadataStatus.Corrupt);
            }

            return document.State == "committed" && document.CommittedAtUtc is not null
                ? new SessionMetadataReadResult(SessionMetadataStatus.Committed)
                : new SessionMetadataReadResult(SessionMetadataStatus.Corrupt);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or
                                          ApplicationDataOwnershipException)
        {
            return new SessionMetadataReadResult(SessionMetadataStatus.Corrupt);
        }
    }

    private async ValueTask WriteAsync(
        TargetId targetId,
        string fileName,
        SessionDto document,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        await SecureApplicationFile.WriteAtomicAsync(
            _store.Layout.GetTargetRoot(targetId.Value),
            fileName,
            bytes,
            cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteIfRegular(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        SecureApplicationFile.EnsureRegularFile(path);
        File.Delete(path);
    }

    private static void ValidateIdentity(TargetId targetId, TargetEndpoint endpoint)
    {
        if (targetId.Value == Guid.Empty)
        {
            throw new ArgumentException("Target identity must not be empty.", nameof(targetId));
        }

        ArgumentNullException.ThrowIfNull(endpoint);
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

    private sealed class SessionDto
    {
        public int SchemaVersion { get; init; }
        public Guid TargetId { get; init; }
        public string? Origin { get; init; }
        public string? State { get; init; }
        public DateTimeOffset? CommittedAtUtc { get; init; }
    }
}
