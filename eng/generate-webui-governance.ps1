#requires -Version 7.4

[CmdletBinding()]
param(
    [string] $RepositoryRoot,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot 'artifacts/webui-governance'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

Add-Type -TypeDefinition @'
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class GovernanceJsonFile : IDisposable
{
    public GovernanceJsonFile(string path, JsonDocument document, byte[] canonicalBytes)
    {
        Path = path;
        Document = document;
        CanonicalBytes = canonicalBytes;
        CanonicalSha256 = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
    }

    public string Path { get; }
    public JsonDocument Document { get; }
    public byte[] CanonicalBytes { get; }
    public string CanonicalSha256 { get; }

    public void Dispose() => Document.Dispose();
}

public static class GovernanceJson
{
    private const long MaximumSafeInteger = 9007199254740991L;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly HashSet<string> SensitivePropertyNames = new HashSet<string>(
        new[]
        {
            "authorization",
            "clientsecret",
            "cookie",
            "cookievalue",
            "imagedata",
            "pagecontent",
            "password",
            "privatekey",
            "requestbody",
            "responsebody",
            "token"
        },
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex SensitiveValue = new Regex(
        @"(?i)(?:[?&](?:token|code|key|auth|signature)=[^&\s]+|authorization\s*:|bearer\s+[A-Za-z0-9._~-]+|set-cookie\s*:|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?:^|\s)(?:[A-Za-z]:\\|/home/|/Users/)[^\s]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static GovernanceJsonFile Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
        {
            throw new InvalidDataException("[STRICT_UTF8_BOM] UTF-8 BOM is forbidden: " + path);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("[STRICT_UTF8] Invalid UTF-8: " + path, exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("[STRICT_JSON_PARSE] Invalid JSON: " + path, exception);
        }

        try
        {
            ValidateStrict(document.RootElement, "$", path);
            return new GovernanceJsonFile(path, document, Canonicalize(document.RootElement));
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static byte[] CanonicalizeText(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        ValidateStrict(document.RootElement, "$", "generated text");
        return Canonicalize(document.RootElement);
    }

    public static string CanonicalSha256(JsonElement element)
    {
        return Convert.ToHexString(SHA256.HashData(Canonicalize(element))).ToLowerInvariant();
    }

    public static bool JsonEquals(JsonElement left, JsonElement right)
    {
        return Canonicalize(left).AsSpan().SequenceEqual(Canonicalize(right));
    }

    public static string FindSensitiveData(JsonElement element)
    {
        return FindSensitiveDataCore(element, "$", null);
    }

    private static void ValidateStrict(JsonElement element, string path, string source)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!property.Name.IsNormalized(NormalizationForm.FormC))
                    {
                        throw new InvalidDataException("[STRICT_UNICODE_NFC] Non-NFC property at " + path + " in " + source);
                    }
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException("[STRICT_DUPLICATE_KEY] Duplicate property " + property.Name + " at " + path + " in " + source);
                    }
                    ValidateStrict(property.Value, path + "/" + property.Name, source);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateStrict(item, path + "/" + index, source);
                    index++;
                }
                break;
            case JsonValueKind.String:
                string value = element.GetString() ?? string.Empty;
                if (!value.IsNormalized(NormalizationForm.FormC))
                {
                    throw new InvalidDataException("[STRICT_UNICODE_NFC] Non-NFC string at " + path + " in " + source);
                }
                break;
            case JsonValueKind.Number:
                string raw = element.GetRawText();
                if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0 ||
                    !element.TryGetInt64(out long integer) ||
                    integer < -MaximumSafeInteger || integer > MaximumSafeInteger)
                {
                    throw new InvalidDataException("[STRICT_SAFE_INTEGER] Only IEEE-754 safe integers are allowed at " + path + " in " + source);
                }
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw new InvalidDataException("[STRICT_JSON_KIND] Unsupported JSON value at " + path + " in " + source);
        }
    }

    private static byte[] Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false
        }))
        {
            WriteCanonical(writer, element);
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                JsonProperty[] properties = element.EnumerateObject().ToArray();
                Array.Sort(properties, (left, right) => string.CompareOrdinal(left.Name, right.Name));
                foreach (JsonProperty property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
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
                throw new InvalidDataException("[JCS_KIND] Unsupported JSON value kind.");
        }
    }

    private static string FindSensitiveDataCore(JsonElement element, string path, string propertyName)
    {
        if (propertyName != null && SensitivePropertyNames.Contains(propertyName))
        {
            return "[SENSITIVE_FIELD] " + path;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string result = FindSensitiveDataCore(property.Value, path + "/" + property.Name, property.Name);
                    if (result != null)
                    {
                        return result;
                    }
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string result = FindSensitiveDataCore(item, path + "/" + index, propertyName);
                    if (result != null)
                    {
                        return result;
                    }
                    index++;
                }
                break;
            case JsonValueKind.String:
                string value = element.GetString() ?? string.Empty;
                if (SensitiveValue.IsMatch(value))
                {
                    return "[SENSITIVE_VALUE] " + path;
                }
                break;
        }

        return null;
    }
}
'@ -Language CSharp

function New-GovernanceFailure {
    param(
        [Parameter(Mandatory)]
        [string] $Code,

        [Parameter(Mandatory)]
        [string] $Message
    )

    throw "[$Code] $Message"
}

function Get-JsonProperty {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $Element,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $value = [System.Text.Json.JsonElement]::new()
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object -and
        $Element.TryGetProperty($Name, [ref] $value)) {
        return $value
    }

    return $null
}

function Get-RequiredJsonProperty {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $Element,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $value = Get-JsonProperty -Element $Element -Name $Name
    if ($null -eq $value) {
        New-GovernanceFailure -Code 'JSON_REQUIRED_PROPERTY' -Message "$Path 缺少 $Name。"
    }
    return $value
}

function Resolve-LocalSchemaReference {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $SchemaRoot,

        [Parameter(Mandatory)]
        [string] $Reference
    )

    if (-not $Reference.StartsWith('#/', [StringComparison]::Ordinal)) {
        New-GovernanceFailure -Code 'SCHEMA_EXTERNAL_REF' -Message "禁止外部 Schema 引用：$Reference"
    }

    $current = $SchemaRoot
    foreach ($rawSegment in $Reference.Substring(2).Split('/')) {
        $segment = $rawSegment.Replace('~1', '/').Replace('~0', '~')
        $next = Get-JsonProperty -Element $current -Name $segment
        if ($null -eq $next) {
            New-GovernanceFailure -Code 'SCHEMA_REF_NOT_FOUND' -Message "无法解析 Schema 引用：$Reference"
        }
        $current = $next
    }
    return $current
}

function Test-JsonType {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $Instance,

        [Parameter(Mandatory)]
        [string] $ExpectedType
    )

    switch ($ExpectedType) {
        'object' { return $Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::Object }
        'array' { return $Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::Array }
        'string' { return $Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::String }
        'integer' { return $Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::Number }
        'boolean' {
            return $Instance.ValueKind -in @(
                [System.Text.Json.JsonValueKind]::True,
                [System.Text.Json.JsonValueKind]::False)
        }
        'null' { return $Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::Null }
        default {
            New-GovernanceFailure -Code 'SCHEMA_UNSUPPORTED_TYPE' -Message "不支持的类型：$ExpectedType"
        }
    }
}

function Assert-JsonSchema {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $Instance,

        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $Schema,

        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $SchemaRoot,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $referenceNode = Get-JsonProperty -Element $Schema -Name '$ref'
    if ($null -ne $referenceNode) {
        $resolved = Resolve-LocalSchemaReference `
            -SchemaRoot $SchemaRoot `
            -Reference $referenceNode.GetString()
        Assert-JsonSchema -Instance $Instance -Schema $resolved -SchemaRoot $SchemaRoot -Path $Path
        return
    }

    $typeNode = Get-JsonProperty -Element $Schema -Name 'type'
    if ($null -ne $typeNode) {
        $expectedType = $typeNode.GetString()
        if (-not (Test-JsonType -Instance $Instance -ExpectedType $expectedType)) {
            New-GovernanceFailure -Code 'SCHEMA_TYPE' -Message "$Path 应为 $expectedType，实际为 $($Instance.ValueKind)。"
        }
    }

    $constNode = Get-JsonProperty -Element $Schema -Name 'const'
    if ($null -ne $constNode -and
        -not [GovernanceJson]::JsonEquals($Instance, $constNode)) {
        New-GovernanceFailure -Code 'SCHEMA_CONST' -Message "$Path 不等于固定值。"
    }

    $enumNode = Get-JsonProperty -Element $Schema -Name 'enum'
    if ($null -ne $enumNode) {
        $matched = $false
        foreach ($candidate in $enumNode.EnumerateArray()) {
            if ([GovernanceJson]::JsonEquals($Instance, $candidate)) {
                $matched = $true
                break
            }
        }
        if (-not $matched) {
            New-GovernanceFailure -Code 'SCHEMA_ENUM' -Message "$Path 不属于允许枚举。"
        }
    }

    if ($Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $requiredNode = Get-JsonProperty -Element $Schema -Name 'required'
        if ($null -ne $requiredNode) {
            foreach ($requiredName in $requiredNode.EnumerateArray()) {
                $requiredValue = [System.Text.Json.JsonElement]::new()
                if (-not $Instance.TryGetProperty($requiredName.GetString(), [ref] $requiredValue)) {
                    New-GovernanceFailure -Code 'SCHEMA_REQUIRED' -Message "$Path 缺少 $($requiredName.GetString())。"
                }
            }
        }

        $propertySchemas = Get-JsonProperty -Element $Schema -Name 'properties'
        $additionalNode = Get-JsonProperty -Element $Schema -Name 'additionalProperties'
        $allowAdditional = $true
        if ($null -ne $additionalNode -and
            $additionalNode.ValueKind -eq [System.Text.Json.JsonValueKind]::False) {
            $allowAdditional = $false
        }

        foreach ($property in $Instance.EnumerateObject()) {
            $propertySchema = [System.Text.Json.JsonElement]::new()
            $known = $null -ne $propertySchemas -and
                $propertySchemas.TryGetProperty($property.Name, [ref] $propertySchema)
            if ($known) {
                Assert-JsonSchema `
                    -Instance $property.Value `
                    -Schema $propertySchema `
                    -SchemaRoot $SchemaRoot `
                    -Path "$Path/$($property.Name)"
            }
            elseif (-not $allowAdditional) {
                New-GovernanceFailure -Code 'SCHEMA_ADDITIONAL_PROPERTY' -Message "$Path 禁止字段 $($property.Name)。"
            }
        }
    }

    if ($Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        $count = $Instance.GetArrayLength()
        $minimumItemsNode = Get-JsonProperty -Element $Schema -Name 'minItems'
        if ($null -ne $minimumItemsNode -and $count -lt $minimumItemsNode.GetInt32()) {
            New-GovernanceFailure -Code 'SCHEMA_MIN_ITEMS' -Message "$Path 的元素数量不足。"
        }
        $maximumItemsNode = Get-JsonProperty -Element $Schema -Name 'maxItems'
        if ($null -ne $maximumItemsNode -and $count -gt $maximumItemsNode.GetInt32()) {
            New-GovernanceFailure -Code 'SCHEMA_MAX_ITEMS' -Message "$Path 的元素数量超限。"
        }

        $uniqueItemsNode = Get-JsonProperty -Element $Schema -Name 'uniqueItems'
        if ($null -ne $uniqueItemsNode -and
            $uniqueItemsNode.ValueKind -eq [System.Text.Json.JsonValueKind]::True) {
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($item in $Instance.EnumerateArray()) {
                if (-not $seen.Add([GovernanceJson]::CanonicalSha256($item))) {
                    New-GovernanceFailure -Code 'SCHEMA_UNIQUE_ITEMS' -Message "$Path 包含重复元素。"
                }
            }
        }

        $itemSchema = Get-JsonProperty -Element $Schema -Name 'items'
        if ($null -ne $itemSchema) {
            $index = 0
            foreach ($item in $Instance.EnumerateArray()) {
                Assert-JsonSchema `
                    -Instance $item `
                    -Schema $itemSchema `
                    -SchemaRoot $SchemaRoot `
                    -Path "$Path/$index"
                $index++
            }
        }
    }

    if ($Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
        $value = $Instance.GetString()
        $minimumLengthNode = Get-JsonProperty -Element $Schema -Name 'minLength'
        if ($null -ne $minimumLengthNode -and $value.Length -lt $minimumLengthNode.GetInt32()) {
            New-GovernanceFailure -Code 'SCHEMA_MIN_LENGTH' -Message "$Path 的字符串过短。"
        }
        $maximumLengthNode = Get-JsonProperty -Element $Schema -Name 'maxLength'
        if ($null -ne $maximumLengthNode -and $value.Length -gt $maximumLengthNode.GetInt32()) {
            New-GovernanceFailure -Code 'SCHEMA_MAX_LENGTH' -Message "$Path 的字符串过长。"
        }
        $patternNode = Get-JsonProperty -Element $Schema -Name 'pattern'
        if ($null -ne $patternNode) {
            $regex = [regex]::new(
                $patternNode.GetString(),
                [Text.RegularExpressions.RegexOptions]::CultureInvariant,
                [TimeSpan]::FromSeconds(2))
            if (-not $regex.IsMatch($value)) {
                New-GovernanceFailure -Code 'SCHEMA_PATTERN' -Message "$Path 不匹配固定模式。"
            }
        }
    }

    if ($Instance.ValueKind -eq [System.Text.Json.JsonValueKind]::Number) {
        $value = $Instance.GetInt64()
        $minimumNode = Get-JsonProperty -Element $Schema -Name 'minimum'
        if ($null -ne $minimumNode -and $value -lt $minimumNode.GetInt64()) {
            New-GovernanceFailure -Code 'SCHEMA_MINIMUM' -Message "$Path 小于允许下限。"
        }
        $maximumNode = Get-JsonProperty -Element $Schema -Name 'maximum'
        if ($null -ne $maximumNode -and $value -gt $maximumNode.GetInt64()) {
            New-GovernanceFailure -Code 'SCHEMA_MAXIMUM' -Message "$Path 大于允许上限。"
        }
    }
}

function Assert-SchemaDocument {
    param(
        [Parameter(Mandatory)]
        [GovernanceJsonFile] $File,

        [Parameter(Mandatory)]
        [string] $ExpectedId
    )

    $root = $File.Document.RootElement
    if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or
        (Get-RequiredJsonProperty -Element $root -Name '$schema' -Path '$').GetString() -ne
            'https://json-schema.org/draft/2020-12/schema' -or
        (Get-RequiredJsonProperty -Element $root -Name '$id' -Path '$').GetString() -ne $ExpectedId -or
        (Get-RequiredJsonProperty -Element $root -Name 'type' -Path '$').GetString() -ne 'object') {
        New-GovernanceFailure -Code 'SCHEMA_DOCUMENT' -Message "Schema 根身份无效：$($File.Path)"
    }

    $additional = Get-RequiredJsonProperty -Element $root -Name 'additionalProperties' -Path '$'
    if ($additional.ValueKind -ne [System.Text.Json.JsonValueKind]::False) {
        New-GovernanceFailure -Code 'SCHEMA_OPEN_ROOT' -Message "Schema 根必须 additionalProperties=false：$($File.Path)"
    }
}

function Assert-AllSchemaReferences {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $Element,

        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $SchemaRoot
    )

    switch ($Element.ValueKind) {
        ([System.Text.Json.JsonValueKind]::Object) {
            foreach ($property in $Element.EnumerateObject()) {
                if ($property.Name -eq '$ref') {
                    $null = Resolve-LocalSchemaReference `
                        -SchemaRoot $SchemaRoot `
                        -Reference $property.Value.GetString()
                }
                else {
                    Assert-AllSchemaReferences -Element $property.Value -SchemaRoot $SchemaRoot
                }
            }
        }
        ([System.Text.Json.JsonValueKind]::Array) {
            foreach ($item in $Element.EnumerateArray()) {
                Assert-AllSchemaReferences -Element $item -SchemaRoot $SchemaRoot
            }
        }
    }
}

function Assert-SensitiveDataFree {
    param(
        [Parameter(Mandatory)]
        [GovernanceJsonFile] $File
    )

    $finding = [GovernanceJson]::FindSensitiveData($File.Document.RootElement)
    if (-not [string]::IsNullOrWhiteSpace($finding)) {
        throw "$finding in $($File.Path)"
    }
}

function Assert-SortedUniqueStrings {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $Array,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $previous = $null
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Array.EnumerateArray()) {
        if ($item.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
            New-GovernanceFailure -Code 'NORMALIZATION_STRING_ARRAY' -Message "$Path 只能包含字符串。"
        }
        $value = $item.GetString()
        if (-not $seen.Add($value)) {
            New-GovernanceFailure -Code 'NORMALIZATION_DUPLICATE' -Message "$Path 包含重复值 $value。"
        }
        if ($null -ne $previous -and
            [string]::CompareOrdinal($previous, $value) -ge 0) {
            New-GovernanceFailure -Code 'NORMALIZATION_ORDER' -Message "$Path 未按字节序排序。"
        }
        $previous = $value
    }
}

function Assert-SortedUniqueObjectKeys {
    param(
        [Parameter(Mandatory)]
        [System.Text.Json.JsonElement] $Array,

        [Parameter(Mandatory)]
        [string[]] $KeyNames,

        [Parameter(Mandatory)]
        [string] $Path
    )

    $previous = $null
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Array.EnumerateArray()) {
        $parts = foreach ($keyName in $KeyNames) {
            $value = Get-RequiredJsonProperty -Element $item -Name $keyName -Path $Path
            if ($value.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                $value.GetString()
            }
            else {
                $value.GetRawText()
            }
        }
        $key = $parts -join "`u{001f}"
        if (-not $seen.Add($key)) {
            New-GovernanceFailure -Code 'NORMALIZATION_DUPLICATE' -Message "$Path 包含重复身份 $key。"
        }
        if ($null -ne $previous -and
            [string]::CompareOrdinal($previous, $key) -ge 0) {
            New-GovernanceFailure -Code 'NORMALIZATION_ORDER' -Message "$Path 未按身份排序。"
        }
        $previous = $key
    }
}

function Assert-ContractCapabilities {
    param(
        [Parameter(Mandatory)]
        [GovernanceJsonFile] $File
    )

    $root = $File.Document.RootElement
    if ((Get-RequiredJsonProperty -Element $root -Name 'contractVersion' -Path '$').GetString() -ne '1.1.0') {
        New-GovernanceFailure -Code 'CONTRACT_VERSION' -Message '当前实现只允许 contractVersion=1.1.0。'
    }

    $allCapabilityIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($arrayName in @('baseCapabilities', 'extensionCapabilities', 'conditionalCapabilities')) {
        $array = Get-RequiredJsonProperty -Element $root -Name $arrayName -Path '$'
        Assert-SortedUniqueObjectKeys -Array $array -KeyNames @('capabilityId') -Path "`$/$arrayName"
        foreach ($item in $array.EnumerateArray()) {
            $capabilityId = (Get-RequiredJsonProperty -Element $item -Name 'capabilityId' -Path "`$/$arrayName").GetString()
            if (-not $allCapabilityIds.Add($capabilityId)) {
                New-GovernanceFailure -Code 'CONTRACT_CAPABILITY_OVERLAP' -Message "能力身份重复：$capabilityId"
            }
            foreach ($nestedName in @('requiredEvidence', 'resourceTypes')) {
                $nested = Get-JsonProperty -Element $item -Name $nestedName
                if ($null -ne $nested) {
                    Assert-SortedUniqueStrings -Array $nested -Path "`$/$arrayName/$capabilityId/$nestedName"
                }
            }
        }
    }

    $forbidden = Get-RequiredJsonProperty -Element $root -Name 'permanentlyForbiddenCapabilities' -Path '$'
    Assert-SortedUniqueStrings -Array $forbidden -Path '$/permanentlyForbiddenCapabilities'
    foreach ($item in $forbidden.EnumerateArray()) {
        if (-not $allCapabilityIds.Add($item.GetString())) {
            New-GovernanceFailure -Code 'CONTRACT_CAPABILITY_OVERLAP' -Message "禁止能力与授权能力重复：$($item.GetString())"
        }
    }

    $cspDebt = Get-RequiredJsonProperty -Element $root -Name 'globalCspDebt' -Path '$'
    Assert-SortedUniqueObjectKeys -Array $cspDebt -KeyNames @('capabilityId') -Path '$/globalCspDebt'
    foreach ($item in $cspDebt.EnumerateArray()) {
        $capabilityId = (Get-RequiredJsonProperty -Element $item -Name 'capabilityId' -Path '$/globalCspDebt').GetString()
        if (-not $allCapabilityIds.Add($capabilityId)) {
            New-GovernanceFailure -Code 'CONTRACT_CAPABILITY_OVERLAP' -Message "CSP 债务能力身份重复：$capabilityId"
        }
    }

    $methodCeilings = Get-RequiredJsonProperty -Element $root -Name 'methodCeilings' -Path '$'
    Assert-SortedUniqueStrings `
        -Array (Get-RequiredJsonProperty -Element $methodCeilings -Name 'passiveResource' -Path '$/methodCeilings') `
        -Path '$/methodCeilings/passiveResource'
    Assert-SortedUniqueStrings `
        -Array (Get-RequiredJsonProperty -Element $methodCeilings -Name 'reviewedApi' -Path '$/methodCeilings') `
        -Path '$/methodCeilings/reviewedApi'
}

function Get-SemVerParts {
    param([Parameter(Mandatory)][string] $Version)

    $match = [regex]::Match(
        $Version,
        '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant,
        [TimeSpan]::FromSeconds(1))
    if (-not $match.Success) {
        New-GovernanceFailure -Code 'SEMVER_PARSE' -Message "无效 SemVer：$Version"
    }
    return [pscustomobject]@{
        Major = [uint64] $match.Groups['major'].Value
        Minor = [uint64] $match.Groups['minor'].Value
        Patch = [uint64] $match.Groups['patch'].Value
        Pre = $match.Groups['pre'].Value
    }
}

function Compare-SemVer {
    param(
        [Parameter(Mandatory)][string] $Left,
        [Parameter(Mandatory)][string] $Right
    )

    $leftParts = Get-SemVerParts -Version $Left
    $rightParts = Get-SemVerParts -Version $Right
    foreach ($name in @('Major', 'Minor', 'Patch')) {
        if ($leftParts.$name -lt $rightParts.$name) { return -1 }
        if ($leftParts.$name -gt $rightParts.$name) { return 1 }
    }
    if ($leftParts.Pre.Length -eq 0 -and $rightParts.Pre.Length -eq 0) { return 0 }
    if ($leftParts.Pre.Length -eq 0) { return 1 }
    if ($rightParts.Pre.Length -eq 0) { return -1 }
    return [string]::CompareOrdinal($leftParts.Pre, $rightParts.Pre)
}

function Assert-Registry {
    param(
        [Parameter(Mandatory)]
        [GovernanceJsonFile] $File,

        [Parameter(Mandatory)]
        [GovernanceJsonFile] $Capabilities,

        [switch] $AllowCandidateRules
    )

    $root = $File.Document.RootElement
    $contractVersion = (Get-RequiredJsonProperty -Element $root -Name 'contractVersion' -Path '$').GetString()
    $capabilityRoot = $Capabilities.Document.RootElement
    if ($contractVersion -ne
        (Get-RequiredJsonProperty -Element $capabilityRoot -Name 'contractVersion' -Path '$').GetString()) {
        New-GovernanceFailure -Code 'REGISTRY_CONTRACT_IDENTITY' -Message '注册表与能力契约版本不一致。'
    }

    $activeRules = Get-RequiredJsonProperty -Element $root -Name 'activeRules' -Path '$'
    $tombstones = Get-RequiredJsonProperty -Element $root -Name 'tombstones' -Path '$'
    Assert-SortedUniqueObjectKeys -Array $activeRules -KeyNames @('ruleId', 'ruleVersion') -Path '$/activeRules'
    Assert-SortedUniqueObjectKeys -Array $tombstones -KeyNames @('ruleId', 'revokedFromRegistryVersion') -Path '$/tombstones'

    $allowedExtensionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $extensionCapabilities = Get-RequiredJsonProperty -Element $capabilityRoot -Name 'extensionCapabilities' -Path '$'
    foreach ($item in $extensionCapabilities.EnumerateArray()) {
        $null = $allowedExtensionIds.Add(
            (Get-RequiredJsonProperty -Element $item -Name 'capabilityId' -Path '$/extensionCapabilities').GetString())
    }

    $ruleIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $rules = @($activeRules.EnumerateArray())
    foreach ($rule in $rules) {
        $ruleId = (Get-RequiredJsonProperty -Element $rule -Name 'ruleId' -Path '$/activeRules').GetString()
        if (-not $ruleIds.Add($ruleId)) {
            New-GovernanceFailure -Code 'REGISTRY_RULE_ID_REUSE' -Message "活动 ruleId 重复：$ruleId"
        }
        if ((Get-RequiredJsonProperty -Element $rule -Name 'contractVersion' -Path '$/activeRules').GetString() -ne $contractVersion) {
            New-GovernanceFailure -Code 'REGISTRY_RULE_CONTRACT' -Message "规则契约版本不一致：$ruleId"
        }

        $match = Get-RequiredJsonProperty -Element $rule -Name 'match' -Path '$/activeRules'
        $minimum = (Get-RequiredJsonProperty -Element $match -Name 'minimumUiVersion' -Path '$/activeRules/match').GetString()
        $maximum = (Get-RequiredJsonProperty -Element $match -Name 'maximumUiVersion' -Path '$/activeRules/match').GetString()
        if ((Compare-SemVer -Left $minimum -Right $maximum) -gt 0) {
            New-GovernanceFailure -Code 'REGISTRY_VERSION_RANGE' -Message "规则版本范围反转：$ruleId"
        }
        $sourceRevision = Get-JsonProperty -Element $match -Name 'sourceRev'
        if (-not $AllowCandidateRules -and
            ($minimum -cne $maximum -or $null -eq $sourceRevision)) {
            New-GovernanceFailure `
                -Code 'PRODUCTION_RULE_EVIDENCE_REQUIRED' `
                -Message "生产活动规则必须绑定精确版本和不可变 sourceRev：$ruleId"
        }

        $grants = Get-RequiredJsonProperty -Element $rule -Name 'grants' -Path '$/activeRules'
        foreach ($grant in $grants.EnumerateArray()) {
            $capabilityId = (Get-RequiredJsonProperty -Element $grant -Name 'capabilityId' -Path '$/activeRules/grants').GetString()
            if (-not $allowedExtensionIds.Contains($capabilityId)) {
                New-GovernanceFailure -Code 'REGISTRY_OUTSIDE_CEILING' -Message "规则请求未授权扩展能力：$capabilityId"
            }
            if ($capabilityId -ceq 'reviewed-inline-script-sha256') {
                $digest = (Get-RequiredJsonProperty `
                    -Element $grant `
                    -Name 'scriptSha256' `
                    -Path '$/activeRules/grants').GetString()
                if ($digest -cnotmatch '^[A-Za-z0-9+/]{43}=$') {
                    New-GovernanceFailure `
                        -Code 'REGISTRY_SCRIPT_DIGEST' `
                        -Message "内联脚本 SHA-256 不是规范 Base64：$ruleId"
                }
                continue
            }
            Assert-SortedUniqueStrings `
                -Array (Get-RequiredJsonProperty -Element $grant -Name 'methods' -Path '$/activeRules/grants') `
                -Path '$/activeRules/grants/methods'
            Assert-SortedUniqueStrings `
                -Array (Get-RequiredJsonProperty -Element $grant -Name 'resourceTypes' -Path '$/activeRules/grants') `
                -Path '$/activeRules/grants/resourceTypes'
            $pathRule = Get-RequiredJsonProperty -Element $grant -Name 'path' -Path '$/activeRules/grants'
            $pathValue = (Get-RequiredJsonProperty -Element $pathRule -Name 'value' -Path '$/activeRules/grants/path').GetString()
            if ($pathValue.Contains('//', [StringComparison]::Ordinal) -or
                $pathValue -match '(?i)%2f|%5c|(?:^|/)\.\.?(/|$)') {
                New-GovernanceFailure -Code 'REGISTRY_PATH_NORMALIZATION' -Message "规则路径可产生双重解释：$pathValue"
            }
            if ((Get-RequiredJsonProperty -Element $pathRule -Name 'kind' -Path '$/activeRules/grants/path').GetString() -eq 'segment-prefix' -and
                -not $pathValue.EndsWith('/', [StringComparison]::Ordinal)) {
                New-GovernanceFailure -Code 'REGISTRY_PATH_BOUNDARY' -Message "段前缀必须以 / 结束：$pathValue"
            }
        }
    }

    for ($leftIndex = 0; $leftIndex -lt $rules.Count; $leftIndex++) {
        $leftMatch = Get-RequiredJsonProperty -Element $rules[$leftIndex] -Name 'match' -Path '$/activeRules'
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $rules.Count; $rightIndex++) {
            $rightMatch = Get-RequiredJsonProperty -Element $rules[$rightIndex] -Name 'match' -Path '$/activeRules'
            $sameIdentity = $true
            foreach ($name in @('uiId', 'adapterKey')) {
                if ((Get-RequiredJsonProperty -Element $leftMatch -Name $name -Path '$/activeRules/match').GetString() -ne
                    (Get-RequiredJsonProperty -Element $rightMatch -Name $name -Path '$/activeRules/match').GetString()) {
                    $sameIdentity = $false
                }
            }
            $leftRevision = Get-JsonProperty -Element $leftMatch -Name 'sourceRev'
            $rightRevision = Get-JsonProperty -Element $rightMatch -Name 'sourceRev'
            if (($null -eq $leftRevision) -ne ($null -eq $rightRevision) -or
                ($null -ne $leftRevision -and $leftRevision.GetString() -ne $rightRevision.GetString())) {
                $sameIdentity = $false
            }
            if ($sameIdentity) {
                $leftMinimum = (Get-RequiredJsonProperty -Element $leftMatch -Name 'minimumUiVersion' -Path '$').GetString()
                $leftMaximum = (Get-RequiredJsonProperty -Element $leftMatch -Name 'maximumUiVersion' -Path '$').GetString()
                $rightMinimum = (Get-RequiredJsonProperty -Element $rightMatch -Name 'minimumUiVersion' -Path '$').GetString()
                $rightMaximum = (Get-RequiredJsonProperty -Element $rightMatch -Name 'maximumUiVersion' -Path '$').GetString()
                if ((Compare-SemVer -Left $leftMinimum -Right $rightMaximum) -le 0 -and
                    (Compare-SemVer -Left $rightMinimum -Right $leftMaximum) -le 0) {
                    New-GovernanceFailure -Code 'REGISTRY_OVERLAPPING_MATCH' -Message '活动规则匹配域重叠。'
                }
            }
        }
    }

    foreach ($tombstone in $tombstones.EnumerateArray()) {
        $ruleId = (Get-RequiredJsonProperty -Element $tombstone -Name 'ruleId' -Path '$/tombstones').GetString()
        if ($ruleIds.Contains($ruleId)) {
            New-GovernanceFailure -Code 'REGISTRY_TOMBSTONE_PRECEDENCE' -Message "活动规则与墓碑冲突：$ruleId"
        }
    }
}

function Assert-ProviderIntake {
    param([Parameter(Mandatory)][GovernanceJsonFile] $File)

    $history = Get-RequiredJsonProperty -Element $File.Document.RootElement -Name 'applicationStateHistory' -Path '$'
    $entries = @($history.EnumerateArray())
    if ((Get-RequiredJsonProperty -Element $entries[0] -Name 'state' -Path '$/applicationStateHistory/0').GetString() -ne 'Received') {
        New-GovernanceFailure -Code 'STATE_INITIAL' -Message '申请状态必须从 Received 开始。'
    }
    $allowed = @{
        Received = @('AwaitingProvider', 'InReview', 'ClosedRejected', 'ClosedProviderWithdrawn')
        AwaitingProvider = @('InReview', 'ClosedRejected', 'ClosedProviderWithdrawn')
        InReview = @('AwaitingProvider', 'RuleCandidate', 'ClosedRejected', 'ClosedProviderWithdrawn', 'ClosedSuperseded')
        RuleCandidate = @('Verification', 'ClosedRejected', 'ClosedSuperseded')
        Verification = @('AwaitingProvider', 'AwaitingSignedRelease', 'ClosedRejected', 'ClosedSuperseded')
        AwaitingSignedRelease = @('Verification', 'ClosedSupported', 'ClosedSuperseded')
        ClosedSupported = @()
        ClosedRejected = @()
        ClosedProviderWithdrawn = @()
        ClosedSuperseded = @()
    }
    for ($index = 1; $index -lt $entries.Count; $index++) {
        $previous = (Get-RequiredJsonProperty -Element $entries[$index - 1] -Name 'state' -Path '$/applicationStateHistory').GetString()
        $current = (Get-RequiredJsonProperty -Element $entries[$index] -Name 'state' -Path '$/applicationStateHistory').GetString()
        if ($current -notin $allowed[$previous]) {
            New-GovernanceFailure -Code 'STATE_TRANSITION' -Message "非法申请状态迁移：$previous -> $current"
        }
        if ($current.StartsWith('Closed', [StringComparison]::Ordinal) -and
            $current -ne 'ClosedSupported' -and
            $null -eq (Get-JsonProperty -Element $entries[$index] -Name 'reasonCode')) {
            New-GovernanceFailure -Code 'STATE_REASON_REQUIRED' -Message "$current 必须有稳定原因码。"
        }
    }

    Assert-SortedUniqueObjectKeys `
        -Array (Get-RequiredJsonProperty -Element $File.Document.RootElement -Name 'components' -Path '$') `
        -KeyNames @('uiId', 'uiVersion', 'sourceRev') `
        -Path '$/components'
    Assert-SortedUniqueObjectKeys `
        -Array (Get-RequiredJsonProperty -Element $File.Document.RootElement -Name 'capabilitySlices' -Path '$') `
        -KeyNames @('adapterKey') `
        -Path '$/capabilitySlices'
    Assert-SortedUniqueStrings `
        -Array (Get-RequiredJsonProperty -Element $File.Document.RootElement -Name 'conditionalCapabilityRequests' -Path '$') `
        -Path '$/conditionalCapabilityRequests'
}

function Assert-ReviewDecision {
    param([Parameter(Mandatory)][GovernanceJsonFile] $File)

    $root = $File.Document.RootElement
    $decision = (Get-RequiredJsonProperty -Element $root -Name 'decision' -Path '$').GetString()
    $reasonCodes = Get-RequiredJsonProperty -Element $root -Name 'reasonCodes' -Path '$'
    Assert-SortedUniqueStrings -Array $reasonCodes -Path '$/reasonCodes'
    Assert-SortedUniqueStrings `
        -Array (Get-RequiredJsonProperty -Element $root -Name 'requiredGates' -Path '$') `
        -Path '$/requiredGates'
    if ($decision -in @('Rejected', 'NeedsProvider', 'Superseded') -and
        $reasonCodes.GetArrayLength() -eq 0) {
        New-GovernanceFailure -Code 'DECISION_REASON_REQUIRED' -Message "$decision 必须有稳定原因码。"
    }
    if ($decision -eq 'ApprovedForVerification') {
        if ((Get-RequiredJsonProperty -Element $root -Name 'globalCeilingResult' -Path '$').GetString() -ne 'within-ceiling') {
            New-GovernanceFailure -Code 'DECISION_CEILING' -Message '批准验证必须处于全局能力上限内。'
        }
        foreach ($name in @('windowsCompatibilityMaintainer', 'securityMaintainer')) {
            $maintainer = Get-RequiredJsonProperty -Element $root -Name $name -Path '$'
            if ((Get-RequiredJsonProperty -Element $maintainer -Name 'result' -Path "`$/$name").GetString() -ne 'approve') {
                New-GovernanceFailure -Code 'DECISION_APPROVAL' -Message '批准验证需要两个独立维护者批准。'
            }
        }
    }
}

function Assert-SupportRecord {
    param([Parameter(Mandatory)][GovernanceJsonFile] $File)

    $root = $File.Document.RootElement
    $supportState = (Get-RequiredJsonProperty -Element $root -Name 'supportState' -Path '$').GetString()
    $rules = Get-RequiredJsonProperty -Element $root -Name 'rules' -Path '$'
    if ($rules.GetArrayLength() -gt 0) {
        Assert-SortedUniqueObjectKeys -Array $rules -KeyNames @('ruleId', 'ruleVersion') -Path '$/rules'
    }
    if ($supportState -eq 'Supported') {
        if ($rules.GetArrayLength() -eq 0 -or
            $null -eq (Get-JsonProperty -Element $root -Name 'reviewDecisionId')) {
            New-GovernanceFailure -Code 'SUPPORT_IDENTITY_REQUIRED' -Message 'Supported 必须绑定规则和审核决定。'
        }
        $launcher = Get-RequiredJsonProperty -Element $root -Name 'launcher' -Path '$'
        if ((Get-RequiredJsonProperty -Element $launcher -Name 'signatureStatus' -Path '$/launcher').GetString() -ne 'valid-trusted-timestamp') {
            New-GovernanceFailure -Code 'SUPPORT_SIGNED_RELEASE_REQUIRED' -Message 'Supported 必须绑定可信签名版本。'
        }
        $runtime = Get-RequiredJsonProperty -Element $root -Name 'runtimeEvidence' -Path '$'
        foreach ($name in @('minimumRuntime', 'evergreenRuntime', 'sameEndpointEdge')) {
            $result = Get-RequiredJsonProperty -Element $runtime -Name $name -Path '$/runtimeEvidence'
            if ((Get-RequiredJsonProperty -Element $result -Name 'result' -Path "`$/runtimeEvidence/$name").GetString() -ne 'PASS') {
                New-GovernanceFailure -Code 'SUPPORT_GATE_REQUIRED' -Message 'Supported 缺少双 Runtime 或 Edge PASS。'
            }
        }
    }
}

function Assert-ImpactMap {
    param([Parameter(Mandatory)][GovernanceJsonFile] $File)

    $root = $File.Document.RootElement
    $unknown = Get-RequiredJsonProperty -Element $root -Name 'unknownInput' -Path '$'
    $unknownTags = @((Get-RequiredJsonProperty -Element $unknown -Name 'triggerTags' -Path '$/unknownInput').EnumerateArray() |
        ForEach-Object { $_.GetString() })
    $unknownRs = @((Get-RequiredJsonProperty -Element $unknown -Name 'requiredRs' -Path '$/unknownInput').EnumerateArray() |
        ForEach-Object { $_.GetString() })
    $expectedTags = 1..8 | ForEach-Object { 'VFY-{0:D2}' -f $_ }
    $expectedRs = 1..15 | ForEach-Object { 'RS-{0:D2}' -f $_ }
    if (($unknownTags -join '|') -cne ($expectedTags -join '|') -or
        ($unknownRs -join '|') -cne ($expectedRs -join '|')) {
        New-GovernanceFailure -Code 'IMPACT_FAIL_CLOSED' -Message '未知输入必须触发全部 VFY 和 RS。'
    }

    Assert-SortedUniqueStrings `
        -Array (Get-RequiredJsonProperty -Element $root -Name 'identityInputs' -Path '$') `
        -Path '$/identityInputs'
    $mappings = Get-RequiredJsonProperty -Element $root -Name 'mappings' -Path '$'
    Assert-SortedUniqueObjectKeys -Array $mappings -KeyNames @('mappingId') -Path '$/mappings'
    foreach ($mapping in $mappings.EnumerateArray()) {
        $mappingId = (Get-RequiredJsonProperty -Element $mapping -Name 'mappingId' -Path '$/mappings').GetString()
        foreach ($arrayName in @('pathPatterns', 'triggerTags', 'requiredRs', 'identityInputs')) {
            Assert-SortedUniqueStrings `
                -Array (Get-RequiredJsonProperty -Element $mapping -Name $arrayName -Path '$/mappings') `
                -Path "`$/mappings/$mappingId/$arrayName"
        }
    }
}

function Assert-ReleaseEvidence {
    param([Parameter(Mandatory)][GovernanceJsonFile] $File)

    $root = $File.Document.RootElement
    $installer = Get-RequiredJsonProperty -Element $root -Name 'installer' -Path '$'
    $confirmation = Get-RequiredJsonProperty -Element $root -Name 'humanConfirmation' -Path '$'
    if ((Get-RequiredJsonProperty -Element $confirmation -Name 'installerSha256' -Path '$/humanConfirmation').GetString() -ne
        (Get-RequiredJsonProperty -Element $installer -Name 'sha256' -Path '$/installer').GetString()) {
        New-GovernanceFailure -Code 'EVIDENCE_INSTALLER_IDENTITY' -Message '人工确认与安装包哈希不一致。'
    }
}

function Invoke-DomainValidation {
    param(
        [Parameter(Mandatory)][string] $Kind,
        [Parameter(Mandatory)][GovernanceJsonFile] $File,
        [GovernanceJsonFile] $Capabilities,
        [switch] $AllowCandidateRules
    )

    switch ($Kind) {
        'contract' { Assert-ContractCapabilities -File $File }
        'registry' { Assert-Registry -File $File -Capabilities $Capabilities -AllowCandidateRules:$AllowCandidateRules }
        'intake' { Assert-ProviderIntake -File $File }
        'review' { Assert-ReviewDecision -File $File }
        'support' { Assert-SupportRecord -File $File }
        'release' { Assert-ReleaseEvidence -File $File }
        'impact' { Assert-ImpactMap -File $File }
        'descriptor' {
            $components = Get-RequiredJsonProperty -Element $File.Document.RootElement -Name 'components' -Path '$'
            Assert-SortedUniqueObjectKeys -Array $components -KeyNames @('uiId', 'uiVersion', 'sourceRev') -Path '$/components'
            foreach ($component in $components.EnumerateArray()) {
                Assert-SortedUniqueStrings `
                    -Array (Get-RequiredJsonProperty -Element $component -Name 'adapterKeys' -Path '$/components') `
                    -Path '$/components/adapterKeys'
            }
        }
        default { New-GovernanceFailure -Code 'UNKNOWN_DOCUMENT_KIND' -Message "未知机器文档类型：$Kind" }
    }
}

function Get-RelativeRepositoryPath {
    param([Parameter(Mandatory)][string] $Path)
    return [IO.Path]::GetRelativePath($RepositoryRoot, $Path).Replace('\', '/')
}

function Get-RejectionCode {
    param([Parameter(Mandatory)][Management.Automation.ErrorRecord] $ErrorRecord)

    $text = $ErrorRecord.Exception.Message
    $match = [regex]::Match($text, '\[(?<code>[A-Z0-9_]+)\]')
    if (-not $match.Success -and $null -ne $ErrorRecord.Exception.InnerException) {
        $match = [regex]::Match($ErrorRecord.Exception.InnerException.Message, '\[(?<code>[A-Z0-9_]+)\]')
    }
    if (-not $match.Success) {
        return 'UNCLASSIFIED'
    }
    return $match.Groups['code'].Value
}

$schemaDefinitions = [ordered]@{
    'schemas/webui-compatibility-descriptor.schema.json' = [ordered]@{
        Id = 'https://schemas.dshwindowslauncher.invalid/webui-compatibility-descriptor.schema.json'
        Kind = 'descriptor'
    }
    'schemas/webui-contract-capabilities.schema.json' = [ordered]@{
        Id = 'https://schemas.dshwindowslauncher.invalid/webui-contract-capabilities.schema.json'
        Kind = 'contract'
    }
    'schemas/webui-adapter-registry.schema.json' = [ordered]@{
        Id = 'https://schemas.dshwindowslauncher.invalid/webui-adapter-registry.schema.json'
        Kind = 'registry'
    }
    'schemas/webui-provider-intake.schema.json' = [ordered]@{
        Id = 'https://schemas.dshwindowslauncher.invalid/webui-provider-intake.schema.json'
        Kind = 'intake'
    }
    'schemas/webui-review-decision.schema.json' = [ordered]@{
        Id = 'https://schemas.dshwindowslauncher.invalid/webui-review-decision.schema.json'
        Kind = 'review'
    }
    'schemas/webui-support-record.schema.json' = [ordered]@{
        Id = 'https://schemas.dshwindowslauncher.invalid/webui-support-record.schema.json'
        Kind = 'support'
    }
    'schemas/webui-release-evidence.schema.json' = [ordered]@{
        Id = 'https://schemas.dshwindowslauncher.invalid/webui-release-evidence.schema.json'
        Kind = 'release'
    }
    'schemas/verification-impact-map.schema.json' = [ordered]@{
        Id = 'https://schemas.dshwindowslauncher.invalid/verification-impact-map.schema.json'
        Kind = 'impact'
    }
}

$productionDefinitions = [ordered]@{
    'compatibility/contracts/webui-contract-capabilities.json' = 'contract'
    'compatibility/registry/webui-adapter-registry.json' = 'registry'
    'eng/verification-impact-map.json' = 'impact'
}

$exampleDefinitions = [ordered]@{
    'compatibility/providers/examples/positive/descriptor-basic.json' = 'descriptor'
    'compatibility/providers/examples/positive/provider-intake-received.json' = 'intake'
    'compatibility/providers/examples/positive/review-needs-provider.json' = 'review'
    'compatibility/providers/examples/redacted/support-no-formal.json' = 'support'
}

$negativeDefinitions = [ordered]@{
    'compatibility/providers/examples/negative/descriptor-authorization-field.json' = [ordered]@{
        Kind = 'descriptor'
        ExpectedCode = 'SCHEMA_ADDITIONAL_PROPERTY'
    }
    'compatibility/providers/examples/negative/descriptor-duplicate-key.json' = [ordered]@{
        Kind = 'descriptor'
        ExpectedCode = 'STRICT_DUPLICATE_KEY'
    }
    'compatibility/providers/examples/negative/provider-intake-invalid-transition.json' = [ordered]@{
        Kind = 'intake'
        ExpectedCode = 'STATE_TRANSITION'
    }
    'compatibility/providers/examples/negative/provider-intake-sensitive.json' = [ordered]@{
        Kind = 'intake'
        ExpectedCode = 'SENSITIVE_VALUE'
    }
    'compatibility/providers/examples/negative/registry-overlapping-rules.json' = [ordered]@{
        Kind = 'registry'
        ExpectedCode = 'REGISTRY_OVERLAPPING_MATCH'
    }
}

$openFiles = [Collections.Generic.List[GovernanceJsonFile]]::new()
try {
    $expectedMachinePaths = @(
        @($schemaDefinitions.Keys) +
        @($productionDefinitions.Keys) +
        @($exampleDefinitions.Keys) +
        @($negativeDefinitions.Keys) |
            Sort-Object -Unique -CaseSensitive)
    $discoveredMachinePaths = @(
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'schemas') -Filter '*.json' -File -Recurse
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'compatibility/contracts') -Filter '*.json' -File -Recurse
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'compatibility/registry') -Filter '*.json' -File -Recurse
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'compatibility/providers/examples') -Filter '*.json' -File -Recurse
        Get-Item -LiteralPath (Join-Path $RepositoryRoot 'eng/verification-impact-map.json')
    ) | ForEach-Object { Get-RelativeRepositoryPath -Path $_.FullName } |
        Sort-Object -Unique -CaseSensitive
    if (($expectedMachinePaths -join '|') -cne ($discoveredMachinePaths -join '|')) {
        $difference = Compare-Object `
            -ReferenceObject $expectedMachinePaths `
            -DifferenceObject $discoveredMachinePaths
        New-GovernanceFailure -Code 'MACHINE_INVENTORY' -Message "机器文件清单未显式登记：$($difference | Out-String)"
    }

    $schemaFiles = @{}
    $inputEvidence = [Collections.Generic.List[object]]::new()
    foreach ($relativePath in $schemaDefinitions.Keys) {
        $path = Join-Path $RepositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            New-GovernanceFailure -Code 'MISSING_SCHEMA' -Message "缺少 Schema：$relativePath"
        }
        $file = [GovernanceJson]::Load($path)
        $openFiles.Add($file)
        Assert-SchemaDocument -File $file -ExpectedId $schemaDefinitions[$relativePath].Id
        Assert-AllSchemaReferences `
            -Element $file.Document.RootElement `
            -SchemaRoot $file.Document.RootElement
        Assert-SensitiveDataFree -File $file
        $schemaFiles[$schemaDefinitions[$relativePath].Kind] = $file
        $inputEvidence.Add([ordered]@{
            path = $relativePath
            kind = 'schema'
            jcsSha256 = $file.CanonicalSha256
        })
    }

    $capabilitiesFile = $null
    $registryFile = $null
    foreach ($relativePath in $productionDefinitions.Keys) {
        $path = Join-Path $RepositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            New-GovernanceFailure -Code 'MISSING_MACHINE_INPUT' -Message "缺少机器输入：$relativePath"
        }
        $kind = $productionDefinitions[$relativePath]
        $file = [GovernanceJson]::Load($path)
        $openFiles.Add($file)
        Assert-JsonSchema `
            -Instance $file.Document.RootElement `
            -Schema $schemaFiles[$kind].Document.RootElement `
            -SchemaRoot $schemaFiles[$kind].Document.RootElement `
            -Path '$'
        Assert-SensitiveDataFree -File $file
        if ($kind -eq 'contract') {
            $capabilitiesFile = $file
        }
        elseif ($kind -eq 'registry') {
            $registryFile = $file
        }
        Invoke-DomainValidation -Kind $kind -File $file -Capabilities $capabilitiesFile
        $inputEvidence.Add([ordered]@{
            path = $relativePath
            kind = $kind
            jcsSha256 = $file.CanonicalSha256
        })
    }

    foreach ($relativePath in $exampleDefinitions.Keys) {
        $path = Join-Path $RepositoryRoot $relativePath
        $kind = $exampleDefinitions[$relativePath]
        $file = [GovernanceJson]::Load($path)
        $openFiles.Add($file)
        Assert-JsonSchema `
            -Instance $file.Document.RootElement `
            -Schema $schemaFiles[$kind].Document.RootElement `
            -SchemaRoot $schemaFiles[$kind].Document.RootElement `
            -Path '$'
        Assert-SensitiveDataFree -File $file
        Invoke-DomainValidation -Kind $kind -File $file -Capabilities $capabilitiesFile
        $inputEvidence.Add([ordered]@{
            path = $relativePath
            kind = "example-$kind"
            jcsSha256 = $file.CanonicalSha256
        })
    }

    $negativeEvidence = [Collections.Generic.List[object]]::new()
    foreach ($relativePath in $negativeDefinitions.Keys) {
        $definition = $negativeDefinitions[$relativePath]
        $path = Join-Path $RepositoryRoot $relativePath
        $rejected = $false
        $code = $null
        $file = $null
        try {
            $file = [GovernanceJson]::Load($path)
            $openFiles.Add($file)
            Assert-JsonSchema `
                -Instance $file.Document.RootElement `
                -Schema $schemaFiles[$definition.Kind].Document.RootElement `
                -SchemaRoot $schemaFiles[$definition.Kind].Document.RootElement `
                -Path '$'
            Assert-SensitiveDataFree -File $file
            Invoke-DomainValidation `
                -Kind $definition.Kind `
                -File $file `
                -Capabilities $capabilitiesFile `
                -AllowCandidateRules
        }
        catch {
            $rejected = $true
            $code = Get-RejectionCode -ErrorRecord $_
        }
        if (-not $rejected) {
            New-GovernanceFailure -Code 'NEGATIVE_EXAMPLE_ACCEPTED' -Message "负例被接受：$relativePath"
        }
        if ($code -ne $definition.ExpectedCode) {
            New-GovernanceFailure -Code 'NEGATIVE_REASON_MISMATCH' -Message "负例拒绝原因错误：$relativePath；实际 $code；预期 $($definition.ExpectedCode)"
        }
        $negativeEvidence.Add([ordered]@{
            path = $relativePath
            expectedCode = $definition.ExpectedCode
            result = 'REJECTED'
        })
    }

    $orderedInputs = @($inputEvidence | Sort-Object { $_['path'] } -CaseSensitive)
    $orderedNegatives = @($negativeEvidence | Sort-Object { $_['path'] } -CaseSensitive)
    $registryEvidence = $orderedInputs |
        Where-Object { $_['path'] -eq 'compatibility/registry/webui-adapter-registry.json' } |
        Select-Object -First 1
    if ($null -eq $registryEvidence) {
        New-GovernanceFailure -Code 'REGISTRY_EVIDENCE_MISSING' -Message '缺少生产注册表摘要。'
    }
    if ($null -eq $registryFile) {
        New-GovernanceFailure -Code 'REGISTRY_INPUT_MISSING' -Message '缺少已校验的生产注册表。'
    }
    $registryRoot = $registryFile.Document.RootElement
    $productionRegistryVersion =
        (Get-RequiredJsonProperty -Element $registryRoot -Name 'registryVersion' -Path '$').GetInt64()
    $productionActiveRuleCount =
        (Get-RequiredJsonProperty -Element $registryRoot -Name 'activeRules' -Path '$').GetArrayLength()
    $productionTombstoneCount =
        (Get-RequiredJsonProperty -Element $registryRoot -Name 'tombstones' -Path '$').GetArrayLength()
    $productionContractVersion =
        (Get-RequiredJsonProperty -Element $registryRoot -Name 'contractVersion' -Path '$').GetString()
    $summary = [ordered]@{
        schemaVersion = 1
        result = 'PASS'
        normalization = 'RFC8785-JCS-UTF8-NFC-safe-integers'
        productionRegistry = [ordered]@{
            registryVersion = $productionRegistryVersion
            activeRuleCount = $productionActiveRuleCount
            tombstoneCount = $productionTombstoneCount
            jcsSha256 = [string] $registryEvidence['jcsSha256']
        }
        inputs = $orderedInputs
        negativeExamples = $orderedNegatives
    }
    $summaryJson = $summary | ConvertTo-Json -Depth 100
    $summaryBytes = [GovernanceJson]::CanonicalizeText($summaryJson)

    $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
    $summaryPath = Join-Path $OutputDirectory 'webui-governance-summary.json'
    [IO.File]::WriteAllBytes($summaryPath, $summaryBytes)
    $summarySha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($summaryBytes)).ToLowerInvariant()

    $markdownLines = @(
        '# WebUI governance summary',
        '',
        '- Result: PASS',
        "- Contract version: $productionContractVersion",
        "- Registry version: $productionRegistryVersion",
        "- Production active rules: $productionActiveRuleCount",
        "- Production tombstones: $productionTombstoneCount",
        "- Validated machine inputs: $($orderedInputs.Count)",
        "- Rejected negative examples: $($orderedNegatives.Count)",
        "- Structured summary SHA-256: $summarySha256",
        '',
        'Only exact, reviewed production compatibility rules are declared.'
    )
    $markdownPath = Join-Path $OutputDirectory 'webui-governance-summary.md'
    [IO.File]::WriteAllText(
        $markdownPath,
        ($markdownLines -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host "WebUI governance validation: PASS"
    Write-Host "Structured summary: $summaryPath"
    Write-Host "Structured summary SHA-256: $summarySha256"
}
finally {
    foreach ($file in $openFiles) {
        $file.Dispose()
    }
}
