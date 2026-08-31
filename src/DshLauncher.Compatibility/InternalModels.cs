namespace DshLauncher.Compatibility;

internal sealed record ContractDefinition(
    string ContractVersion,
    int MaximumDescriptorBytes,
    int MaximumDescriptorComponents,
    int MaximumJsonDepth,
    IReadOnlyList<CapabilityKind> BaseCapabilities,
    IReadOnlyDictionary<string, CapabilityCeilingDefinition> CapabilityCeilings,
    byte[] CanonicalJson,
    string Sha256);

internal sealed record RegistryDefinition(
    int RegistryVersion,
    string ContractVersion,
    IReadOnlyList<RuleDefinition> Rules,
    IReadOnlyList<TombstoneDefinition> Tombstones,
    byte[] CanonicalJson,
    string Sha256);

internal sealed record RuleDefinition(
    string RuleId,
    string RuleVersion,
    string ContractVersion,
    string UiId,
    SemanticVersion MinimumUiVersion,
    SemanticVersion MaximumUiVersion,
    string? SourceRev,
    string AdapterKey,
    string Purpose,
    string DiagnosticName,
    IReadOnlyList<GrantDefinition> Capabilities);

internal sealed record TombstoneDefinition(
    string RuleId,
    int RevokedFromRegistryVersion,
    string Severity,
    string ReasonCode);

internal sealed record GrantDefinition(
    string CapabilityId,
    CapabilityKind Kind,
    string? Origin,
    string? ParentOrigin,
    string? Path,
    CapabilityPathMatch PathMatch,
    IReadOnlyList<HttpMethodKind> Methods,
    IReadOnlyList<WebResourceKind> ResourceKinds,
    IReadOnlyList<string> QueryKeys,
    CapabilityDocumentScope DocumentScope,
    string? ScriptSha256,
    string Purpose)
{
    public string ScopeKey => string.Join(
        '|',
        Kind,
        Origin ?? string.Empty,
        ParentOrigin ?? string.Empty,
        Path ?? string.Empty,
        PathMatch,
        DocumentScope,
        ScriptSha256 ?? string.Empty);

    public string FullKey => string.Join(
        '|',
        ScopeKey,
        string.Join(',', Methods.Order()),
        string.Join(',', ResourceKinds.Order()),
        string.Join(',', QueryKeys.Order(StringComparer.Ordinal)),
        Purpose);

    public CapabilityGrant ToPublic() => new(
        Kind,
        Origin,
        ParentOrigin,
        Path,
        PathMatch,
        Methods,
        ResourceKinds,
        QueryKeys,
        DocumentScope,
        ScriptSha256,
        Purpose);
}

internal sealed record CapabilityCeilingDefinition(
    string CapabilityId,
    CapabilityKind Kind,
    IReadOnlySet<HttpMethodKind> Methods,
    IReadOnlySet<WebResourceKind> ResourceKinds,
    CapabilityDocumentScope DocumentScope,
    bool IsConditional);

internal sealed record DescriptorDefinition(
    int SchemaVersion,
    string ContractVersion,
    IReadOnlyList<DescriptorComponent> Components,
    byte[] CanonicalJson,
    string Sha256)
{
    public DescriptorIdentity ToPublic() => new(
        SchemaVersion,
        ContractVersion,
        Components.Select(component => WebUiComponentIdentity.Create(
            component.UiId,
            component.UiVersion.Text,
            component.SourceRev,
            component.AdapterKeys)),
        Sha256);
}

internal sealed record DescriptorComponent(
    string UiId,
    SemanticVersion UiVersion,
    string? SourceRev,
    IReadOnlyList<string> AdapterKeys);

internal enum StrictJsonError
{
    None,
    Malformed,
    TooDeep,
    DuplicateProperty,
}
