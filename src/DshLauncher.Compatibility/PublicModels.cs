using System.Collections.ObjectModel;

namespace DshLauncher.Compatibility;

public interface IPageCapabilityResolver
{
    PageCapabilityResolution Resolve(
        Guid targetId,
        BoundedDescriptorResponse descriptorResponse);
}

public enum CompatibilityLevel
{
    Base = 0,
    Extended = 1,
}

public enum CompatibilityReasonCode
{
    None = 0,
    DescriptorMissing = 1,
    DescriptorRedirected = 2,
    DescriptorStatusRejected = 3,
    DescriptorContentTypeRejected = 4,
    DescriptorTooLarge = 5,
    DescriptorMalformed = 6,
    DescriptorTooDeep = 7,
    DescriptorDuplicateProperty = 8,
    DescriptorTooManyComponents = 9,
    DescriptorUnknownField = 10,
    DescriptorUnsupportedSchema = 11,
    DescriptorUnsupportedContract = 12,
    DescriptorInvalidIdentity = 13,
    DescriptorIdentityConflict = 14,
    NoMatchingRule = 15,
    PartialMatch = 16,
    RuleTombstoned = 17,
    RuleConflict = 18,
    RuleOutsideGlobalCeiling = 19,
    ExternalCapabilityRejected = 20,
    ExternalCapabilityConfirmationFailed = 21,
    RegistryRollbackBlocked = 22,
    ContractMalformed = 23,
    ContractUnsupportedSchema = 24,
    ContractInvalidCapability = 25,
    RegistryMalformed = 26,
    RegistryUnsupportedSchema = 27,
    RegistryVersionInvalid = 28,
    RegistryIntegrityFailed = 29,
    RegistryRuleOverlap = 30,
    RegistryTombstoneConflict = 31,
    RegistryInvalidCapability = 32,
}

public enum CapabilityKind
{
    SameOrigin = 0,
    SameOriginFrame = 1,
    AboutBlankFrame = 2,
    DataImage = 3,
    BlobImage = 4,
    BlobMedia = 5,
    WebGl = 6,
    PageEditing = 7,
    FindInPage = 8,
    Zoom = 9,
    UserImageInput = 10,
    ExternalPassive = 11,
    ReviewedApi = 12,
    Frame = 13,
    WebSocket = 14,
    DedicatedWorker = 15,
    SharedWorker = 16,
    AudioWorklet = 17,
    Oopif = 18,
    Redirect = 19,
    BlobData = 20,
    CdpFetch = 21,
    ReviewedInlineScriptSha256 = 22,
    TargetWebSocket = 23,
}

public enum HttpMethodKind
{
    Get = 0,
    Head = 1,
    Post = 2,
    Options = 3,
}

public enum WebResourceKind
{
    Document = 0,
    Stylesheet = 1,
    Image = 2,
    Media = 3,
    Font = 4,
    Script = 5,
    XmlHttpRequest = 6,
    Fetch = 7,
    TextTrack = 8,
    EventSource = 9,
    WebSocket = 10,
    Manifest = 11,
    Other = 12,
    Frame = 13,
    Worker = 14,
}

public enum CapabilityPathMatch
{
    None = 0,
    Exact = 1,
    SegmentPrefix = 2,
}

public enum CapabilityDocumentScope
{
    TargetDocument = 0,
    CrossOriginFrameOnly = 1,
    AnyApprovedDocument = 2,
}

public sealed class BoundedDescriptorResponse
{
    private const int MaximumRetainedBodyBytes = (64 * 1024) + 1;
    private readonly byte[] _body;

    private BoundedDescriptorResponse(
        bool isMissing,
        int statusCode,
        string? contentType,
        bool wasRedirected,
        ReadOnlySpan<byte> body)
    {
        IsMissing = isMissing;
        StatusCode = statusCode;
        ContentType = contentType;
        WasRedirected = wasRedirected;
        _body = body[..Math.Min(body.Length, MaximumRetainedBodyBytes)].ToArray();
    }

    public bool IsMissing { get; }

    public int StatusCode { get; }

    public string? ContentType { get; }

    public bool WasRedirected { get; }

    public ReadOnlyMemory<byte> Body => _body.ToArray();

    internal ReadOnlyMemory<byte> BodyBuffer => _body;

    public static BoundedDescriptorResponse Missing() =>
        new(true, 0, null, false, ReadOnlySpan<byte>.Empty);

    public static BoundedDescriptorResponse Received(
        int statusCode,
        string? contentType,
        bool wasRedirected,
        ReadOnlyMemory<byte> body) =>
        new(false, statusCode, contentType, wasRedirected, body.Span);
}

public sealed class DescriptorIdentity
{
    internal DescriptorIdentity(
        int schemaVersion,
        string contractVersion,
        IEnumerable<WebUiComponentIdentity> components,
        string sha256)
    {
        SchemaVersion = schemaVersion;
        ContractVersion = contractVersion;
        Components = Freeze(components);
        Sha256 = sha256;
    }

    public int SchemaVersion { get; }

    public string ContractVersion { get; }

    public IReadOnlyList<WebUiComponentIdentity> Components { get; }

    public string Sha256 { get; }

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());
}

public sealed class WebUiComponentIdentity
{
    private WebUiComponentIdentity(
        string uiId,
        string uiVersion,
        string? sourceRev,
        IReadOnlyList<string> adapterKeys)
    {
        UiId = uiId;
        UiVersion = uiVersion;
        SourceRev = sourceRev;
        AdapterKeys = adapterKeys;
    }

    public string UiId { get; }

    public string UiVersion { get; }

    public string? SourceRev { get; }

    public IReadOnlyList<string> AdapterKeys { get; }

    internal static WebUiComponentIdentity Create(
        string uiId,
        string uiVersion,
        string? sourceRev,
        IEnumerable<string> adapterKeys) =>
        new(
            uiId,
            uiVersion,
            sourceRev,
            Array.AsReadOnly(adapterKeys.ToArray()));
}

public sealed record RuleIdentity(
    string RuleId,
    string RuleVersion,
    string AdapterKey,
    string Purpose);

public sealed class CapabilityGrant
{
    internal CapabilityGrant(
        CapabilityKind kind,
        string? origin,
        string? parentOrigin,
        string? path,
        CapabilityPathMatch pathMatch,
        IEnumerable<HttpMethodKind> methods,
        IEnumerable<WebResourceKind> resourceKinds,
        IEnumerable<string> queryKeys,
        CapabilityDocumentScope documentScope,
        string? scriptSha256,
        string purpose)
    {
        Kind = kind;
        Origin = origin;
        ParentOrigin = parentOrigin;
        Path = path;
        PathMatch = pathMatch;
        Methods = Array.AsReadOnly(methods.Distinct().Order().ToArray());
        ResourceKinds = Array.AsReadOnly(
            resourceKinds.Distinct().Order().ToArray());
        QueryKeys = Array.AsReadOnly(
            queryKeys.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
        DocumentScope = documentScope;
        ScriptSha256 = scriptSha256;
        Purpose = purpose;
    }

    public CapabilityKind Kind { get; }

    public string? Origin { get; }

    /// <summary>
    /// Gets the exact parent origin required for a frame-scoped grant.
    /// A null value means the target document is the parent.
    /// </summary>
    public string? ParentOrigin { get; }

    public string? Path { get; }

    public CapabilityPathMatch PathMatch { get; }

    public IReadOnlyList<HttpMethodKind> Methods { get; }

    public IReadOnlyList<WebResourceKind> ResourceKinds { get; }

    /// <summary>
    /// Gets the only query-key names the grant may carry. An empty list forbids
    /// a query. Enforcement must also reject duplicate keys and ambiguous values.
    /// </summary>
    public IReadOnlyList<string> QueryKeys { get; }

    public CapabilityDocumentScope DocumentScope { get; }

    /// <summary>
    /// Gets the canonical Base64 SHA-256 digest for a reviewed inline script.
    /// Null for every non-script-hash grant.
    /// </summary>
    public string? ScriptSha256 { get; }

    public string Purpose { get; }
}

public sealed class PageCapabilitySnapshot
{
    private readonly byte[] _canonicalJson;

    internal PageCapabilitySnapshot(
        Guid targetId,
        string contractVersion,
        int registryVersion,
        IEnumerable<CapabilityGrant> baseCapabilities,
        IEnumerable<CapabilityGrant> extensionCapabilities,
        IEnumerable<RuleIdentity> matchedRules,
        ReadOnlySpan<byte> canonicalJson,
        string sha256)
    {
        TargetId = targetId;
        ContractVersion = contractVersion;
        RegistryVersion = registryVersion;
        BaseCapabilities = Array.AsReadOnly(baseCapabilities.ToArray());
        ExtensionCapabilities = Array.AsReadOnly(
            extensionCapabilities.ToArray());
        MatchedRules = Array.AsReadOnly(matchedRules.ToArray());
        _canonicalJson = canonicalJson.ToArray();
        Sha256 = sha256;
    }

    public Guid TargetId { get; }

    public string ContractVersion { get; }

    public int RegistryVersion { get; }

    public IReadOnlyList<CapabilityGrant> BaseCapabilities { get; }

    public IReadOnlyList<CapabilityGrant> ExtensionCapabilities { get; }

    public IReadOnlyList<RuleIdentity> MatchedRules { get; }

    public ReadOnlyMemory<byte> CanonicalJson => _canonicalJson.ToArray();

    public string Sha256 { get; }
}

public sealed class PageCapabilityResolution
{
    internal PageCapabilityResolution(
        CompatibilityLevel level,
        CompatibilityReasonCode primaryReason,
        IEnumerable<CompatibilityReasonCode> reasons,
        PageCapabilitySnapshot baseSnapshot,
        PageCapabilitySnapshot snapshot,
        DescriptorIdentity? descriptorIdentity)
    {
        Level = level;
        PrimaryReason = primaryReason;
        Reasons = Array.AsReadOnly(reasons.Distinct().ToArray());
        BaseSnapshot = baseSnapshot;
        Snapshot = snapshot;
        DescriptorIdentity = descriptorIdentity;
    }

    public CompatibilityLevel Level { get; }

    public CompatibilityReasonCode PrimaryReason { get; }

    public IReadOnlyList<CompatibilityReasonCode> Reasons { get; }

    public PageCapabilitySnapshot BaseSnapshot { get; }

    public PageCapabilitySnapshot Snapshot { get; }

    public DescriptorIdentity? DescriptorIdentity { get; }
}

public sealed class CompatibilityConfigurationException : Exception
{
    public CompatibilityConfigurationException(
        CompatibilityReasonCode reasonCode,
        string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public CompatibilityReasonCode ReasonCode { get; }
}
