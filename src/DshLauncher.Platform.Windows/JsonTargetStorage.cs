using System.Text.Json;
using System.Text.Json.Serialization;
using DshLauncher.Core;

namespace DshLauncher.Platform.Windows;

public sealed class JsonTargetStorage : ITargetStorage
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private const int TombstoneSchemaVersion = 1;
    private const string TombstonePrefix = "forget-";
    private const string TombstoneSuffix = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly ApplicationDataStore _store;

    public JsonTargetStorage(ApplicationDataStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<TargetCatalogReadResult> ReadCatalogAsync(
        CancellationToken cancellationToken)
    {
        var snapshots = await _store.ReadCatalogSnapshotsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshots.Primary is null && snapshots.Backup is null)
        {
            return new TargetCatalogReadResult(CatalogReadStatus.Missing);
        }

        var primary = ParseCatalog(snapshots.Primary);
        if (primary.Status == CatalogReadStatus.Loaded)
        {
            return primary;
        }

        if (primary.Status == CatalogReadStatus.UnknownSchema)
        {
            return primary;
        }

        var backup = ParseCatalog(snapshots.Backup);
        if (backup.Status == CatalogReadStatus.UnknownSchema)
        {
            return backup;
        }

        if (backup.Status != CatalogReadStatus.Loaded || backup.Document is null ||
            snapshots.Backup is null)
        {
            return new TargetCatalogReadResult(CatalogReadStatus.Corrupt);
        }

        // Two atomic writes leave both primary and backup valid. A crash after the
        // first still leaves a valid primary, so recovery never needs the corrupt file.
        await _store.WriteCatalogSnapshotAsync(snapshots.Backup, cancellationToken)
            .ConfigureAwait(false);
        await _store.WriteCatalogSnapshotAsync(snapshots.Backup, cancellationToken)
            .ConfigureAwait(false);
        return backup with { Status = CatalogReadStatus.RecoveredFromBackup };
    }

    public async ValueTask WriteCatalogAsync(
        TargetCatalogDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document);
        var dto = new CatalogDto
        {
            SchemaVersion = document.SchemaVersion,
            DefaultTargetId = document.DefaultTargetId?.Value,
            Targets = document.Targets.Select(target => new TargetDto
            {
                TargetId = target.TargetId.Value,
                Ipv4 = target.Ipv4,
                Port = target.Port,
                DisplayName = target.DisplayName,
                TrustPolicyVersion = target.TrustPolicyVersion,
                TrustedAtUtc = target.TrustedAtUtc,
            }).ToArray(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        await _store.WriteCatalogSnapshotAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<ForgetTombstone>> ReadForgetTombstonesAsync(
        CancellationToken cancellationToken)
    {
        await SecureApplicationFile.VerifyOwnedRootAsync(_store.Layout, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<ForgetTombstone>();
        foreach (var path in Directory.EnumerateFiles(
                     _store.Layout.RootPath,
                     $"{TombstonePrefix}*{TombstoneSuffix}",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecureApplicationFile.EnsureRegularFile(path);
            var bytes = await SecureApplicationFile.ReadBoundedAsync(
                path,
                MaximumDocumentBytes,
                cancellationToken).ConfigureAwait(false);
            var dto = DeserializeStrict<TombstoneDto>(bytes);
            if (dto is null || dto.SchemaVersion != TombstoneSchemaVersion ||
                dto.TargetId == Guid.Empty ||
                !string.Equals(
                    Path.GetFileName(path),
                    TombstoneFileName(dto.TargetId),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A forget tombstone is invalid.");
            }

            results.Add(new ForgetTombstone(
                new TargetId(dto.TargetId),
                dto.SuccessorTargetId is { } successor ? new TargetId(successor) : null));
        }

        return results;
    }

    public async ValueTask WriteForgetTombstoneAsync(
        ForgetTombstone tombstone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        if (tombstone.TargetId.Value == Guid.Empty ||
            tombstone.SuccessorTargetId?.Value == Guid.Empty ||
            tombstone.SuccessorTargetId == tombstone.TargetId)
        {
            throw new ArgumentException("Forget tombstone identities are invalid.", nameof(tombstone));
        }

        await SecureApplicationFile.VerifyOwnedRootAsync(_store.Layout, cancellationToken)
            .ConfigureAwait(false);
        var dto = new TombstoneDto
        {
            SchemaVersion = TombstoneSchemaVersion,
            TargetId = tombstone.TargetId.Value,
            SuccessorTargetId = tombstone.SuccessorTargetId?.Value,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        await SecureApplicationFile.WriteAtomicAsync(
            _store.Layout.RootPath,
            TombstoneFileName(tombstone.TargetId.Value),
            bytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearForgetTombstoneAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        if (targetId.Value == Guid.Empty)
        {
            throw new ArgumentException("Target identity must not be empty.", nameof(targetId));
        }

        await SecureApplicationFile.VerifyOwnedRootAsync(_store.Layout, cancellationToken)
            .ConfigureAwait(false);
        var path = Path.Combine(_store.Layout.RootPath, TombstoneFileName(targetId.Value));
        if (!File.Exists(path))
        {
            return;
        }

        SecureApplicationFile.EnsureRegularFile(path);
        File.Delete(path);
    }

    private static TargetCatalogReadResult ParseCatalog(byte[]? bytes)
    {
        if (bytes is null)
        {
            return new TargetCatalogReadResult(CatalogReadStatus.Corrupt);
        }

        if (bytes.Length is 0 or > MaximumDocumentBytes)
        {
            return new TargetCatalogReadResult(CatalogReadStatus.Corrupt);
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
                return new TargetCatalogReadResult(CatalogReadStatus.Corrupt);
            }

            if (schemaVersion > TargetManager.CurrentCatalogSchemaVersion)
            {
                return new TargetCatalogReadResult(
                    CatalogReadStatus.UnknownSchema,
                    FoundSchemaVersion: schemaVersion);
            }

            if (schemaVersion != TargetManager.CurrentCatalogSchemaVersion)
            {
                return new TargetCatalogReadResult(CatalogReadStatus.Corrupt);
            }

            var dto = JsonSerializer.Deserialize<CatalogDto>(bytes, JsonOptions);
            if (dto?.Targets is null)
            {
                return new TargetCatalogReadResult(CatalogReadStatus.Corrupt);
            }

            var document = new TargetCatalogDocument(
                dto.SchemaVersion,
                dto.DefaultTargetId is { } defaultId ? new TargetId(defaultId) : null,
                dto.Targets.Select(target => new StoredTarget(
                    new TargetId(target.TargetId),
                    target.Ipv4 ?? string.Empty,
                    target.Port,
                    target.DisplayName,
                    target.TrustPolicyVersion,
                    target.TrustedAtUtc)).ToArray());
            ValidateDocument(document);
            return new TargetCatalogReadResult(CatalogReadStatus.Loaded, document);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or
                                          ArgumentException or OverflowException)
        {
            return new TargetCatalogReadResult(CatalogReadStatus.Corrupt);
        }
    }

    private static T? DeserializeStrict<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            return HasDuplicateObjectProperties(document.RootElement)
                ? default
                : JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
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

    private static void ValidateDocument(TargetCatalogDocument document)
    {
        if (document.SchemaVersion != TargetManager.CurrentCatalogSchemaVersion ||
            document.Targets is null)
        {
            throw new InvalidDataException("The target catalog schema is invalid.");
        }

        var ids = new HashSet<TargetId>();
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in document.Targets)
        {
            if (target.TargetId.Value == Guid.Empty ||
                !ids.Add(target.TargetId) ||
                string.IsNullOrWhiteSpace(target.Ipv4) ||
                target.Port is < 1 or > 65535 ||
                !endpoints.Add($"{target.Ipv4}:{target.Port}") ||
                target.TrustPolicyVersion <= 0 ||
                target.DisplayName is { Length: > 256 })
            {
                throw new InvalidDataException("The target catalog invariant is invalid.");
            }
        }

        if (document.Targets.Count == 0)
        {
            if (document.DefaultTargetId is not null)
            {
                throw new InvalidDataException("An empty catalog cannot have a default target.");
            }
        }
        else if (document.DefaultTargetId is null || !ids.Contains(document.DefaultTargetId.Value))
        {
            throw new InvalidDataException("A non-empty catalog must have one valid default target.");
        }
    }

    private static string TombstoneFileName(Guid targetId) =>
        $"{TombstonePrefix}{targetId:N}{TombstoneSuffix}";

    private sealed class CatalogDto
    {
        public int SchemaVersion { get; init; }
        public Guid? DefaultTargetId { get; init; }
        public TargetDto[]? Targets { get; init; }
    }

    private sealed class TargetDto
    {
        public Guid TargetId { get; init; }
        public string? Ipv4 { get; init; }
        public int Port { get; init; }
        public string? DisplayName { get; init; }
        public int TrustPolicyVersion { get; init; }
        public DateTimeOffset TrustedAtUtc { get; init; }
    }

    private sealed class TombstoneDto
    {
        public int SchemaVersion { get; init; }
        public Guid TargetId { get; init; }
        public Guid? SuccessorTargetId { get; init; }
    }
}

internal static class SecureApplicationFile
{
    public static async ValueTask VerifyOwnedRootAsync(
        ApplicationDataLayout layout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!Directory.Exists(layout.RootPath))
        {
            throw new ApplicationDataOwnershipException("Application data root does not exist.");
        }

        EnsureNotReparsePoint(layout.RootPath);
        EnsureRegularFile(layout.OwnershipMarkerPath);
        var marker = await File.ReadAllTextAsync(layout.OwnershipMarkerPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                marker,
                ApplicationDataLayout.OwnershipMarkerContent,
                StringComparison.Ordinal))
        {
            throw new ApplicationDataOwnershipException("Application data ownership is invalid.");
        }
    }

    public static async ValueTask<byte[]> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        EnsureRegularFile(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length is <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException("Application data document size is invalid.");
        }

        var content = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Application data document size changed while reading.");
        }

        return content;
    }

    public static async ValueTask WriteAtomicAsync(
        string directory,
        string fileName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(directory);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Atomic file name must be a leaf name.", nameof(fileName));
        }

        var destination = Path.Combine(directory, fileName);
        var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                EnsureRegularFile(destination);
                File.Replace(temporary, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void EnsureRegularFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ApplicationDataOwnershipException("Required application data file is missing.");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new ApplicationDataOwnershipException("Application data file is not a regular file.");
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ApplicationDataOwnershipException("Application data path is a reparse point.");
        }
    }
}
