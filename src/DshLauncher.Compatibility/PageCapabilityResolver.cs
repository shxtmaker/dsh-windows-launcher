namespace DshLauncher.Compatibility;

public sealed class PageCapabilityResolver : IPageCapabilityResolver
{
    private readonly ContractDefinition _contract;
    private readonly RegistryDefinition _registry;

    private PageCapabilityResolver(
        ContractDefinition contract,
        RegistryDefinition registry)
    {
        _contract = contract;
        _registry = registry;
    }

    public string ContractVersion => _contract.ContractVersion;

    public string ContractSha256 => _contract.Sha256;

    public int RegistryVersion => _registry.RegistryVersion;

    public string RegistrySha256 => _registry.Sha256;

    public static PageCapabilityResolver Create(
        ReadOnlyMemory<byte> trustedContractJson,
        ReadOnlyMemory<byte> trustedRegistryJson)
    {
        ContractDefinition contract;
        try
        {
            contract = ConfigurationParser.ParseContract(trustedContractJson);
        }
        catch (CompatibilityConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (IsConfigurationFailure(exception))
        {
            throw new CompatibilityConfigurationException(
                CompatibilityReasonCode.ContractMalformed,
                "The trusted compatibility contract could not be validated.");
        }

        try
        {
            var registry = ConfigurationParser.ParseRegistry(
                trustedRegistryJson,
                contract);
            return new PageCapabilityResolver(contract, registry);
        }
        catch (CompatibilityConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (IsConfigurationFailure(exception))
        {
            throw new CompatibilityConfigurationException(
                CompatibilityReasonCode.RegistryIntegrityFailed,
                "The trusted adapter registry could not be validated.");
        }
    }

    public PageCapabilityResolution Resolve(
        Guid targetId,
        BoundedDescriptorResponse descriptorResponse)
    {
        if (targetId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target identity must not be empty.",
                nameof(targetId));
        }

        ArgumentNullException.ThrowIfNull(descriptorResponse);
        var baseSnapshot = CreateSnapshot(
            targetId,
            descriptorSha256: null,
            extensionCapabilities: [],
            matchedRules: []);
        try
        {
            if (!DescriptorParser.TryParse(
                    descriptorResponse,
                    _contract,
                    out var descriptor,
                    out var descriptorReason))
            {
                return BaseResolution(baseSnapshot, descriptorReason, descriptorIdentity: null);
            }

            var matchedRules = new List<RuleDefinition>();
            var unmatched = false;
            foreach (var component in descriptor.Components)
            {
                var componentMatched = false;
                foreach (var adapterKey in component.AdapterKeys)
                {
                    var rule = FindMatchingRule(component, adapterKey);
                    if (rule is null)
                    {
                        unmatched = true;
                        continue;
                    }

                    componentMatched = true;
                    matchedRules.Add(rule);
                }

                if (!componentMatched)
                {
                    unmatched = true;
                }
            }

            if (matchedRules.Count == 0)
            {
                return BaseResolution(
                    baseSnapshot,
                    CompatibilityReasonCode.NoMatchingRule,
                    descriptor.ToPublic());
            }

            var uniqueRules = matchedRules
                .GroupBy(
                    rule => $"{rule.RuleId}\0{rule.RuleVersion}",
                    StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
                .ThenBy(rule => rule.RuleVersion, StringComparer.Ordinal)
                .ToArray();
            if (!TryMergeCapabilities(
                    uniqueRules,
                    out var capabilities,
                    out var mergeReason))
            {
                return BaseResolution(
                    baseSnapshot,
                    mergeReason,
                    descriptor.ToPublic());
            }

            var ruleIdentities = uniqueRules.Select(rule => new RuleIdentity(
                rule.RuleId,
                rule.RuleVersion,
                rule.AdapterKey,
                rule.Purpose)).ToArray();
            var candidate = CreateSnapshot(
                targetId,
                descriptor.Sha256,
                capabilities.Select(capability => capability.ToPublic()),
                ruleIdentities);
            var reasons = unmatched
                ? new[] { CompatibilityReasonCode.PartialMatch }
                : new[] { CompatibilityReasonCode.None };
            return new PageCapabilityResolution(
                CompatibilityLevel.Extended,
                reasons[0],
                reasons,
                baseSnapshot,
                candidate,
                descriptor.ToPublic());
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            ArgumentException or
            FormatException or
            OverflowException)
        {
            return BaseResolution(
                baseSnapshot,
                CompatibilityReasonCode.DescriptorMalformed,
                descriptorIdentity: null);
        }
    }

    private RuleDefinition? FindMatchingRule(
        DescriptorComponent component,
        string adapterKey)
    {
        RuleDefinition? match = null;
        foreach (var rule in _registry.Rules)
        {
            var exactVersionRule = string.Equals(
                rule.MinimumUiVersion.Text,
                rule.MaximumUiVersion.Text,
                StringComparison.Ordinal);
            if (!string.Equals(
                    rule.ContractVersion,
                    _contract.ContractVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(rule.UiId, component.UiId, StringComparison.Ordinal) ||
                !string.Equals(rule.AdapterKey, adapterKey, StringComparison.Ordinal) ||
                exactVersionRule && !string.Equals(
                    rule.MinimumUiVersion.Text,
                    component.UiVersion.Text,
                    StringComparison.Ordinal) ||
                !exactVersionRule &&
                (component.UiVersion.Prerelease is not null ||
                 component.UiVersion.Build is not null ||
                 rule.MinimumUiVersion.CompareTo(component.UiVersion) > 0 ||
                 rule.MaximumUiVersion.CompareTo(component.UiVersion) < 0) ||
                rule.SourceRev is not null &&
                !string.Equals(
                    rule.SourceRev,
                    component.SourceRev,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = rule;
        }

        return match;
    }

    private static bool TryMergeCapabilities(
        IEnumerable<RuleDefinition> rules,
        out IReadOnlyList<GrantDefinition> capabilities,
        out CompatibilityReasonCode reason)
    {
        var merged = new List<GrantDefinition>();
        foreach (var scopeGroup in rules.SelectMany(rule => rule.Capabilities)
                     .GroupBy(capability => capability.ScopeKey, StringComparer.Ordinal))
        {
            var purposes = scopeGroup.Select(capability => capability.Purpose)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (purposes.Length != 1)
            {
                capabilities = [];
                reason = CompatibilityReasonCode.RuleConflict;
                return false;
            }

            var first = scopeGroup.First();
            merged.Add(new GrantDefinition(
                first.CapabilityId,
                first.Kind,
                first.Origin,
                first.ParentOrigin,
                first.Path,
                first.PathMatch,
                Array.AsReadOnly(scopeGroup.SelectMany(capability => capability.Methods)
                    .Distinct().Order().ToArray()),
                Array.AsReadOnly(scopeGroup.SelectMany(capability => capability.ResourceKinds)
                    .Distinct().Order().ToArray()),
                Array.AsReadOnly(scopeGroup.SelectMany(capability => capability.QueryKeys)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()),
                first.DocumentScope,
                first.Purpose));
        }

        capabilities = Array.AsReadOnly(
            merged.OrderBy(capability => capability.FullKey, StringComparer.Ordinal)
                .ToArray());
        reason = CompatibilityReasonCode.None;
        return true;
    }

    private PageCapabilitySnapshot CreateSnapshot(
        Guid targetId,
        string? descriptorSha256,
        IEnumerable<CapabilityGrant> extensionCapabilities,
        IEnumerable<RuleIdentity> matchedRules)
    {
        var baseCapabilities = _contract.BaseCapabilities
            .Select(CreateBaseGrant)
            .ToArray();
        var extensions = extensionCapabilities.ToArray();
        var rules = matchedRules.ToArray();
        var canonical = CanonicalJson.WriteSnapshot(
            targetId,
            _contract.ContractVersion,
            _registry.RegistryVersion,
            descriptorSha256,
            baseCapabilities,
            extensions,
            rules);
        return new PageCapabilitySnapshot(
            targetId,
            _contract.ContractVersion,
            _registry.RegistryVersion,
            baseCapabilities,
            extensions,
            rules,
            canonical,
            CanonicalJson.Sha256(canonical));
    }

    private static CapabilityGrant CreateBaseGrant(CapabilityKind kind)
    {
        var (methods, resources) = kind switch
        {
            CapabilityKind.SameOrigin =>
                (AllHttpMethods(), Enum.GetValues<WebResourceKind>()
                    .Where(resource => resource is not (
                        WebResourceKind.WebSocket or
                        WebResourceKind.Frame or
                        WebResourceKind.Worker))),
            CapabilityKind.SameOriginFrame or CapabilityKind.AboutBlankFrame =>
                (ReadMethods(), [WebResourceKind.Document]),
            CapabilityKind.DataImage or CapabilityKind.BlobImage =>
                (ReadMethods(), [WebResourceKind.Image]),
            CapabilityKind.BlobMedia =>
                (ReadMethods(), [WebResourceKind.Media]),
            CapabilityKind.BlobData =>
                (ReadMethods(), [WebResourceKind.Fetch, WebResourceKind.XmlHttpRequest]),
            _ => (Enumerable.Empty<HttpMethodKind>(), Enumerable.Empty<WebResourceKind>()),
        };
        return new CapabilityGrant(
            kind,
            origin: null,
            parentOrigin: null,
            path: null,
            CapabilityPathMatch.None,
            methods,
            resources,
            queryKeys: [],
            CapabilityDocumentScope.TargetDocument,
            $"base.{kind.ToString().ToLowerInvariant()}");
    }

    private static HttpMethodKind[] AllHttpMethods() =>
        Enum.GetValues<HttpMethodKind>();

    private static IEnumerable<HttpMethodKind> ReadMethods() =>
        [HttpMethodKind.Get, HttpMethodKind.Head];

    private static PageCapabilityResolution BaseResolution(
        PageCapabilitySnapshot baseSnapshot,
        CompatibilityReasonCode reason,
        DescriptorIdentity? descriptorIdentity) =>
        new(
            CompatibilityLevel.Base,
            reason,
            [reason],
            baseSnapshot,
            baseSnapshot,
            descriptorIdentity);

    private static bool IsConfigurationFailure(Exception exception) => exception is
        InvalidOperationException or
        ArgumentException or
        FormatException or
        OverflowException;
}
