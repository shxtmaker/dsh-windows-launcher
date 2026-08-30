using System.Text.Json;

namespace DshLauncher.Compatibility;

internal static class DescriptorParser
{
    public const int MaximumDescriptorBytes = 64 * 1024;
    private const int MaximumAdapterKeys = 32;

    public static bool TryParse(
        BoundedDescriptorResponse response,
        ContractDefinition contract,
        out DescriptorDefinition descriptor,
        out CompatibilityReasonCode reason)
    {
        descriptor = null!;
        reason = CompatibilityReasonCode.None;
        if (response.IsMissing)
        {
            reason = CompatibilityReasonCode.DescriptorMissing;
            return false;
        }

        if (response.WasRedirected)
        {
            reason = CompatibilityReasonCode.DescriptorRedirected;
            return false;
        }

        if (response.StatusCode != 200)
        {
            reason = CompatibilityReasonCode.DescriptorStatusRejected;
            return false;
        }

        if (!IsExactJsonUtf8ContentType(response.ContentType))
        {
            reason = CompatibilityReasonCode.DescriptorContentTypeRejected;
            return false;
        }

        var body = response.BodyBuffer;
        if (body.Length > contract.MaximumDescriptorBytes)
        {
            reason = CompatibilityReasonCode.DescriptorTooLarge;
            return false;
        }

        if (!StrictJson.TryParse(
                body,
                contract.MaximumJsonDepth,
                out var document,
                out var parseError))
        {
            reason = parseError switch
            {
                StrictJsonError.TooDeep => CompatibilityReasonCode.DescriptorTooDeep,
                StrictJsonError.DuplicateProperty =>
                    CompatibilityReasonCode.DescriptorDuplicateProperty,
                _ => CompatibilityReasonCode.DescriptorMalformed,
            };
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                reason = CompatibilityReasonCode.DescriptorMalformed;
                return false;
            }

            if (!StrictJson.HasOnlyProperties(
                    root,
                    "schemaVersion",
                    "contractVersion",
                    "components",
                    "extensions"))
            {
                reason = CompatibilityReasonCode.DescriptorUnknownField;
                return false;
            }

            if (!StrictJson.TryGetSafeInteger(root, "schemaVersion", out var schemaVersion))
            {
                reason = CompatibilityReasonCode.DescriptorMalformed;
                return false;
            }

            if (schemaVersion != 1)
            {
                reason = CompatibilityReasonCode.DescriptorUnsupportedSchema;
                return false;
            }

            if (!StrictJson.TryGetRequiredString(
                    root,
                    "contractVersion",
                    out var contractVersion) ||
                !SemanticVersion.TryParseExact(contractVersion, out _))
            {
                reason = CompatibilityReasonCode.DescriptorInvalidIdentity;
                return false;
            }

            if (!string.Equals(
                    contractVersion,
                    contract.ContractVersion,
                    StringComparison.Ordinal))
            {
                reason = CompatibilityReasonCode.DescriptorUnsupportedContract;
                return false;
            }

            if (root.TryGetProperty("extensions", out var extensions) &&
                (extensions.ValueKind != JsonValueKind.Object ||
                 !HasNamespacedExtensionKeys(extensions) ||
                 ContainsAuthorizationField(extensions)))
            {
                reason = extensions.ValueKind == JsonValueKind.Object
                    ? CompatibilityReasonCode.DescriptorUnknownField
                    : CompatibilityReasonCode.DescriptorMalformed;
                return false;
            }

            if (!root.TryGetProperty("components", out var componentsElement) ||
                componentsElement.ValueKind != JsonValueKind.Array)
            {
                reason = CompatibilityReasonCode.DescriptorMalformed;
                return false;
            }

            if (componentsElement.GetArrayLength() is 0 ||
                componentsElement.GetArrayLength() > contract.MaximumDescriptorComponents)
            {
                reason = CompatibilityReasonCode.DescriptorTooManyComponents;
                return false;
            }

            var components = new List<DescriptorComponent>();
            foreach (var element in componentsElement.EnumerateArray())
            {
                if (!TryParseComponent(element, out var component, out reason))
                {
                    return false;
                }

                components.Add(component);
            }

            if (components.GroupBy(component => component.UiId, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
            {
                reason = CompatibilityReasonCode.DescriptorIdentityConflict;
                return false;
            }

            var canonical = CanonicalJson.WriteDescriptor(
                schemaVersion,
                contractVersion,
                components);
            descriptor = new DescriptorDefinition(
                schemaVersion,
                contractVersion,
                Array.AsReadOnly(components.ToArray()),
                canonical,
                CanonicalJson.Sha256(canonical));
            return true;
        }
    }

    private static bool TryParseComponent(
        JsonElement element,
        out DescriptorComponent component,
        out CompatibilityReasonCode reason)
    {
        component = null!;
        reason = CompatibilityReasonCode.None;
        if (element.ValueKind != JsonValueKind.Object)
        {
            reason = CompatibilityReasonCode.DescriptorMalformed;
            return false;
        }

        if (!StrictJson.HasOnlyProperties(
                element,
                "uiId",
                "uiVersion",
                "sourceRev",
                "adapterKeys",
                "extensions"))
        {
            reason = CompatibilityReasonCode.DescriptorUnknownField;
            return false;
        }

        if (!StrictJson.TryGetRequiredString(element, "uiId", out var uiId) ||
            !CompatibilityNormalization.TryNormalizeUiId(uiId, out uiId) ||
            !StrictJson.TryGetRequiredString(element, "uiVersion", out var uiVersionText) ||
            !SemanticVersion.TryParseExact(uiVersionText, out var uiVersion) ||
            !StrictJson.TryGetOptionalString(element, "sourceRev", out var sourceRev) ||
            !CompatibilityNormalization.TryNormalizeSourceRevision(sourceRev, out sourceRev))
        {
            reason = CompatibilityReasonCode.DescriptorInvalidIdentity;
            return false;
        }

        if (element.TryGetProperty("extensions", out var extensions) &&
            (extensions.ValueKind != JsonValueKind.Object ||
             !HasNamespacedExtensionKeys(extensions) ||
             ContainsAuthorizationField(extensions)))
        {
            reason = extensions.ValueKind == JsonValueKind.Object
                ? CompatibilityReasonCode.DescriptorUnknownField
                : CompatibilityReasonCode.DescriptorMalformed;
            return false;
        }

        if (!element.TryGetProperty("adapterKeys", out var keysElement) ||
            keysElement.ValueKind != JsonValueKind.Array ||
            keysElement.GetArrayLength() is 0 or > MaximumAdapterKeys)
        {
            reason = CompatibilityReasonCode.DescriptorMalformed;
            return false;
        }

        var adapterKeys = new List<string>();
        foreach (var keyElement in keysElement.EnumerateArray())
        {
            if (keyElement.ValueKind != JsonValueKind.String ||
                !CompatibilityNormalization.TryNormalizeAdapterKey(
                    keyElement.GetString() ?? string.Empty,
                    out var adapterKey))
            {
                reason = CompatibilityReasonCode.DescriptorInvalidIdentity;
                return false;
            }

            adapterKeys.Add(adapterKey);
        }

        if (adapterKeys.Distinct(StringComparer.Ordinal).Count() != adapterKeys.Count)
        {
            reason = CompatibilityReasonCode.DescriptorIdentityConflict;
            return false;
        }

        component = new DescriptorComponent(
            uiId,
            uiVersion,
            sourceRev,
            Array.AsReadOnly(adapterKeys.Order(StringComparer.Ordinal).ToArray()));
        return true;
    }

    private static bool IsExactJsonUtf8ContentType(string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        var parts = contentType.Split(';', StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               string.Equals(parts[0], "application/json", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(parts[1], "charset=utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAuthorizationField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsAuthorizationField);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (IsAuthorizationFieldName(property.Name) ||
                ContainsAuthorizationField(property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNamespacedExtensionKeys(JsonElement extensions) =>
        extensions.EnumerateObject().All(property =>
            CompatibilityNormalization.TryNormalizeUiId(
                property.Name,
                out var normalized) &&
            string.Equals(property.Name, normalized, StringComparison.Ordinal));

    private static bool IsAuthorizationFieldName(string propertyName) => propertyName is
        "origin" or
        "parentOrigin" or
        "path" or
        "methods" or
        "resourceTypes" or
        "resourceKinds" or
        "queryKeys" or
        "redirectPolicy" or
        "capabilityId" or
        "capabilities" or
        "csp" or
        "permissions" or
        "documentScope";
}
