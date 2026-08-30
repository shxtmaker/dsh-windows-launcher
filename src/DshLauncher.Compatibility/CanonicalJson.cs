using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DshLauncher.Compatibility;

internal static class CanonicalJson
{
    public static byte[] WriteCanonicalDocument(
        JsonElement root,
        CanonicalDocumentKind documentKind) =>
        Write(writer => WriteElement(writer, root, "$", documentKind));

    public static byte[] WriteDescriptor(
        int schemaVersion,
        string contractVersion,
        IEnumerable<DescriptorComponent> components) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("components");
            writer.WriteStartArray();
            foreach (var component in components.OrderBy(
                             component => component.UiId,
                             StringComparer.Ordinal)
                         .ThenBy(
                             component => component.UiVersion.Text,
                             StringComparer.Ordinal)
                         .ThenBy(
                             component => component.SourceRev,
                             StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("adapterKeys");
                writer.WriteStartArray();
                foreach (var adapterKey in component.AdapterKeys.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(adapterKey);
                }

                writer.WriteEndArray();
                if (component.SourceRev is not null)
                {
                    writer.WriteString("sourceRev", component.SourceRev);
                }

                writer.WriteString("uiId", component.UiId);
                writer.WriteString("uiVersion", component.UiVersion.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("contractVersion", contractVersion);
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WriteEndObject();
        });

    public static byte[] WriteSnapshot(
        Guid targetId,
        string contractVersion,
        int registryVersion,
        string? descriptorSha256,
        IEnumerable<CapabilityGrant> baseCapabilities,
        IEnumerable<CapabilityGrant> extensionCapabilities,
        IEnumerable<RuleIdentity> matchedRules) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("baseCapabilities");
            writer.WriteStartArray();
            foreach (var grant in baseCapabilities.OrderBy(GrantSortKey, StringComparer.Ordinal))
            {
                WriteGrant(writer, grant);
            }

            writer.WriteEndArray();
            writer.WriteString("contractVersion", contractVersion);
            if (descriptorSha256 is not null)
            {
                writer.WriteString("descriptorSha256", descriptorSha256);
            }

            writer.WritePropertyName("extensionCapabilities");
            writer.WriteStartArray();
            foreach (var grant in extensionCapabilities.OrderBy(GrantSortKey, StringComparer.Ordinal))
            {
                WriteGrant(writer, grant);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("matchedRules");
            writer.WriteStartArray();
            foreach (var rule in matchedRules.OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
                         .ThenBy(rule => rule.RuleVersion, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("adapterKey", rule.AdapterKey);
                writer.WriteString("purpose", rule.Purpose);
                writer.WriteString("ruleId", rule.RuleId);
                writer.WriteString("ruleVersion", rule.RuleVersion);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("registryVersion", registryVersion);
            writer.WriteString("targetId", targetId.ToString("D"));
            writer.WriteEndObject();
        });

    public static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static byte[] Write(Action<Utf8JsonWriter> writeAction)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   output,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = false,
                       SkipValidation = false,
                   }))
        {
            writeAction(writer);
        }

        return output.WrittenSpan.ToArray();
    }

    private static void WriteGrant(Utf8JsonWriter writer, CapabilityGrant grant)
    {
        writer.WriteStartObject();
        writer.WriteString("documentScope", grant.DocumentScope.ToString());
        writer.WriteString("kind", grant.Kind.ToString());
        WriteMethodArray(writer, "methods", grant.Methods);
        if (grant.Origin is not null)
        {
            writer.WriteString("origin", grant.Origin);
        }

        if (grant.ParentOrigin is not null)
        {
            writer.WriteString("parentOrigin", grant.ParentOrigin);
        }

        if (grant.Path is not null)
        {
            writer.WriteString("path", grant.Path);
        }

        writer.WriteString("pathMatch", grant.PathMatch.ToString());
        writer.WriteString("purpose", grant.Purpose);
        writer.WritePropertyName("queryKeys");
        writer.WriteStartArray();
        foreach (var queryKey in grant.QueryKeys.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(queryKey);
        }

        writer.WriteEndArray();
        WriteEnumArray(writer, "resourceKinds", grant.ResourceKinds);
        writer.WriteEndObject();
    }

    private static void WriteEnumArray<T>(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<T> values)
        where T : struct, Enum
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values.Distinct().Order())
        {
            writer.WriteStringValue(value.ToString());
        }

        writer.WriteEndArray();
    }

    private static void WriteMethodArray(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<HttpMethodKind> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values.Distinct().Order())
        {
            writer.WriteStringValue(value.ToString().ToUpperInvariant());
        }

        writer.WriteEndArray();
    }

    private static string GrantSortKey(CapabilityGrant grant) => string.Join(
        '|',
        grant.Kind,
        grant.Origin ?? string.Empty,
        grant.ParentOrigin ?? string.Empty,
        grant.Path ?? string.Empty,
        grant.PathMatch,
        grant.DocumentScope,
        string.Join(',', grant.Methods),
        string.Join(',', grant.ResourceKinds),
        string.Join(',', grant.QueryKeys),
        grant.Purpose);

    private static void WriteElement(
        Utf8JsonWriter writer,
        JsonElement element,
        string path,
        CanonicalDocumentKind documentKind)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(
                        writer,
                        property.Value,
                        $"{path}.{property.Name}",
                        documentKind);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in OrderArray(element, path, documentKind))
                {
                    WriteElement(writer, item, $"{path}[]", documentKind);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteNumberValue(element.GetInt64());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException("Unsupported JSON token in canonical input.");
        }
    }

    private static IEnumerable<JsonElement> OrderArray(
        JsonElement array,
        string path,
        CanonicalDocumentKind documentKind)
    {
        var items = array.EnumerateArray().ToArray();
        if (path.EndsWith(".adapterKeys", StringComparison.Ordinal) ||
            path.EndsWith(".methods", StringComparison.Ordinal) ||
            path.EndsWith(".queryKeys", StringComparison.Ordinal) ||
            path.EndsWith(".requiredEvidence", StringComparison.Ordinal) ||
            path.EndsWith(".resourceTypes", StringComparison.Ordinal) ||
            path.EndsWith(".permanentlyForbiddenCapabilities", StringComparison.Ordinal))
        {
            return items.OrderBy(
                item => item.GetString(),
                StringComparer.Ordinal);
        }

        if (documentKind == CanonicalDocumentKind.Contract && path is
            "$.baseCapabilities" or
            "$.conditionalCapabilities" or
            "$.extensionCapabilities" or
            "$.globalCspDebt")
        {
            return items.OrderBy(
                item => item.GetProperty("capabilityId").GetString(),
                StringComparer.Ordinal);
        }

        if (documentKind == CanonicalDocumentKind.Registry && path == "$.activeRules")
        {
            return items.OrderBy(
                    item => item.GetProperty("ruleId").GetString(),
                    StringComparer.Ordinal)
                .ThenBy(
                    item => item.GetProperty("ruleVersion").GetString(),
                    StringComparer.Ordinal);
        }

        if (documentKind == CanonicalDocumentKind.Registry && path == "$.tombstones")
        {
            return items.OrderBy(
                    item => item.GetProperty("ruleId").GetString(),
                    StringComparer.Ordinal)
                .ThenBy(item => item.GetProperty("revokedFromRegistryVersion").GetInt64());
        }

        if (documentKind == CanonicalDocumentKind.Registry &&
            path.EndsWith(".grants", StringComparison.Ordinal))
        {
            return items.OrderBy(CanonicalSortKey, StringComparer.Ordinal);
        }

        return items;
    }

    private static string CanonicalSortKey(JsonElement element) =>
        Convert.ToHexString(Write(writer =>
            WriteElement(writer, element, "$", CanonicalDocumentKind.Registry)));
}

internal enum CanonicalDocumentKind
{
    Contract,
    Registry,
}
