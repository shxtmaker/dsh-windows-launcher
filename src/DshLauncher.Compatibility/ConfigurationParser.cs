using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshLauncher.Compatibility;

internal static partial class ConfigurationParser
{
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private const int MaximumConfigurationDepth = 32;
    private const int MaximumRules = 4096;
    private const int MaximumCapabilitiesPerRule = 256;
    private const string DescriptorSchemaId =
        "https://schemas.dshwindowslauncher.invalid/webui-compatibility-descriptor.schema.json";

    private static readonly Dictionary<string, CapabilityKind> BaseCapabilityMap =
        new Dictionary<string, CapabilityKind>(StringComparer.Ordinal)
        {
            ["about-blank-frame"] = CapabilityKind.AboutBlankFrame,
            ["blob-data-read"] = CapabilityKind.BlobData,
            ["blob-image"] = CapabilityKind.BlobImage,
            ["blob-media"] = CapabilityKind.BlobMedia,
            ["data-image"] = CapabilityKind.DataImage,
            ["find-in-page"] = CapabilityKind.FindInPage,
            ["page-editing"] = CapabilityKind.PageEditing,
            ["page-zoom"] = CapabilityKind.Zoom,
            ["same-origin-frame"] = CapabilityKind.SameOriginFrame,
            ["target-api-request"] = CapabilityKind.SameOrigin,
            ["target-http-document"] = CapabilityKind.SameOrigin,
            ["target-http-resource"] = CapabilityKind.SameOrigin,
            ["target-sse"] = CapabilityKind.SameOrigin,
            ["user-image-drop"] = CapabilityKind.UserImageInput,
            ["user-image-paste"] = CapabilityKind.UserImageInput,
            ["webgl-rendering"] = CapabilityKind.WebGl,
        };

    private static readonly Dictionary<string, ExtensionCapabilityShape>
        ExtensionCapabilityMap =
            new Dictionary<string, ExtensionCapabilityShape>(StringComparer.Ordinal)
            {
                ["external-reviewed-api"] = new(
                    CapabilityKind.ReviewedApi,
                    "reviewedApi",
                    CapabilityDocumentScope.TargetDocument,
                    new HashSet<WebResourceKind>(
                        [WebResourceKind.Fetch, WebResourceKind.XmlHttpRequest])),
                ["external-reviewed-frame"] = new(
                    CapabilityKind.Frame,
                    "passiveResource",
                    CapabilityDocumentScope.TargetDocument,
                    new HashSet<WebResourceKind>(
                        [WebResourceKind.Document, WebResourceKind.Frame])),
                ["external-reviewed-frame-script"] = new(
                    CapabilityKind.ExternalPassive,
                    "passiveResource",
                    CapabilityDocumentScope.CrossOriginFrameOnly,
                    new HashSet<WebResourceKind>([WebResourceKind.Script])),
                ["external-reviewed-passive-resource"] = new(
                    CapabilityKind.ExternalPassive,
                    "passiveResource",
                    CapabilityDocumentScope.TargetDocument,
                    new HashSet<WebResourceKind>([
                        WebResourceKind.Font,
                        WebResourceKind.Image,
                        WebResourceKind.Media,
                        WebResourceKind.Stylesheet,
                    ])),
                ["reviewed-inline-script-sha256"] = new(
                    CapabilityKind.ReviewedInlineScriptSha256,
                    "passiveResource",
                    CapabilityDocumentScope.TargetDocument,
                    new HashSet<WebResourceKind>([WebResourceKind.Script]),
                    MainDocumentScriptAllowed: true),
                ["target-websocket"] = new(
                    CapabilityKind.TargetWebSocket,
                    "passiveResource",
                    CapabilityDocumentScope.TargetDocument,
                    new HashSet<WebResourceKind>([WebResourceKind.WebSocket])),
            };

    private static readonly Dictionary<string, CapabilityKind> ConditionalCapabilityMap =
        new Dictionary<string, CapabilityKind>(StringComparer.Ordinal)
        {
            ["audio-worklet"] = CapabilityKind.AudioWorklet,
            ["cdp-fetch"] = CapabilityKind.CdpFetch,
            ["dedicated-worker"] = CapabilityKind.DedicatedWorker,
            ["external-websocket"] = CapabilityKind.WebSocket,
            ["external-reviewed-frame-script"] = CapabilityKind.ExternalPassive,
            ["oopif-frame"] = CapabilityKind.Oopif,
            ["redirect-hop"] = CapabilityKind.Redirect,
            ["shared-worker"] = CapabilityKind.SharedWorker,
        };

    private static readonly HashSet<string> TombstoneReasonCodes =
    [
        "INCOMPLETE_IDENTITY",
        "UNREPRODUCIBLE",
        "ROUTE_SECURITY_FAILED",
        "OUTSIDE_GLOBAL_CEILING",
        "RULE_TOO_BROAD",
        "GATE_FAILED",
        "IDENTITY_MISMATCH",
        "MAINTENANCE_ENDED",
        "PROVIDER_WITHDRAWN",
        "DEPENDENCY_UNAVAILABLE",
    ];

    public static ContractDefinition ParseContract(ReadOnlyMemory<byte> bytes)
    {
        using var document = ParseTrustedJson(
            bytes,
            CompatibilityReasonCode.ContractMalformed,
            "contract");
        var root = document.RootElement;
        if (!StrictJson.HasOnlyProperties(
                root,
                "schemaVersion",
                "contractVersion",
                "descriptorSchemaId",
                "limits",
                "methodCeilings",
                "baseCapabilities",
                "extensionCapabilities",
                "conditionalCapabilities",
                "permanentlyForbiddenCapabilities",
                "globalCspDebt") ||
            !StrictJson.TryGetSafeInteger(root, "schemaVersion", out var schemaVersion))
        {
            throw ContractError(
                CompatibilityReasonCode.ContractMalformed,
                "The trusted compatibility contract has an invalid shape.");
        }

        if (schemaVersion != 1)
        {
            throw ContractError(
                CompatibilityReasonCode.ContractUnsupportedSchema,
                "The trusted compatibility contract schema is not supported.");
        }

        if (!StrictJson.TryGetRequiredString(root, "contractVersion", out var versionText) ||
            !SemanticVersion.TryParseExact(versionText, out _) ||
            !StrictJson.TryGetRequiredString(root, "descriptorSchemaId", out var schemaId) ||
            !string.Equals(schemaId, DescriptorSchemaId, StringComparison.Ordinal))
        {
            throw ContractError(
                CompatibilityReasonCode.ContractMalformed,
                "The trusted compatibility contract identity is invalid.");
        }

        ParseContractLimits(
            root,
            out var maximumDescriptorBytes,
            out var maximumDescriptorComponents,
            out var maximumJsonDepth);
        var methodCeilings = ParseMethodCeilings(root);
        var baseCapabilities = ParseBaseCapabilities(root);
        var ceilings = ParseExtensionCapabilities(root, methodCeilings);
        ParseConditionalCapabilities(root, ceilings);
        ParseForbiddenCapabilities(root, ceilings.Keys);
        ParseGlobalCspDebt(root);

        var canonical = CanonicalJson.WriteCanonicalDocument(
            root,
            CanonicalDocumentKind.Contract);
        return new ContractDefinition(
            versionText,
            maximumDescriptorBytes,
            maximumDescriptorComponents,
            maximumJsonDepth,
            Array.AsReadOnly(baseCapabilities.Distinct().Order().ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                string,
                CapabilityCeilingDefinition>(ceilings),
            canonical,
            CanonicalJson.Sha256(canonical));
    }

    public static RegistryDefinition ParseRegistry(
        ReadOnlyMemory<byte> bytes,
        ContractDefinition contract)
    {
        using var document = ParseTrustedJson(
            bytes,
            CompatibilityReasonCode.RegistryMalformed,
            "registry");
        var root = document.RootElement;
        if (!StrictJson.HasOnlyProperties(
                root,
                "schemaVersion",
                "registryVersion",
                "contractVersion",
                "activeRules",
                "tombstones") ||
            !StrictJson.TryGetSafeInteger(root, "schemaVersion", out var schemaVersion))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryMalformed,
                "The trusted adapter registry has an invalid shape.");
        }

        if (schemaVersion != 1)
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryUnsupportedSchema,
                "The trusted adapter registry schema is not supported.");
        }

        if (!StrictJson.TryGetSafeInteger(root, "registryVersion", out var registryVersion) ||
            registryVersion <= 0)
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryVersionInvalid,
                "The trusted adapter registry version is invalid.");
        }

        if (!StrictJson.TryGetRequiredString(root, "contractVersion", out var contractVersion) ||
            !string.Equals(contractVersion, contract.ContractVersion, StringComparison.Ordinal))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryIntegrityFailed,
                "The trusted adapter registry does not bind the configured contract.");
        }

        var rules = ParseRules(root, contract);
        var tombstones = ParseTombstones(root, registryVersion);
        ValidateRuleIdentities(rules, tombstones);
        ValidateNoOverlap(rules);

        var canonical = CanonicalJson.WriteCanonicalDocument(
            root,
            CanonicalDocumentKind.Registry);
        return new RegistryDefinition(
            registryVersion,
            contractVersion,
            Array.AsReadOnly(rules.ToArray()),
            Array.AsReadOnly(tombstones.ToArray()),
            canonical,
            CanonicalJson.Sha256(canonical));
    }

    private static JsonDocument ParseTrustedJson(
        ReadOnlyMemory<byte> bytes,
        CompatibilityReasonCode reason,
        string name)
    {
        if (bytes.Length is 0 or > MaximumConfigurationBytes ||
            !StrictJson.TryParse(
                bytes,
                MaximumConfigurationDepth,
                out var document,
                out _))
        {
            throw new CompatibilityConfigurationException(
                reason,
                $"The trusted compatibility {name} is not valid strict JSON.");
        }

        return document;
    }

    private static void ParseContractLimits(
        JsonElement root,
        out int descriptorBytes,
        out int descriptorComponents,
        out int jsonDepth)
    {
        if (!root.TryGetProperty("limits", out var limits) ||
            !StrictJson.HasOnlyProperties(
                limits,
                "descriptorBytes",
                "descriptorComponents",
                "jsonDepth") ||
            !StrictJson.TryGetSafeInteger(limits, "descriptorBytes", out descriptorBytes) ||
            descriptorBytes != DescriptorParser.MaximumDescriptorBytes ||
            !StrictJson.TryGetSafeInteger(
                limits,
                "descriptorComponents",
                out descriptorComponents) ||
            descriptorComponents != 64 ||
            !StrictJson.TryGetSafeInteger(limits, "jsonDepth", out jsonDepth) ||
            jsonDepth is < 1 or > 64)
        {
            throw ContractError(
                CompatibilityReasonCode.ContractMalformed,
                "The trusted compatibility contract limits are invalid.");
        }
    }

    private static Dictionary<string, IReadOnlySet<HttpMethodKind>> ParseMethodCeilings(
        JsonElement root)
    {
        if (!root.TryGetProperty("methodCeilings", out var element) ||
            !StrictJson.HasOnlyProperties(element, "passiveResource", "reviewedApi"))
        {
            throw ContractError(
                CompatibilityReasonCode.ContractInvalidCapability,
                "The trusted compatibility contract method ceilings are invalid.");
        }

        var passive = ParseHttpMethodArray(
            element,
            "passiveResource",
            CompatibilityReasonCode.ContractInvalidCapability);
        var reviewedApi = ParseHttpMethodArray(
            element,
            "reviewedApi",
            CompatibilityReasonCode.ContractInvalidCapability);
        if (!passive.SetEquals([HttpMethodKind.Get, HttpMethodKind.Head]) ||
            !reviewedApi.SetEquals([
                HttpMethodKind.Get,
                HttpMethodKind.Post,
                HttpMethodKind.Options,
            ]))
        {
            throw ContractError(
                CompatibilityReasonCode.ContractInvalidCapability,
                "The trusted compatibility contract method ceilings exceed the product model.");
        }

        return new Dictionary<string, IReadOnlySet<HttpMethodKind>>(StringComparer.Ordinal)
        {
            ["passiveResource"] = passive,
            ["reviewedApi"] = reviewedApi,
        };
    }

    private static List<CapabilityKind> ParseBaseCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("baseCapabilities", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0)
        {
            throw ContractError(
                CompatibilityReasonCode.ContractInvalidCapability,
                "The trusted compatibility contract base capabilities are invalid.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<CapabilityKind>();
        foreach (var item in array.EnumerateArray())
        {
            if (!StrictJson.HasOnlyProperties(item, "capabilityId", "description") ||
                !TryGetCapabilityId(item, out var capabilityId) ||
                !TryGetBoundedText(item, "description", 240, out _) ||
                !ids.Add(capabilityId) ||
                !BaseCapabilityMap.TryGetValue(capabilityId, out var kind))
            {
                throw ContractError(
                    CompatibilityReasonCode.ContractInvalidCapability,
                    "The trusted compatibility contract has an invalid base capability.");
            }

            values.Add(kind);
        }

        return values;
    }

    private static Dictionary<string, CapabilityCeilingDefinition> ParseExtensionCapabilities(
        JsonElement root,
        Dictionary<string, IReadOnlySet<HttpMethodKind>> methodCeilings)
    {
        if (!root.TryGetProperty("extensionCapabilities", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0)
        {
            throw ContractError(
                CompatibilityReasonCode.ContractInvalidCapability,
                "The trusted compatibility contract extension capabilities are invalid.");
        }

        var values = new Dictionary<string, CapabilityCeilingDefinition>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (!StrictJson.HasOnlyProperties(
                    item,
                    "capabilityId",
                    "description",
                    "methodCeiling",
                    "resourceTypes",
                    "mainDocumentScriptAllowed") ||
                !TryGetCapabilityId(item, out var capabilityId) ||
                !TryGetBoundedText(item, "description", 240, out _) ||
                !ExtensionCapabilityMap.TryGetValue(capabilityId, out var shape) ||
                !StrictJson.TryGetRequiredString(item, "methodCeiling", out var methodCeiling) ||
                !string.Equals(methodCeiling, shape.MethodCeiling, StringComparison.Ordinal) ||
                !methodCeilings.TryGetValue(methodCeiling, out var methods) ||
                !item.TryGetProperty("mainDocumentScriptAllowed", out var scriptAllowed) ||
                scriptAllowed.ValueKind != (shape.MainDocumentScriptAllowed
                    ? JsonValueKind.True
                    : JsonValueKind.False))
            {
                throw ContractError(
                    CompatibilityReasonCode.ContractInvalidCapability,
                    "The trusted compatibility contract has an invalid extension capability.");
            }

            var resources = ParseResourceTypeArray(
                item,
                "resourceTypes",
                CompatibilityReasonCode.ContractInvalidCapability);
            if (resources.Count == 0 || !resources.SetEquals(shape.AllowedResources) ||
                !values.TryAdd(capabilityId, new CapabilityCeilingDefinition(
                    capabilityId,
                    shape.Kind,
                    methods,
                    resources,
                    shape.DocumentScope,
                    IsConditional: false)))
            {
                throw ContractError(
                    CompatibilityReasonCode.ContractInvalidCapability,
                    "The trusted compatibility contract extension ceiling is invalid.");
            }
        }

        return values;
    }

    private static void ParseConditionalCapabilities(
        JsonElement root,
        IDictionary<string, CapabilityCeilingDefinition> ceilings)
    {
        if (!root.TryGetProperty("conditionalCapabilities", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0)
        {
            throw ContractError(
                CompatibilityReasonCode.ContractInvalidCapability,
                "The trusted compatibility contract conditional capabilities are invalid.");
        }

        foreach (var item in array.EnumerateArray())
        {
            if (!StrictJson.HasOnlyProperties(
                    item,
                    "capabilityId",
                    "status",
                    "requiredEvidence") ||
                !TryGetCapabilityId(item, out var capabilityId) ||
                !ConditionalCapabilityMap.TryGetValue(capabilityId, out var kind) ||
                !StrictJson.TryGetRequiredString(item, "status", out var status) ||
                status != "not-authorized" ||
                !TryGetUniqueStringArray(item, "requiredEvidence", 32, out var evidence) ||
                evidence.Count < 4 ||
                evidence.Any(value => value is not (
                    "minimum-runtime" or
                    "evergreen-runtime" or
                    "same-endpoint-edge" or
                    "negative-arrival-count" or
                    "recovery" or
                    "signed-candidate")) ||
                !ceilings.TryAdd(capabilityId, new CapabilityCeilingDefinition(
                    capabilityId,
                    kind,
                    new HashSet<HttpMethodKind>(),
                    new HashSet<WebResourceKind>(),
                    CapabilityDocumentScope.TargetDocument,
                    IsConditional: true)))
            {
                throw ContractError(
                    CompatibilityReasonCode.ContractInvalidCapability,
                    "The trusted compatibility contract has an invalid conditional capability.");
            }
        }
    }

    private static void ParseForbiddenCapabilities(
        JsonElement root,
        IEnumerable<string> allowedCapabilityIds)
    {
        if (!TryGetUniqueStringArray(
                root,
                "permanentlyForbiddenCapabilities",
                512,
                out var forbidden) ||
            forbidden.Count == 0 ||
            forbidden.Any(value =>
                !CompatibilityNormalization.TryNormalizeCapabilityId(value, out _)) ||
            forbidden.Intersect(allowedCapabilityIds, StringComparer.Ordinal).Any())
        {
            throw ContractError(
                CompatibilityReasonCode.ContractInvalidCapability,
                "The trusted compatibility contract forbidden capabilities are invalid.");
        }
    }

    private static void ParseGlobalCspDebt(JsonElement root)
    {
        if (!root.TryGetProperty("globalCspDebt", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw ContractError(
                CompatibilityReasonCode.ContractMalformed,
                "The trusted compatibility contract CSP debt is invalid.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (!StrictJson.HasOnlyProperties(
                    item,
                    "capabilityId",
                    "status",
                    "description") ||
                !TryGetCapabilityId(item, out var capabilityId) ||
                !ids.Add(capabilityId) ||
                !StrictJson.TryGetRequiredString(item, "status", out var status) ||
                status != "blocked-pending-evidence" ||
                !TryGetBoundedText(item, "description", 240, out _))
            {
                throw ContractError(
                    CompatibilityReasonCode.ContractInvalidCapability,
                    "The trusted compatibility contract CSP debt is invalid.");
            }
        }
    }

    private static List<RuleDefinition> ParseRules(
        JsonElement root,
        ContractDefinition contract)
    {
        if (!root.TryGetProperty("activeRules", out var rulesElement) ||
            rulesElement.ValueKind != JsonValueKind.Array ||
            rulesElement.GetArrayLength() > MaximumRules)
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryMalformed,
                "The trusted adapter registry rules are invalid.");
        }

        return rulesElement.EnumerateArray()
            .Select(element => ParseRule(element, contract))
            .ToList();
    }

    private static RuleDefinition ParseRule(
        JsonElement element,
        ContractDefinition contract)
    {
        if (!StrictJson.HasOnlyProperties(
                element,
                "ruleId",
                "ruleVersion",
                "contractVersion",
                "evidenceBaseline",
                "match",
                "purpose",
                "grants") ||
            !StrictJson.TryGetRequiredString(element, "ruleId", out var ruleId) ||
            !CompatibilityNormalization.TryNormalizeRuleId(ruleId, out ruleId) ||
            !StrictJson.TryGetRequiredString(element, "ruleVersion", out var ruleVersion) ||
            !SemanticVersion.TryParseExact(ruleVersion, out _) ||
            !StrictJson.TryGetRequiredString(element, "contractVersion", out var contractVersion) ||
            !string.Equals(contractVersion, contract.ContractVersion, StringComparison.Ordinal))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryMalformed,
                "The trusted adapter registry contains an invalid rule identity.");
        }

        ValidateEvidenceBaseline(element);
        if (!element.TryGetProperty("match", out var match) ||
            !StrictJson.HasOnlyProperties(
                match,
                "uiId",
                "minimumUiVersion",
                "maximumUiVersion",
                "sourceRev",
                "adapterKey") ||
            !StrictJson.TryGetRequiredString(match, "uiId", out var uiId) ||
            !CompatibilityNormalization.TryNormalizeUiId(uiId, out uiId) ||
            !StrictJson.TryGetRequiredString(
                match,
                "minimumUiVersion",
                out var minimumVersionText) ||
            !SemanticVersion.TryParseExact(minimumVersionText, out var minimumVersion) ||
            !StrictJson.TryGetRequiredString(
                match,
                "maximumUiVersion",
                out var maximumVersionText) ||
            !SemanticVersion.TryParseExact(maximumVersionText, out var maximumVersion) ||
            !IsValidVersionRange(minimumVersion, maximumVersion) ||
            !StrictJson.TryGetOptionalString(match, "sourceRev", out var sourceRev) ||
            !CompatibilityNormalization.TryNormalizeSourceRevision(sourceRev, out sourceRev) ||
            !StrictJson.TryGetRequiredString(match, "adapterKey", out var adapterKey) ||
            !CompatibilityNormalization.TryNormalizeAdapterKey(adapterKey, out adapterKey))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryMalformed,
                "The trusted adapter registry contains an invalid rule match.");
        }

        if (!element.TryGetProperty("purpose", out var purposeElement) ||
            !StrictJson.HasOnlyProperties(purposeElement, "displayName", "diagnosticName") ||
            !TryGetBoundedText(purposeElement, "displayName", 160, out var purpose) ||
            !StrictJson.TryGetRequiredString(
                purposeElement,
                "diagnosticName",
                out var diagnosticName) ||
            !CompatibilityNormalization.TryNormalizeDiagnosticName(
                diagnosticName,
                out diagnosticName) ||
            !element.TryGetProperty("grants", out var grantsElement) ||
            grantsElement.ValueKind != JsonValueKind.Array ||
            grantsElement.GetArrayLength() is 0 or > MaximumCapabilitiesPerRule)
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryMalformed,
                "The trusted adapter registry contains invalid rule metadata.");
        }

        var grants = grantsElement.EnumerateArray()
            .Select(grant => ParseGrant(grant, purpose, contract))
            .ToArray();
        if (grants.Select(grant => grant.FullKey)
            .Distinct(StringComparer.Ordinal).Count() != grants.Length)
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryIntegrityFailed,
                "The trusted adapter registry contains duplicate capabilities.");
        }

        return new RuleDefinition(
            ruleId,
            ruleVersion,
            contractVersion,
            uiId,
            minimumVersion,
            maximumVersion,
            sourceRev,
            adapterKey,
            purpose,
            diagnosticName,
            Array.AsReadOnly(grants));
    }

    private static void ValidateEvidenceBaseline(JsonElement rule)
    {
        if (!rule.TryGetProperty("evidenceBaseline", out var baseline) ||
            !StrictJson.HasOnlyProperties(
                baseline,
                "harnessCommit",
                "lanPluginVersion",
                "routeVersion") ||
            !StrictJson.TryGetRequiredString(baseline, "harnessCommit", out var commit) ||
            !CommitPattern().IsMatch(commit) ||
            !StrictJson.TryGetRequiredString(
                baseline,
                "lanPluginVersion",
                out var pluginVersion) ||
            !SemanticVersion.TryParseExact(pluginVersion, out _) ||
            !TryGetBoundedText(baseline, "routeVersion", 128, out _))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryMalformed,
                "The trusted adapter registry evidence baseline is invalid.");
        }
    }

    private static GrantDefinition ParseGrant(
        JsonElement element,
        string purpose,
        ContractDefinition contract)
    {
        if (!StrictJson.HasOnlyProperties(
                element,
                "capabilityId",
                "origin",
                "path",
                "methods",
                "resourceTypes",
                "redirectPolicy",
                "parentOrigin",
                "queryKeys",
                "scriptSha256") ||
            !TryGetCapabilityId(element, out var capabilityId) ||
            !contract.CapabilityCeilings.TryGetValue(capabilityId, out var ceiling) ||
            ceiling.IsConditional)
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryInvalidCapability,
                "The trusted adapter registry contains an invalid capability.");
        }

        if (ceiling.Kind == CapabilityKind.ReviewedInlineScriptSha256)
        {
            return ParseReviewedInlineScriptGrant(
                element,
                capabilityId,
                purpose,
                ceiling);
        }

        var isTargetWebSocket = ceiling.Kind == CapabilityKind.TargetWebSocket;
        if (!StrictJson.TryGetOptionalString(element, "origin", out var origin) ||
            isTargetWebSocket && origin is not null ||
            !isTargetWebSocket &&
            (origin is null || !TryGetCanonicalHttpsOrigin(origin, out origin)) ||
            !element.TryGetProperty("path", out var pathElement) ||
            !StrictJson.HasOnlyProperties(pathElement, "kind", "value") ||
            !StrictJson.TryGetRequiredString(pathElement, "kind", out var pathKind) ||
            !TryMapPathMatch(pathKind, out var pathMatch) ||
            !StrictJson.TryGetRequiredString(pathElement, "value", out var path) ||
            path.Length > 1024 ||
            !CompatibilityNormalization.TryNormalizePath(path, pathMatch, out var normalizedPath) ||
            !string.Equals(path, normalizedPath, StringComparison.Ordinal) ||
            !StrictJson.TryGetRequiredString(element, "redirectPolicy", out var redirectPolicy) ||
            redirectPolicy != "none" ||
            !StrictJson.TryGetOptionalString(element, "parentOrigin", out var parentOrigin) ||
            parentOrigin is not null &&
            !TryGetCanonicalHttpsOrigin(parentOrigin, out parentOrigin) ||
            element.TryGetProperty("scriptSha256", out _))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryInvalidCapability,
                "The trusted adapter registry contains an invalid capability.");
        }

        var methods = ParseHttpMethodArray(
            element,
            "methods",
            CompatibilityReasonCode.RegistryInvalidCapability);
        var resources = ParseResourceTypeArray(
            element,
            "resourceTypes",
            CompatibilityReasonCode.RegistryInvalidCapability);
        if (!TryGetOptionalUniqueStringArray(element, "queryKeys", 256, out var queryKeys) ||
            queryKeys.Any(value =>
                !CompatibilityNormalization.TryNormalizeQueryKey(value, out _)))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryInvalidCapability,
                "The trusted adapter registry capability query keys are invalid.");
        }

        var grant = new GrantDefinition(
            capabilityId,
            ceiling.Kind,
            origin,
            parentOrigin,
            path,
            pathMatch,
            Array.AsReadOnly(methods.Order().ToArray()),
            Array.AsReadOnly(resources.Order().ToArray()),
            Array.AsReadOnly(queryKeys.Order(StringComparer.Ordinal).ToArray()),
            ceiling.DocumentScope,
            ScriptSha256: null,
            purpose);
        if (!methods.IsSubsetOf(ceiling.Methods) ||
            !resources.IsSubsetOf(ceiling.ResourceKinds) ||
            !CompatibilityNormalization.IsGrantShapeValid(grant))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryInvalidCapability,
                "The trusted adapter registry capability is outside the global ceiling.");
        }

        return grant;
    }

    private static GrantDefinition ParseReviewedInlineScriptGrant(
        JsonElement element,
        string capabilityId,
        string purpose,
        CapabilityCeilingDefinition ceiling)
    {
        if (!StrictJson.HasOnlyProperties(
                element,
                "capabilityId",
                "scriptSha256") ||
            !StrictJson.TryGetRequiredString(
                element,
                "scriptSha256",
                out var scriptSha256) ||
            !CompatibilityNormalization.TryNormalizeSha256Base64(
                scriptSha256,
                out scriptSha256))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryInvalidCapability,
                "The trusted adapter registry inline script digest is invalid.");
        }

        var grant = new GrantDefinition(
            capabilityId,
            ceiling.Kind,
            Origin: null,
            ParentOrigin: null,
            Path: null,
            CapabilityPathMatch.None,
            Methods: Array.Empty<HttpMethodKind>(),
            ResourceKinds: Array.Empty<WebResourceKind>(),
            QueryKeys: Array.Empty<string>(),
            ceiling.DocumentScope,
            scriptSha256,
            purpose);
        if (!CompatibilityNormalization.IsGrantShapeValid(grant))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryInvalidCapability,
                "The trusted adapter registry inline script digest is outside the global ceiling.");
        }

        return grant;
    }

    private static List<TombstoneDefinition> ParseTombstones(
        JsonElement root,
        int registryVersion)
    {
        if (!root.TryGetProperty("tombstones", out var element) ||
            element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() > MaximumRules)
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryMalformed,
                "The trusted adapter registry tombstones are invalid.");
        }

        var tombstones = new List<TombstoneDefinition>();
        foreach (var item in element.EnumerateArray())
        {
            if (!StrictJson.HasOnlyProperties(
                    item,
                    "ruleId",
                    "revokedFromRegistryVersion",
                    "severity",
                    "reasonCode") ||
                !StrictJson.TryGetRequiredString(item, "ruleId", out var ruleId) ||
                !CompatibilityNormalization.TryNormalizeRuleId(ruleId, out ruleId) ||
                !StrictJson.TryGetSafeInteger(
                    item,
                    "revokedFromRegistryVersion",
                    out var revokedVersion) ||
                revokedVersion <= 0 || revokedVersion > registryVersion ||
                !StrictJson.TryGetRequiredString(item, "severity", out var severity) ||
                severity is not ("Critical" or "High" or "Medium" or "Low") ||
                !StrictJson.TryGetRequiredString(item, "reasonCode", out var reasonCode) ||
                !TombstoneReasonCodes.Contains(reasonCode))
            {
                throw RegistryError(
                    CompatibilityReasonCode.RegistryMalformed,
                    "The trusted adapter registry contains an invalid tombstone.");
            }

            tombstones.Add(new TombstoneDefinition(
                ruleId,
                revokedVersion,
                severity,
                reasonCode));
        }

        return tombstones;
    }

    private static void ValidateRuleIdentities(
        List<RuleDefinition> rules,
        List<TombstoneDefinition> tombstones)
    {
        if (rules.GroupBy(rule => rule.RuleId, StringComparer.Ordinal)
                .Any(group => group.Select(rule => rule.RuleVersion)
                    .Distinct(StringComparer.Ordinal).Count() != group.Count()) ||
            tombstones.Select(tombstone => tombstone.RuleId)
                .Distinct(StringComparer.Ordinal).Count() != tombstones.Count)
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryIntegrityFailed,
                "The trusted adapter registry reuses a rule identity.");
        }

        var activeIds = rules.Select(rule => rule.RuleId)
            .ToHashSet(StringComparer.Ordinal);
        if (tombstones.Any(tombstone => activeIds.Contains(tombstone.RuleId)))
        {
            throw RegistryError(
                CompatibilityReasonCode.RegistryTombstoneConflict,
                "The trusted adapter registry reactivates a tombstoned rule identity.");
        }
    }

    private static void ValidateNoOverlap(List<RuleDefinition> rules)
    {
        for (var leftIndex = 0; leftIndex < rules.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < rules.Count; rightIndex++)
            {
                var left = rules[leftIndex];
                var right = rules[rightIndex];
                if (string.Equals(left.ContractVersion, right.ContractVersion, StringComparison.Ordinal) &&
                    string.Equals(left.UiId, right.UiId, StringComparison.Ordinal) &&
                    string.Equals(left.AdapterKey, right.AdapterKey, StringComparison.Ordinal) &&
                    SourceRevisionsOverlap(left.SourceRev, right.SourceRev) &&
                    left.MinimumUiVersion.CompareTo(right.MaximumUiVersion) <= 0 &&
                    right.MinimumUiVersion.CompareTo(left.MaximumUiVersion) <= 0)
                {
                    throw RegistryError(
                        CompatibilityReasonCode.RegistryRuleOverlap,
                        "The trusted adapter registry contains overlapping match domains.");
                }
            }
        }
    }

    private static HashSet<HttpMethodKind> ParseHttpMethodArray(
        JsonElement element,
        string propertyName,
        CompatibilityReasonCode reasonCode)
    {
        if (!element.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() is 0 or > 3)
        {
            throw ConfigurationError(reasonCode, "A trusted HTTP method list is invalid.");
        }

        var values = new HashSet<HttpMethodKind>();
        foreach (var item in array.EnumerateArray())
        {
            var method = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
            var value = method switch
            {
                "GET" => HttpMethodKind.Get,
                "HEAD" => HttpMethodKind.Head,
                "POST" => HttpMethodKind.Post,
                "OPTIONS" => HttpMethodKind.Options,
                _ => throw ConfigurationError(
                    reasonCode,
                    "A trusted HTTP method value is invalid."),
            };
            if (!values.Add(value))
            {
                throw ConfigurationError(
                    reasonCode,
                    "A trusted HTTP method list contains duplicates.");
            }
        }

        return values;
    }

    private static HashSet<WebResourceKind> ParseResourceTypeArray(
        JsonElement element,
        string propertyName,
        CompatibilityReasonCode reasonCode)
    {
        if (!element.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            throw ConfigurationError(reasonCode, "A trusted resource type list is invalid.");
        }

        var values = new HashSet<WebResourceKind>();
        foreach (var item in array.EnumerateArray())
        {
            var resource = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
            var value = resource switch
            {
                "document" => WebResourceKind.Document,
                "frame" => WebResourceKind.Frame,
                "stylesheet" => WebResourceKind.Stylesheet,
                "image" => WebResourceKind.Image,
                "media" => WebResourceKind.Media,
                "font" => WebResourceKind.Font,
                "script" => WebResourceKind.Script,
                "fetch" => WebResourceKind.Fetch,
                "xhr" => WebResourceKind.XmlHttpRequest,
                "websocket" => WebResourceKind.WebSocket,
                "worker" => WebResourceKind.Worker,
                _ => throw ConfigurationError(
                    reasonCode,
                    "A trusted resource type value is invalid."),
            };
            if (!values.Add(value))
            {
                throw ConfigurationError(
                    reasonCode,
                    "A trusted resource type list contains duplicates.");
            }
        }

        return values;
    }

    private static bool TryGetCapabilityId(JsonElement element, out string capabilityId) =>
        StrictJson.TryGetRequiredString(element, "capabilityId", out capabilityId) &&
        CompatibilityNormalization.TryNormalizeCapabilityId(capabilityId, out capabilityId);

    private static bool TryGetBoundedText(
        JsonElement element,
        string propertyName,
        int maximumLength,
        out string value) =>
        StrictJson.TryGetRequiredString(element, propertyName, out value) &&
        value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static bool TryGetUniqueStringArray(
        JsonElement element,
        string propertyName,
        int maximumItems,
        out List<string> values)
    {
        values = [];
        if (!element.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() > maximumItems)
        {
            return false;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(item.GetString()))
            {
                return false;
            }

            values.Add(item.GetString()!);
        }

        return values.Distinct(StringComparer.Ordinal).Count() == values.Count;
    }

    private static bool TryGetOptionalUniqueStringArray(
        JsonElement element,
        string propertyName,
        int maximumItems,
        out List<string> values)
    {
        if (!element.TryGetProperty(propertyName, out _))
        {
            values = [];
            return true;
        }

        return TryGetUniqueStringArray(element, propertyName, maximumItems, out values);
    }

    private static bool TryGetCanonicalHttpsOrigin(
        string value,
        out string normalized)
    {
        normalized = string.Empty;
        return value.Length <= 512 &&
               CanonicalHttpsOriginPattern().IsMatch(value) &&
               CompatibilityNormalization.TryNormalizeOrigin(value, out normalized) &&
               normalized.StartsWith("https://", StringComparison.Ordinal) &&
               string.Equals(value, normalized, StringComparison.Ordinal);
    }

    private static bool TryMapPathMatch(
        string value,
        out CapabilityPathMatch pathMatch)
    {
        pathMatch = value switch
        {
            "exact" => CapabilityPathMatch.Exact,
            "segment-prefix" => CapabilityPathMatch.SegmentPrefix,
            _ => CapabilityPathMatch.None,
        };
        return pathMatch != CapabilityPathMatch.None;
    }

    private static bool SourceRevisionsOverlap(string? left, string? right) =>
        left is null || right is null || string.Equals(left, right, StringComparison.Ordinal);

    private static bool IsValidVersionRange(
        SemanticVersion minimum,
        SemanticVersion maximum)
    {
        if (string.Equals(minimum.Text, maximum.Text, StringComparison.Ordinal))
        {
            return true;
        }

        return minimum.CompareTo(maximum) <= 0 &&
               minimum.Prerelease is null && minimum.Build is null &&
               maximum.Prerelease is null && maximum.Build is null;
    }

    private static CompatibilityConfigurationException ContractError(
        CompatibilityReasonCode reasonCode,
        string message) =>
        new(reasonCode, message);

    private static CompatibilityConfigurationException RegistryError(
        CompatibilityReasonCode reasonCode,
        string message) =>
        new(reasonCode, message);

    private static CompatibilityConfigurationException ConfigurationError(
        CompatibilityReasonCode reasonCode,
        string message) =>
        new(reasonCode, message);

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex(
        "^https://[a-z0-9.-]+:[1-9][0-9]{0,4}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalHttpsOriginPattern();

    private sealed record ExtensionCapabilityShape(
        CapabilityKind Kind,
        string MethodCeiling,
        CapabilityDocumentScope DocumentScope,
        IReadOnlySet<WebResourceKind> AllowedResources,
        bool MainDocumentScriptAllowed = false);
}
