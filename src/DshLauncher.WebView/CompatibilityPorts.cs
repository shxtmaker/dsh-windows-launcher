using DshLauncher.Compatibility;

namespace DshLauncher.WebView;

public interface IExternalCapabilityConfirmationPort
{
    ValueTask<ExternalCapabilityDecision> ConfirmAsync(
        ExternalCapabilityConfirmationRequest request,
        CancellationToken cancellationToken);

    ValueTask RevokeAsync(
        Guid targetId,
        CancellationToken cancellationToken);
}

public enum ExternalCapabilityDecision
{
    Rejected,
    RegistryRollbackBlocked,
    Accepted,
}

public sealed class ExternalCapabilityConfirmationRequest
{
    public ExternalCapabilityConfirmationRequest(
        Guid targetId,
        string snapshotSha256,
        IEnumerable<ExternalCapabilityUse> uses)
    {
        if (targetId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target identity must not be empty.",
                nameof(targetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotSha256);
        ArgumentNullException.ThrowIfNull(uses);
        var frozenUses = uses.Distinct().ToArray();
        if (frozenUses.Length == 0)
        {
            throw new ArgumentException(
                "At least one external capability use is required.",
                nameof(uses));
        }

        TargetId = targetId;
        SnapshotSha256 = snapshotSha256;
        Uses = Array.AsReadOnly(frozenUses);
    }

    public Guid TargetId { get; }

    public string SnapshotSha256 { get; }

    public IReadOnlyList<ExternalCapabilityUse> Uses { get; }
}

public sealed record ExternalCapabilityUse(
    string DisplayOrigin,
    string Purpose,
    CapabilityKind Kind);

public sealed class CompatibilityStatusDto
{
    public CompatibilityStatusDto(
        CompatibilityLevel level,
        CompatibilityReasonCode primaryReason,
        IEnumerable<CompatibilityReasonCode> reasons,
        string contractVersion,
        int descriptorSchemaVersion,
        int registryVersion,
        string snapshotSha256,
        IEnumerable<string> purposes,
        IEnumerable<string> ruleVersions)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotSha256);
        ArgumentNullException.ThrowIfNull(purposes);
        ArgumentNullException.ThrowIfNull(ruleVersions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            descriptorSchemaVersion);

        Level = level;
        PrimaryReason = primaryReason;
        Reasons = Array.AsReadOnly(reasons.Distinct().ToArray());
        ContractVersion = contractVersion;
        DescriptorSchemaVersion = descriptorSchemaVersion;
        RegistryVersion = registryVersion;
        SnapshotSha256 = snapshotSha256;
        Purposes = Array.AsReadOnly(
            purposes
                .Where(static purpose => !string.IsNullOrWhiteSpace(purpose))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
        RuleVersions = Array.AsReadOnly(
            ruleVersions
                .Where(static version => !string.IsNullOrWhiteSpace(version))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public CompatibilityLevel Level { get; }

    public CompatibilityReasonCode PrimaryReason { get; }

    public IReadOnlyList<CompatibilityReasonCode> Reasons { get; }

    public string ContractVersion { get; }

    public int DescriptorSchemaVersion { get; }

    public int RegistryVersion { get; }

    public string SnapshotSha256 { get; }

    public IReadOnlyList<string> Purposes { get; }

    public IReadOnlyList<string> RuleVersions { get; }
}
