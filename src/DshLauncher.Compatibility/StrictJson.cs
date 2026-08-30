using System.Buffers.Text;
using System.Text.Json;

namespace DshLauncher.Compatibility;

internal static class StrictJson
{
    private const int ReaderMaximumDepth = 256;

    public static bool TryParse(
        ReadOnlyMemory<byte> utf8Json,
        int maximumDepth,
        out JsonDocument document,
        out StrictJsonError error)
    {
        document = null!;
        error = StrictJsonError.None;
        try
        {
            var reader = new Utf8JsonReader(
                utf8Json.Span,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = ReaderMaximumDepth,
                });
            var objectProperties = new Stack<HashSet<string>?>();
            var rootValues = 0;
            while (reader.Read())
            {
                if (reader.CurrentDepth > maximumDepth)
                {
                    error = StrictJsonError.TooDeep;
                    return false;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                        if (reader.CurrentDepth == 0)
                        {
                            rootValues++;
                        }

                        break;
                    case JsonTokenType.StartArray:
                        objectProperties.Push(null);
                        if (reader.CurrentDepth == 0)
                        {
                            rootValues++;
                        }

                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        objectProperties.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        {
                            var propertyName = reader.GetString() ?? string.Empty;
                            var normalizedPropertyName = propertyName.Normalize();
                            var properties = objectProperties.Peek();
                            if (!string.Equals(
                                    propertyName,
                                    normalizedPropertyName,
                                    StringComparison.Ordinal))
                            {
                                error = StrictJsonError.Malformed;
                                return false;
                            }

                            if (properties is null || !properties.Add(propertyName))
                            {
                                error = StrictJsonError.DuplicateProperty;
                                return false;
                            }

                            break;
                        }
                    case JsonTokenType.String:
                        var text = reader.GetString() ?? string.Empty;
                        if (!string.Equals(text, text.Normalize(), StringComparison.Ordinal))
                        {
                            error = StrictJsonError.Malformed;
                            return false;
                        }

                        if (reader.CurrentDepth == 0)
                        {
                            rootValues++;
                        }

                        break;
                    case JsonTokenType.Number:
                        if (!TryParseSafeInteger(reader.ValueSpan, out _))
                        {
                            error = StrictJsonError.Malformed;
                            return false;
                        }

                        if (reader.CurrentDepth == 0)
                        {
                            rootValues++;
                        }

                        break;
                    default:
                        if (reader.CurrentDepth == 0)
                        {
                            rootValues++;
                        }

                        break;
                }

                if (rootValues > 1)
                {
                    error = StrictJsonError.Malformed;
                    return false;
                }
            }

            if (rootValues != 1 || objectProperties.Count != 0)
            {
                error = StrictJsonError.Malformed;
                return false;
            }

            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = ReaderMaximumDepth,
                });
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            error = StrictJsonError.Malformed;
            return false;
        }
    }

    public static bool HasOnlyProperties(
        JsonElement element,
        params string[] allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        return element.EnumerateObject().All(property => allowed.Contains(property.Name));
    }

    public static bool TryGetRequiredString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               (value = property.GetString() ?? string.Empty).Length > 0 &&
               string.Equals(value, value.Normalize(), StringComparison.Ordinal);
    }

    public static bool TryGetOptionalString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null && value.Length > 0 &&
               string.Equals(value, value.Normalize(), StringComparison.Ordinal);
    }

    public static bool TryGetSafeInteger(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = 0;
        return TryGetSafeInteger64(element, propertyName, out var longValue) &&
               longValue is >= int.MinValue and <= int.MaxValue &&
               (value = (int)longValue) == longValue;
    }

    public static bool TryGetSafeInteger64(
        JsonElement element,
        string propertyName,
        out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               TryParseSafeInteger(
                   System.Text.Encoding.UTF8.GetBytes(property.GetRawText()),
                   out value);
    }

    private static bool TryParseSafeInteger(
        ReadOnlySpan<byte> utf8Value,
        out long value)
    {
        const long MaximumSafeInteger = 9_007_199_254_740_991;
        value = 0;
        return Utf8Parser.TryParse(utf8Value, out value, out var consumed) &&
               consumed == utf8Value.Length &&
               value is >= -MaximumSafeInteger and <= MaximumSafeInteger;
    }
}
