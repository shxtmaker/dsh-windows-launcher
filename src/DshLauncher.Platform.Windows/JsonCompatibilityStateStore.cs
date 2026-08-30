using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshLauncher.Platform.Windows;

public enum CompatibilityStateReadStatus
{
    Missing,
    Loaded,
    RecoveredFromBackup,
    UnknownSchema,
    Corrupt,
    RegistryRollbackDetected,
}

public enum ExternalCapabilityDecision
{
    Unspecified,
    Accepted,
    Rejected,
}

public enum CompatibilityLifecyclePhase
{
    Unspecified,
    Initializing,
    Loading,
    Ready,
    Recovering,
    Blocked,
    Failed,
}

public sealed record RegistryWatermarkState(int RegistryVersion);

public sealed record RegistryWatermarkReadResult(
    CompatibilityStateReadStatus Status,
    RegistryWatermarkState? State = null,
    int? FoundSchemaVersion = null,
    bool RecoveredFromBackup = false);

public sealed record ExternalCapabilityConfirmationState(
    Guid TargetId,
    string SnapshotDigest,
    IReadOnlyList<string> NormalizedPurposes,
    ExternalCapabilityDecision Decision,
    DateTimeOffset DecidedAtUtc);

public sealed record ExternalCapabilityConfirmationReadResult(
    CompatibilityStateReadStatus Status,
    ExternalCapabilityConfirmationState? State = null,
    int? FoundSchemaVersion = null);

public sealed record CompatibilityDiagnosticState(
    string ContractVersion,
    int DescriptorSchemaVersion,
    int RegistrySchemaVersion,
    int RegistryVersion,
    IReadOnlyList<string> RuleVersions,
    string SnapshotDigest,
    string RedactedOriginName,
    string ReasonCode,
    CompatibilityLifecyclePhase Phase);

public sealed record CompatibilityDiagnosticReadResult(
    CompatibilityStateReadStatus Status,
    CompatibilityDiagnosticState? State = null,
    int? FoundSchemaVersion = null);

public sealed class JsonCompatibilityStateStore
{
    public const int RegistryWatermarkSchemaVersion = 1;
    public const int ExternalCapabilityConfirmationSchemaVersion = 1;
    public const int CompatibilityDiagnosticSchemaVersion = 1;

    private const string RegistryWatermarkFileName =
        "compatibility-registry-watermark.json";
    private const string RegistryWatermarkBackupFileName =
        "compatibility-registry-watermark.json.bak";
    private const string ConfirmationFileName =
        "external-capability-confirmation.json";
    private const string ConfirmationBackupFileName =
        "external-capability-confirmation.json.bak";
    private const string DiagnosticFileName = "compatibility-diagnostic.json";
    private const string DiagnosticBackupFileName =
        "compatibility-diagnostic.json.bak";
    private const int MaximumWatermarkBytes = 4 * 1024;
    private const int MaximumConfirmationBytes = 32 * 1024;
    private const int MaximumDiagnosticBytes = 32 * 1024;
    private const int MaximumPurposes = 64;
    private const int MaximumPurposeCharacters = 512;
    private const int MaximumRuleVersions = 64;
    private const int MaximumRuleVersionCharacters = 128;
    private const int MaximumVersionCharacters = 64;
    private const int MaximumOriginNameCharacters = 128;
    private const int MaximumReasonCodeCharacters = 64;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ApplicationDataStore _store;

    public JsonCompatibilityStateStore(ApplicationDataStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<RegistryWatermarkReadResult> ReadRegistryWatermarkAsync(
        int currentRegistryVersion,
        CancellationToken cancellationToken = default)
    {
        ValidatePositiveVersion(currentRegistryVersion, nameof(currentRegistryVersion));
        await _store.CompatibilityStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await ReadDocumentAsync(
                StateLocation.Application(
                    RegistryWatermarkFileName,
                    RegistryWatermarkBackupFileName,
                    MaximumWatermarkBytes),
                ParseRegistryWatermark,
                allowBackupRecovery: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (outcome.State is { } state &&
                state.RegistryVersion > currentRegistryVersion)
            {
                return new RegistryWatermarkReadResult(
                    CompatibilityStateReadStatus.RegistryRollbackDetected,
                    state,
                    outcome.FoundSchemaVersion,
                    outcome.RecoveredFromBackup);
            }

            return new RegistryWatermarkReadResult(
                outcome.Status,
                outcome.State,
                outcome.FoundSchemaVersion,
                outcome.RecoveredFromBackup);
        }
        finally
        {
            _store.CompatibilityStateGate.Release();
        }
    }

    public async ValueTask AdvanceRegistryWatermarkAsync(
        int registryVersion,
        CancellationToken cancellationToken = default)
    {
        ValidatePositiveVersion(registryVersion, nameof(registryVersion));
        await _store.CompatibilityStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var location = StateLocation.Application(
                RegistryWatermarkFileName,
                RegistryWatermarkBackupFileName,
                MaximumWatermarkBytes);
            var outcome = await ReadDocumentAsync(
                location,
                ParseRegistryWatermark,
                allowBackupRecovery: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureWritable(outcome.Status, "registry watermark");
            if (outcome.State is { } existing)
            {
                if (existing.RegistryVersion > registryVersion)
                {
                    throw new InvalidOperationException(
                        "Registry watermark cannot move backwards.");
                }

                if (existing.RegistryVersion == registryVersion)
                {
                    return;
                }
            }

            var dto = new RegistryWatermarkDto
            {
                SchemaVersion = RegistryWatermarkSchemaVersion,
                RegistryVersion = registryVersion,
            };
            await WriteDocumentAsync(
                location,
                JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions),
                preserveExistingBackup: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _store.CompatibilityStateGate.Release();
        }
    }

    public async ValueTask<ExternalCapabilityConfirmationReadResult>
        ReadExternalCapabilityConfirmationAsync(
            Guid targetId,
            CancellationToken cancellationToken = default)
    {
        ValidateTargetId(targetId);
        await _store.CompatibilityStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await ReadDocumentAsync(
                StateLocation.Target(
                    targetId,
                    ConfirmationFileName,
                    ConfirmationBackupFileName,
                    MaximumConfirmationBytes),
                bytes => ParseConfirmation(bytes, targetId),
                allowBackupRecovery: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ExternalCapabilityConfirmationReadResult(
                outcome.Status,
                outcome.State,
                outcome.FoundSchemaVersion);
        }
        finally
        {
            _store.CompatibilityStateGate.Release();
        }
    }

    public async ValueTask WriteExternalCapabilityConfirmationAsync(
        ExternalCapabilityConfirmationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateTargetId(state.TargetId);
        var normalizedState = NormalizeConfirmation(state);
        await _store.CompatibilityStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var location = StateLocation.Target(
                state.TargetId,
                ConfirmationFileName,
                ConfirmationBackupFileName,
                MaximumConfirmationBytes);
            var outcome = await ReadDocumentAsync(
                location,
                bytes => ParseConfirmation(bytes, state.TargetId),
                allowBackupRecovery: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureWritable(outcome.Status, "external capability confirmation");
            var dto = new ConfirmationDto
            {
                SchemaVersion = ExternalCapabilityConfirmationSchemaVersion,
                TargetId = normalizedState.TargetId,
                SnapshotDigest = normalizedState.SnapshotDigest,
                NormalizedPurposes = normalizedState.NormalizedPurposes.ToArray(),
                Decision = normalizedState.Decision,
                DecidedAtUtc = normalizedState.DecidedAtUtc,
            };
            await WriteDocumentAsync(
                location,
                JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions),
                preserveExistingBackup: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _store.CompatibilityStateGate.Release();
        }
    }

    public async ValueTask DeleteExternalCapabilityConfirmationAsync(
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetId(targetId);
        await _store.CompatibilityStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.DeleteTargetStateSnapshotsAsync(
                targetId,
                ConfirmationFileName,
                ConfirmationBackupFileName,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _store.CompatibilityStateGate.Release();
        }
    }

    public async ValueTask<CompatibilityDiagnosticReadResult>
        ReadCompatibilityDiagnosticAsync(
            Guid targetId,
            CancellationToken cancellationToken = default)
    {
        ValidateTargetId(targetId);
        await _store.CompatibilityStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await ReadDocumentAsync(
                StateLocation.Target(
                    targetId,
                    DiagnosticFileName,
                    DiagnosticBackupFileName,
                    MaximumDiagnosticBytes),
                ParseDiagnostic,
                allowBackupRecovery: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new CompatibilityDiagnosticReadResult(
                outcome.Status,
                outcome.State,
                outcome.FoundSchemaVersion);
        }
        finally
        {
            _store.CompatibilityStateGate.Release();
        }
    }

    public async ValueTask WriteCompatibilityDiagnosticAsync(
        Guid targetId,
        CompatibilityDiagnosticState state,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetId(targetId);
        ArgumentNullException.ThrowIfNull(state);
        var normalizedState = NormalizeDiagnostic(state);
        await _store.CompatibilityStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var location = StateLocation.Target(
                targetId,
                DiagnosticFileName,
                DiagnosticBackupFileName,
                MaximumDiagnosticBytes);
            var outcome = await ReadDocumentAsync(
                location,
                ParseDiagnostic,
                allowBackupRecovery: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureWritable(outcome.Status, "compatibility diagnostic");
            var dto = new DiagnosticDto
            {
                SchemaVersion = CompatibilityDiagnosticSchemaVersion,
                ContractVersion = normalizedState.ContractVersion,
                DescriptorSchemaVersion = normalizedState.DescriptorSchemaVersion,
                RegistrySchemaVersion = normalizedState.RegistrySchemaVersion,
                RegistryVersion = normalizedState.RegistryVersion,
                RuleVersions = normalizedState.RuleVersions.ToArray(),
                SnapshotDigest = normalizedState.SnapshotDigest,
                RedactedOriginName = normalizedState.RedactedOriginName,
                ReasonCode = normalizedState.ReasonCode,
                Phase = normalizedState.Phase,
            };
            await WriteDocumentAsync(
                location,
                JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions),
                preserveExistingBackup: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _store.CompatibilityStateGate.Release();
        }
    }

    private async ValueTask<ReadOutcome<T>> ReadDocumentAsync<T>(
        StateLocation location,
        Func<byte[]?, ParsedDocument<T>> parse,
        bool allowBackupRecovery,
        CancellationToken cancellationToken)
        where T : class
    {
        ApplicationStateSnapshots snapshots;
        try
        {
            snapshots = await ReadSnapshotsAsync(location, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException or
                                          ApplicationDataOwnershipException)
        {
            return ReadOutcome<T>.Corrupt();
        }

        var primary = parse(snapshots.Primary);
        if (primary.Status == ParsedDocumentStatus.Loaded)
        {
            return ReadOutcome<T>.Loaded(primary.State!);
        }

        if (primary.Status == ParsedDocumentStatus.UnknownSchema)
        {
            return ReadOutcome<T>.UnknownSchema(primary.FoundSchemaVersion);
        }

        var backup = parse(snapshots.Backup);
        if (backup.Status == ParsedDocumentStatus.UnknownSchema)
        {
            return ReadOutcome<T>.UnknownSchema(backup.FoundSchemaVersion);
        }

        if (!allowBackupRecovery)
        {
            return primary.Status == ParsedDocumentStatus.Missing &&
                   backup.Status == ParsedDocumentStatus.Missing
                ? ReadOutcome<T>.Missing()
                : ReadOutcome<T>.Corrupt();
        }

        if (backup.Status == ParsedDocumentStatus.Loaded &&
            snapshots.Backup is { } backupBytes)
        {
            try
            {
                await WriteDocumentAsync(
                    location,
                    backupBytes,
                    preserveExistingBackup: true,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or
                                              UnauthorizedAccessException or
                                              ApplicationDataOwnershipException)
            {
                return ReadOutcome<T>.Corrupt();
            }

            return ReadOutcome<T>.Recovered(backup.State!);
        }

        return primary.Status == ParsedDocumentStatus.Missing &&
               backup.Status == ParsedDocumentStatus.Missing
            ? ReadOutcome<T>.Missing()
            : ReadOutcome<T>.Corrupt();
    }

    private ValueTask<ApplicationStateSnapshots> ReadSnapshotsAsync(
        StateLocation location,
        CancellationToken cancellationToken) =>
        location.TargetId is { } targetId
            ? _store.ReadTargetStateSnapshotsAsync(
                targetId,
                location.PrimaryFileName,
                location.BackupFileName,
                location.MaximumBytes,
                cancellationToken)
            : _store.ReadApplicationStateSnapshotsAsync(
                location.PrimaryFileName,
                location.BackupFileName,
                location.MaximumBytes,
                cancellationToken);

    private ValueTask WriteDocumentAsync(
        StateLocation location,
        ReadOnlyMemory<byte> bytes,
        bool preserveExistingBackup,
        CancellationToken cancellationToken) =>
        location.TargetId is { } targetId
            ? _store.WriteTargetStateSnapshotAsync(
                targetId,
                location.PrimaryFileName,
                location.BackupFileName,
                bytes,
                location.MaximumBytes,
                preserveExistingBackup,
                cancellationToken)
            : _store.WriteApplicationStateSnapshotAsync(
                location.PrimaryFileName,
                location.BackupFileName,
                bytes,
                location.MaximumBytes,
                preserveExistingBackup,
                cancellationToken);

    private static ParsedDocument<RegistryWatermarkState> ParseRegistryWatermark(
        byte[]? bytes) =>
        ParseDocument<RegistryWatermarkDto, RegistryWatermarkState>(
            bytes,
            RegistryWatermarkSchemaVersion,
            dto => dto.RegistryVersion > 0
                ? new RegistryWatermarkState(dto.RegistryVersion)
                : null);

    private static ParsedDocument<ExternalCapabilityConfirmationState> ParseConfirmation(
        byte[]? bytes,
        Guid expectedTargetId) =>
        ParseDocument<ConfirmationDto, ExternalCapabilityConfirmationState>(
            bytes,
            ExternalCapabilityConfirmationSchemaVersion,
            dto =>
            {
                if (dto.TargetId != expectedTargetId ||
                    dto.NormalizedPurposes is null ||
                    dto.SnapshotDigest is null ||
                    !IsValidDecision(dto.Decision) ||
                    dto.DecidedAtUtc.Offset != TimeSpan.Zero ||
                    dto.DecidedAtUtc.Year < 2000)
                {
                    return null;
                }

                var purposes = ValidateCanonicalSequence(
                    dto.NormalizedPurposes,
                    MaximumPurposes,
                    MaximumPurposeCharacters,
                    IsPurposeCharacter,
                    allowEmpty: false);
                return purposes is not null && IsSha256Digest(dto.SnapshotDigest)
                    ? new ExternalCapabilityConfirmationState(
                        dto.TargetId,
                        dto.SnapshotDigest,
                        purposes,
                        dto.Decision,
                        dto.DecidedAtUtc)
                    : null;
            });

    private static ParsedDocument<CompatibilityDiagnosticState> ParseDiagnostic(
        byte[]? bytes) =>
        ParseDocument<DiagnosticDto, CompatibilityDiagnosticState>(
            bytes,
            CompatibilityDiagnosticSchemaVersion,
            dto =>
            {
                if (dto.ContractVersion is null ||
                    dto.RuleVersions is null ||
                    dto.SnapshotDigest is null ||
                    dto.RedactedOriginName is null ||
                    dto.ReasonCode is null ||
                    dto.DescriptorSchemaVersion <= 0 ||
                    dto.RegistrySchemaVersion <= 0 ||
                    dto.RegistryVersion <= 0 ||
                    !IsValidPhase(dto.Phase) ||
                    !IsVersionToken(dto.ContractVersion) ||
                    !IsSha256Digest(dto.SnapshotDigest) ||
                    !IsRedactedOriginName(dto.RedactedOriginName) ||
                    !IsReasonCode(dto.ReasonCode))
                {
                    return null;
                }

                var rules = ValidateCanonicalSequence(
                    dto.RuleVersions,
                    MaximumRuleVersions,
                    MaximumRuleVersionCharacters,
                    IsRuleVersionCharacter,
                    allowEmpty: true);
                return rules is null
                    ? null
                    : new CompatibilityDiagnosticState(
                        dto.ContractVersion,
                        dto.DescriptorSchemaVersion,
                        dto.RegistrySchemaVersion,
                        dto.RegistryVersion,
                        rules,
                        dto.SnapshotDigest,
                        dto.RedactedOriginName,
                        dto.ReasonCode,
                        dto.Phase);
            });

    private static ParsedDocument<TState> ParseDocument<TDto, TState>(
        byte[]? bytes,
        int currentSchemaVersion,
        Func<TDto, TState?> translate)
        where TDto : class
        where TState : class
    {
        if (bytes is null)
        {
            return ParsedDocument<TState>.Missing();
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                HasDuplicateObjectProperties(document.RootElement) ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return ParsedDocument<TState>.Corrupt();
            }

            if (schemaVersion > currentSchemaVersion)
            {
                return ParsedDocument<TState>.UnknownSchema(schemaVersion);
            }

            if (schemaVersion != currentSchemaVersion)
            {
                return ParsedDocument<TState>.Corrupt();
            }

            var dto = JsonSerializer.Deserialize<TDto>(bytes, JsonOptions);
            var state = dto is null ? null : translate(dto);
            return state is null
                ? ParsedDocument<TState>.Corrupt()
                : ParsedDocument<TState>.Loaded(state);
        }
        catch (Exception exception) when (exception is JsonException or
                                          InvalidOperationException or
                                          OverflowException or
                                          ArgumentException)
        {
            return ParsedDocument<TState>.Corrupt();
        }
    }

    private static ExternalCapabilityConfirmationState NormalizeConfirmation(
        ExternalCapabilityConfirmationState state)
    {
        if (!IsSha256Digest(state.SnapshotDigest))
        {
            throw new ArgumentException(
                "Snapshot digest must be canonical lowercase SHA-256.",
                nameof(state));
        }

        var decidedAtUtc = state.DecidedAtUtc.ToUniversalTime();
        if (!IsValidDecision(state.Decision) || decidedAtUtc.Year < 2000)
        {
            throw new ArgumentException("Confirmation decision or time is invalid.", nameof(state));
        }

        var purposes = NormalizeSequence(
            state.NormalizedPurposes,
            MaximumPurposes,
            MaximumPurposeCharacters,
            IsPurposeCharacter,
            allowEmpty: false,
            parameterName: nameof(state));
        return state with
        {
            NormalizedPurposes = purposes,
            DecidedAtUtc = decidedAtUtc,
        };
    }

    private static CompatibilityDiagnosticState NormalizeDiagnostic(
        CompatibilityDiagnosticState state)
    {
        if (state.DescriptorSchemaVersion <= 0 ||
            state.RegistrySchemaVersion <= 0 ||
            state.RegistryVersion <= 0 ||
            !IsValidPhase(state.Phase) ||
            !IsVersionToken(state.ContractVersion) ||
            !IsSha256Digest(state.SnapshotDigest) ||
            !IsRedactedOriginName(state.RedactedOriginName) ||
            !IsReasonCode(state.ReasonCode))
        {
            throw new ArgumentException(
                "Compatibility diagnostic contains a non-canonical field.",
                nameof(state));
        }

        var rules = NormalizeSequence(
            state.RuleVersions,
            MaximumRuleVersions,
            MaximumRuleVersionCharacters,
            IsRuleVersionCharacter,
            allowEmpty: true,
            parameterName: nameof(state));
        return state with { RuleVersions = rules };
    }

    private static string[] NormalizeSequence(
        IReadOnlyList<string> values,
        int maximumCount,
        int maximumCharacters,
        Func<char, bool> characterAllowed,
        bool allowEmpty,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if ((!allowEmpty && values.Count == 0) || values.Count > maximumCount)
        {
            throw new ArgumentException("Compatibility state list size is invalid.", parameterName);
        }

        var normalized = values
            .Select(value => NormalizeValue(value, maximumCharacters, characterAllowed, parameterName))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("Compatibility state list contains duplicates.", parameterName);
        }

        return normalized;
    }

    private static string[]? ValidateCanonicalSequence(
        string?[] values,
        int maximumCount,
        int maximumCharacters,
        Func<char, bool> characterAllowed,
        bool allowEmpty)
    {
        if ((!allowEmpty && values.Length == 0) || values.Length > maximumCount)
        {
            return null;
        }

        var prior = string.Empty;
        var result = new string[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value is null ||
                !IsCanonicalValue(value, maximumCharacters, characterAllowed) ||
                (index > 0 && string.CompareOrdinal(prior, value) >= 0))
            {
                return null;
            }

            result[index] = value;
            prior = value;
        }

        return result;
    }

    private static string NormalizeValue(
        string value,
        int maximumCharacters,
        Func<char, bool> characterAllowed,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (!IsCanonicalValue(normalized, maximumCharacters, characterAllowed))
        {
            throw new ArgumentException("Compatibility state value is invalid.", parameterName);
        }

        return normalized;
    }

    private static bool IsCanonicalValue(
        string value,
        int maximumCharacters,
        Func<char, bool> characterAllowed) =>
        value.Length is > 0 &&
        value.Length <= maximumCharacters &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.IsNormalized(NormalizationForm.FormC) &&
        value.All(characterAllowed);

    private static bool IsPurposeCharacter(char value) =>
        !char.IsControl(value) && value is not '?' and not '#' and not '\\';

    private static bool IsRuleVersionCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_' or '+' or '@';

    private static bool IsVersionToken(string value) =>
        IsCanonicalValue(
            value,
            MaximumVersionCharacters,
            character => char.IsAsciiLetterOrDigit(character) ||
                         character is '.' or '-' or '+');

    private static bool IsRedactedOriginName(string value) =>
        IsCanonicalValue(
            value,
            MaximumOriginNameCharacters,
            character => char.IsLetterOrDigit(character) ||
                         character is '.' or '-' or '_' or ' ');

    private static bool IsReasonCode(string value) =>
        value.Length is > 0 &&
        value.Length <= MaximumReasonCodeCharacters &&
        value[0] is >= 'A' and <= 'Z' &&
        value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static bool IsValidDecision(ExternalCapabilityDecision decision) =>
        decision is ExternalCapabilityDecision.Accepted or
            ExternalCapabilityDecision.Rejected;

    private static bool IsValidPhase(CompatibilityLifecyclePhase phase) =>
        phase is CompatibilityLifecyclePhase.Initializing or
            CompatibilityLifecyclePhase.Loading or
            CompatibilityLifecyclePhase.Ready or
            CompatibilityLifecyclePhase.Recovering or
            CompatibilityLifecyclePhase.Blocked or
            CompatibilityLifecyclePhase.Failed;

    private static bool IsSha256Digest(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasDuplicateObjectProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) ||
                    HasDuplicateObjectProperties(property.Value))
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

    private static void EnsureWritable(
        CompatibilityStateReadStatus status,
        string description)
    {
        if (status is CompatibilityStateReadStatus.Corrupt or
            CompatibilityStateReadStatus.UnknownSchema or
            CompatibilityStateReadStatus.RegistryRollbackDetected)
        {
            throw new InvalidDataException(
                $"The {description} cannot be replaced after a fail-closed read.");
        }
    }

    private static void ValidateTargetId(Guid targetId)
    {
        if (targetId == Guid.Empty)
        {
            throw new ArgumentException("Target identity must not be empty.", nameof(targetId));
        }
    }

    private static void ValidatePositiveVersion(int version, string parameterName)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private enum ParsedDocumentStatus
    {
        Missing,
        Loaded,
        UnknownSchema,
        Corrupt,
    }

    private readonly record struct ParsedDocument<T>(
        ParsedDocumentStatus Status,
        T? State,
        int? FoundSchemaVersion)
        where T : class
    {
        public static ParsedDocument<T> Missing() =>
            new(ParsedDocumentStatus.Missing, null, null);

        public static ParsedDocument<T> Loaded(T state) =>
            new(ParsedDocumentStatus.Loaded, state, null);

        public static ParsedDocument<T> UnknownSchema(int schemaVersion) =>
            new(ParsedDocumentStatus.UnknownSchema, null, schemaVersion);

        public static ParsedDocument<T> Corrupt() =>
            new(ParsedDocumentStatus.Corrupt, null, null);
    }

    private readonly record struct ReadOutcome<T>(
        CompatibilityStateReadStatus Status,
        T? State,
        int? FoundSchemaVersion,
        bool RecoveredFromBackup)
        where T : class
    {
        public static ReadOutcome<T> Missing() =>
            new(CompatibilityStateReadStatus.Missing, null, null, false);

        public static ReadOutcome<T> Loaded(T state) =>
            new(CompatibilityStateReadStatus.Loaded, state, null, false);

        public static ReadOutcome<T> Recovered(T state) =>
            new(CompatibilityStateReadStatus.RecoveredFromBackup, state, null, true);

        public static ReadOutcome<T> UnknownSchema(int? schemaVersion) =>
            new(CompatibilityStateReadStatus.UnknownSchema, null, schemaVersion, false);

        public static ReadOutcome<T> Corrupt() =>
            new(CompatibilityStateReadStatus.Corrupt, null, null, false);
    }

    private readonly record struct StateLocation(
        Guid? TargetId,
        string PrimaryFileName,
        string BackupFileName,
        int MaximumBytes)
    {
        public static StateLocation Application(
            string primaryFileName,
            string backupFileName,
            int maximumBytes) =>
            new(null, primaryFileName, backupFileName, maximumBytes);

        public static StateLocation Target(
            Guid targetId,
            string primaryFileName,
            string backupFileName,
            int maximumBytes) =>
            new(targetId, primaryFileName, backupFileName, maximumBytes);
    }

    private sealed class RegistryWatermarkDto
    {
        public required int SchemaVersion { get; init; }

        public required int RegistryVersion { get; init; }
    }

    private sealed class ConfirmationDto
    {
        public required int SchemaVersion { get; init; }

        public required Guid TargetId { get; init; }

        public required string? SnapshotDigest { get; init; }

        public required string?[]? NormalizedPurposes { get; init; }

        public required ExternalCapabilityDecision Decision { get; init; }

        public required DateTimeOffset DecidedAtUtc { get; init; }
    }

    private sealed class DiagnosticDto
    {
        public required int SchemaVersion { get; init; }

        public required string? ContractVersion { get; init; }

        public required int DescriptorSchemaVersion { get; init; }

        public required int RegistrySchemaVersion { get; init; }

        public required int RegistryVersion { get; init; }

        public required string?[]? RuleVersions { get; init; }

        public required string? SnapshotDigest { get; init; }

        public required string? RedactedOriginName { get; init; }

        public required string? ReasonCode { get; init; }

        public required CompatibilityLifecyclePhase Phase { get; init; }
    }
}
