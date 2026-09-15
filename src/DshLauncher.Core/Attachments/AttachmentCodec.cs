using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DshLauncher.Core.Attachments;

/// <summary>
/// 解码后的线协议消息。字段表与冻结顺序由 <see cref="AttachmentCodec"/> 保证；
/// <see cref="Fields"/> 就是该报文的规范化字段集合（canonical 投影的来源）。
/// </summary>
public sealed record AttachmentMessage
{
    public required WireMessageKind Kind { get; init; }

    public required string Type { get; init; }

    public required JsonObject Fields { get; init; }

    public string? SessionId => String("sessionId");

    public string? BatchId => String("batchId");

    public string? FileId => String("fileId");

    public string? TargetId => String("targetId");

    public string? ComposerScope => String("composerScope");

    public int? DocumentEpoch => Integer("documentEpoch");

    public int? ComposerEpoch => Integer("composerEpoch");

    public int? Seq => Integer("seq");

    public int? Offset => Integer("offset");

    public int? ByteLength => Integer("byteLength");

    public int? TotalBytes => Integer("totalBytes");

    public int? FileCount => Integer("fileCount");

    public int? BufferedBytes => Integer("bufferedBytes");

    public int? InFlight => Integer("inFlight");

    public int SubmittedItems => Integer("submittedItems") ?? 1;

    public string? Sha256 => String("sha256");

    public string? Name => String("name");

    public string? Mime => String("mime");

    public string? DataBase64 => String("dataBase64");

    public string? Status => String("status");

    public string? Code => String("code");

    public string? Stage => String("stage");

    public string? Reason => String("reason");

    public string? ClientBuild => String("clientBuild");

    public IReadOnlyList<string> AttachmentIds => Strings("attachmentIds");

    public IReadOnlyList<string> Features => Strings("features");

    public JsonObject? Limits => Fields.TryGetPropertyValue("limits", out var node) ? node as JsonObject : null;

    public JsonArray? Results => Fields.TryGetPropertyValue("results", out var node) ? node as JsonArray : null;

    private string? String(string name) =>
        Fields.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private int? Integer(string name) =>
        Fields.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;

    private List<string> Strings(string name)
    {
        if (!Fields.TryGetPropertyValue(name, out var node) || node is not JsonArray array) return [];
        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text)) list.Add(text);
        }

        return list;
    }
}

/// <summary>解码结果：成功时给出消息与 canonical 投影，失败时给出稳定拒绝码。</summary>
public sealed record AttachmentDecodeResult
{
    public required bool Ok { get; init; }

    public AttachmentMessage? Message { get; init; }

    public JsonObject? Canonical { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? Path { get; init; }
}

/// <summary>
/// 线协议 v1 消息 codec（C# 侧生产实现，与 TS 侧
/// <c>src/shared/wire/messages.ts</c> 逐条对齐）。
///
/// 判定顺序：malformed-json → message-too-large → v → type → 字段名扫描 → 逐字段。
/// </summary>
public static class AttachmentCodec
{
    private enum FieldKind
    {
        Version,
        Int,
        Id,
        Text,
        Sha256,
        Base64,
        Enum,
        EnumArray,
        IdArray,
        Limits,
        ResultArray,
    }

    private sealed record FieldSpec(
        string Name,
        FieldKind Kind,
        bool Required = true,
        long Min = 0,
        long Max = long.MaxValue,
        int MinChars = 0,
        int MaxChars = int.MaxValue,
        string? Pattern = null,
        IReadOnlyList<string>? Values = null,
        int MinItems = 0,
        int MaxItems = int.MaxValue);

    private static readonly Regex IdRegex = new(AttachmentProtocol.IdPattern, RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Regex = new(AttachmentProtocol.Sha256Pattern, RegexOptions.CultureInvariant);
    private static readonly Regex LeafNameRegex = new(AttachmentProtocol.LeafNamePattern, RegexOptions.CultureInvariant);
    private static readonly Regex MimeRegex = new(AttachmentProtocol.MimePattern, RegexOptions.CultureInvariant);
    private static readonly Regex BuildIdRegex = new(AttachmentProtocol.BuildIdPattern, RegexOptions.CultureInvariant);
    private static readonly Regex Base64Regex = new(AttachmentProtocol.Base64Pattern, RegexOptions.CultureInvariant);

    /// <summary>
    /// canonical 序列化选项：与 JS 的 <c>JSON.stringify</c> 逐字节对齐
    /// （只转义引号、反斜杠与控制字符，不转义非 ASCII）。
    /// </summary>
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static FieldSpec Id(string name, bool required = true) =>
        new(
            name,
            FieldKind.Id,
            required,
            MinChars: 1,
            MaxChars: AttachmentProtocol.MaxIdChars,
            Pattern: AttachmentProtocol.IdPattern);

    private static FieldSpec Epoch(string name) =>
        new(name, FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxEpoch);

    private static readonly FieldSpec[] LimitFields =
    [
        new("maxFileBytes", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxFileBytes),
        new("maxFilesPerBatch", FieldKind.Int, Min: 1, Max: AttachmentProtocol.MaxFilesPerBatch),
        new("maxBatchBytes", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxBatchBytes),
        new("maxScreenshotPixels", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxScreenshotPixels),
        new("maxStagingBytesPerTarget", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxStagingBytesPerTarget),
        new("maxConcurrentTargets", FieldKind.Int, Min: 1, Max: AttachmentProtocol.MaxConcurrentTargets),
    ];

    private static readonly FieldSpec[] ResultItemFields =
    [
        Id("fileId"),
        new("status", FieldKind.Enum, Values: AttachmentProtocol.ResultStatuses),
        new("attachmentIds", FieldKind.IdArray, Required: false, MaxItems: AttachmentProtocol.MaxAttachmentIds),
        new("code", FieldKind.Enum, Required: false, Values: AttachmentProtocol.ErrorCodes),
    ];

    /// <summary>冻结字段顺序 = 检查顺序 = canonical 键顺序。</summary>
    private static readonly Dictionary<WireMessageKind, FieldSpec[]> MessageFields = new()
    {
        [WireMessageKind.Hello] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            new("clientBuild", FieldKind.Text, MinChars: 1, MaxChars: 64, Pattern: AttachmentProtocol.BuildIdPattern),
        ],
        [WireMessageKind.Capabilities] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            new(
                "features",
                FieldKind.EnumArray,
                Values: AttachmentProtocol.WireFeatures,
                MaxItems: AttachmentProtocol.WireFeatures.Length),
            new("limits", FieldKind.Limits),
        ],
        [WireMessageKind.Context] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId"),
            Id("targetId"),
            Epoch("documentEpoch"),
            Epoch("composerEpoch"),
            Id("composerScope"),
        ],
        [WireMessageKind.BatchBegin] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId", required: false),
            Id("batchId"),
            Id("targetId"),
            Epoch("documentEpoch"),
            Epoch("composerEpoch"),
            new("fileCount", FieldKind.Int, Min: 1, Max: AttachmentProtocol.MaxFilesPerBatch),
            new("totalBytes", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxBatchBytes),
        ],
        [WireMessageKind.FileBegin] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId", required: false),
            Id("batchId"),
            Id("fileId"),
            new(
                "name",
                FieldKind.Text,
                MinChars: 1,
                MaxChars: AttachmentProtocol.MaxNameChars,
                Pattern: AttachmentProtocol.LeafNamePattern),
            new("byteLength", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxFileBytes),
            new(
                "mime",
                FieldKind.Text,
                MinChars: 1,
                MaxChars: AttachmentProtocol.MaxMimeChars,
                Pattern: AttachmentProtocol.MimePattern),
            new("sha256", FieldKind.Sha256, Required: false),
        ],
        [WireMessageKind.Chunk] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId", required: false),
            Id("batchId"),
            Id("fileId"),
            new("seq", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxSeq),
            new("offset", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxChunkOffset),
            new("byteLength", FieldKind.Int, Min: 1, Max: AttachmentProtocol.ChunkBytes),
            new(
                "dataBase64",
                FieldKind.Base64,
                MinChars: 4,
                MaxChars: AttachmentProtocol.MaxBase64Chars,
                Pattern: AttachmentProtocol.Base64Pattern),
        ],
        [WireMessageKind.Ack] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId", required: false),
            Id("batchId"),
            Id("fileId"),
            new("seq", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxSeq),
            new("offset", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxChunkOffset),
            new("byteLength", FieldKind.Int, Min: 1, Max: AttachmentProtocol.ChunkBytes),
            new("bufferedBytes", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxStagingBytesPerTarget),
            new("inFlight", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxChunksInFlight),
        ],
        [WireMessageKind.FileEnd] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId", required: false),
            Id("batchId"),
            Id("fileId"),
            new("totalBytes", FieldKind.Int, Min: 0, Max: AttachmentProtocol.MaxFileBytes),
            new("sha256", FieldKind.Sha256),
            new("submittedItems", FieldKind.Int, Required: false, Min: 1, Max: AttachmentProtocol.MaxSubmittedItems),
        ],
        [WireMessageKind.ImportResult] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId", required: false),
            Id("batchId"),
            Id("fileId"),
            new("status", FieldKind.Enum, Values: AttachmentProtocol.ResultStatuses),
            new("attachmentIds", FieldKind.IdArray, MaxItems: AttachmentProtocol.MaxAttachmentIds),
            new("code", FieldKind.Enum, Required: false, Values: AttachmentProtocol.ErrorCodes),
        ],
        [WireMessageKind.BatchEnd] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId", required: false),
            Id("batchId"),
            new("status", FieldKind.Enum, Values: AttachmentProtocol.ResultStatuses),
            new(
                "results",
                FieldKind.ResultArray,
                MinItems: 1,
                MaxItems: AttachmentProtocol.MaxFilesPerBatch),
        ],
        [WireMessageKind.Cancel] =
        [
            new("v", FieldKind.Version),
            new("type", FieldKind.Enum, Values: AttachmentProtocol.MessageTypes),
            Id("sessionId", required: false),
            Id("batchId"),
            new("reason", FieldKind.Enum, Required: false, Values: AttachmentProtocol.ErrorCodes),
            new("stage", FieldKind.Enum, Required: false, Values: AttachmentProtocol.ErrorStages),
        ],
    };

    /// <summary>解码并校验一条消息（纯函数，不读取任何外部状态）。</summary>
    public static AttachmentDecodeResult Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var byteLength = Encoding.UTF8.GetByteCount(json);
        if (byteLength > AttachmentProtocol.MaxMessageBytes)
        {
            return Fail("message-too-large", $"消息 {byteLength} 字节 > {AttachmentProtocol.MaxMessageBytes}", string.Empty);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            return Fail("malformed-json", error.Message, string.Empty);
        }

        if (root is not JsonObject raw) return Fail("invalid-field-type", "根节点必须是对象", string.Empty);

        if (!raw.TryGetPropertyValue("v", out var versionNode)) return Fail("missing-field", "缺少 v", "v");
        if (!TryReadInteger(versionNode, out var version)) return Fail("invalid-field-type", "v 必须是整数", "v");
        if (version != AttachmentProtocol.Version)
        {
            return Fail("version-mismatch", $"协议版本必须为 {AttachmentProtocol.Version}", "v");
        }

        if (!raw.TryGetPropertyValue("type", out var typeNode)) return Fail("missing-field", "缺少 type", "type");
        if (typeNode is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type))
        {
            return Fail("invalid-field-type", "type 必须是字符串", "type");
        }

        var kind = AttachmentProtocol.KindOf(type);
        if (kind is null) return Fail("unknown-message-type", $"未知消息类型：{type}", "type");

        var fields = MessageFields[kind.Value];
        var keyFailure = ScanKeys(raw, fields, string.Empty);
        if (keyFailure is not null) return keyFailure;

        var wireFields = new JsonObject();
        var canonical = new JsonObject();
        int? payloadBytes = null;
        string? payloadSha256 = null;
        foreach (var field in fields)
        {
            if (!raw.TryGetPropertyValue(field.Name, out var value))
            {
                if (field.Required) return Fail("missing-field", $"缺少 {field.Name}", field.Name);
                continue;
            }

            if (value is null) return Fail("invalid-field-type", $"{field.Name} 不能为 null", field.Name);
            var outcome = CheckField(field, value, field.Name);
            if (!outcome.Ok) return Fail(outcome.Code!, outcome.Detail!, outcome.Path!);
            if (field.Kind == FieldKind.Base64)
            {
                wireFields[field.Name] = JsonValue.Create(((JsonValue)value).GetValue<string>());
                payloadBytes = outcome.PayloadBytes;
                payloadSha256 = outcome.PayloadSha256;
                continue;
            }

            wireFields[field.Name] = outcome.Value?.DeepClone();
            canonical[field.Name] = outcome.Value?.DeepClone();
        }

        if (kind.Value == WireMessageKind.Chunk)
        {
            var declared = wireFields["byteLength"]!.GetValue<int>();
            if (payloadBytes != declared)
            {
                return Fail("size-mismatch", $"声明 {declared} 字节，载荷 {payloadBytes} 字节", "byteLength");
            }

            canonical["data"] = new JsonObject
            {
                ["bytes"] = payloadBytes,
                ["sha256"] = payloadSha256,
            };
        }

        var message = new AttachmentMessage { Kind = kind.Value, Type = type, Fields = wireFields };
        return new AttachmentDecodeResult { Ok = true, Message = message, Canonical = canonical };
    }

    /// <summary>编码回 JSON 文本（字段顺序即冻结顺序）。</summary>
    public static string Encode(AttachmentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Fields.ToJsonString();
    }

    /// <summary>canonical 投影（对已解码消息同样可用）。</summary>
    public static JsonObject ToCanonical(AttachmentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var canonical = new JsonObject();
        foreach (var pair in message.Fields)
        {
            if (pair.Key == "dataBase64") continue;
            canonical[pair.Key] = pair.Value?.DeepClone();
        }

        if (message.Kind == WireMessageKind.Chunk && message.DataBase64 is { } payload)
        {
            if (!TryDecodeBase64(payload, out var bytes))
            {
                throw new InvalidOperationException("已解码消息的 dataBase64 必须是规范 Base64");
            }

            canonical["data"] = new JsonObject
            {
                ["bytes"] = bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            };
        }

        return canonical;
    }

    /// <summary>按键名排序后序列化，供两端做结构比较（数组保持顺序）。</summary>
    public static string CanonicalJson(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteCanonical(node, builder);
        return builder.ToString();
    }

    /// <summary>结构相等：按 canonical JSON 字符串比较。</summary>
    public static bool CanonicalEquals(JsonNode? left, JsonNode? right) =>
        string.Equals(CanonicalJson(left), CanonicalJson(right), StringComparison.Ordinal);

    private static void WriteCanonical(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var pair in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(JsonValue.Create(pair.Key)!.ToJsonString(CanonicalJsonOptions));
                    builder.Append(':');
                    WriteCanonical(pair.Value, builder);
                }

                builder.Append('}');
                return;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index += 1)
                {
                    if (index > 0) builder.Append(',');
                    WriteCanonical(array[index], builder);
                }

                builder.Append(']');
                return;
            default:
                builder.Append(node.ToJsonString(CanonicalJsonOptions));
                return;
        }
    }

    private sealed record FieldOutcome(
        bool Ok,
        JsonNode? Value = null,
        string? Code = null,
        string? Detail = null,
        string? Path = null,
        int PayloadBytes = 0,
        string? PayloadSha256 = null);

    private static AttachmentDecodeResult? ScanKeys(JsonObject raw, FieldSpec[] fields, string path)
    {
        var allowed = new HashSet<string>(fields.Select(field => field.Name), StringComparer.Ordinal);
        var insensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in allowed) insensitive[name] = name;
        var prefix = path.Length == 0 ? string.Empty : path + ".";
        foreach (var key in raw.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal))
        {
            if (allowed.Contains(key)) continue;
            if (insensitive.TryGetValue(key, out var canonical))
            {
                return Fail("field-name-invalid", $"字段名大小写变体：{key}（应为 {canonical}）", prefix + key);
            }

            return Fail("unknown-field", $"字段集固定，出现未知字段：{key}", prefix + key);
        }

        return null;
    }

    private static FieldOutcome CheckField(FieldSpec spec, JsonNode value, string path)
    {
        switch (spec.Kind)
        {
            case FieldKind.Version:
                if (!TryReadInteger(value, out var version)) return Bad("invalid-field-type", "v 必须是整数", path);
                if (version != AttachmentProtocol.Version)
                {
                    return Bad("version-mismatch", $"协议版本必须为 {AttachmentProtocol.Version}", path);
                }

                return Good(JsonValue.Create((int)version));
            case FieldKind.Int:
                if (!TryReadInteger(value, out var number)) return Bad("invalid-field-type", "必须是整数", path);
                if (number < 0) return Bad("negative-integer", "不允许负数", path);
                if (number < spec.Min || number > spec.Max)
                {
                    return Bad("integer-out-of-range", $"必须在 {spec.Min}..{spec.Max}", path);
                }

                return Good(JsonValue.Create((int)number));
            case FieldKind.Id:
            case FieldKind.Text:
                {
                    if (value is not JsonValue textValue || !textValue.TryGetValue<string>(out var text))
                    {
                        return Bad("invalid-field-type", "必须是字符串", path);
                    }

                    if (text.Length > spec.MaxChars) return Bad("field-too-large", $"长度超过 {spec.MaxChars}", path);
                    if (text.Length < spec.MinChars) return Bad("invalid-field-value", $"长度不足 {spec.MinChars}", path);
                    if (spec.Pattern is { } pattern && !RegexFor(spec.Pattern).IsMatch(text))
                    {
                        return Bad("invalid-field-value", "不符合冻结模式（禁止路径分隔符/空白/控制字符）", path);
                    }

                    return Good(JsonValue.Create(text));
                }

            case FieldKind.Sha256:
                {
                    if (value is not JsonValue hashValue || !hashValue.TryGetValue<string>(out var hash))
                    {
                        return Bad("invalid-field-type", "必须是字符串", path);
                    }

                    if (!Sha256Regex.IsMatch(hash))
                    {
                        return Bad("invalid-field-value", "必须是 64 位小写十六进制", path);
                    }

                    return Good(JsonValue.Create(hash));
                }

            case FieldKind.Base64:
                {
                    if (value is not JsonValue base64Value || !base64Value.TryGetValue<string>(out var base64))
                    {
                        return Bad("invalid-field-type", "必须是字符串", path);
                    }

                    if (base64.Length > spec.MaxChars)
                    {
                        return Bad("field-too-large", $"Base64 超过 {spec.MaxChars} 字符", path);
                    }

                    if (base64.Length < spec.MinChars || !IsCanonicalBase64(base64))
                    {
                        return Bad("invalid-field-value", "不是规范 Base64", path);
                    }

                    var bytes = Convert.FromBase64String(base64);
                    return new FieldOutcome(
                        true,
                        Value: null,
                        PayloadBytes: bytes.Length,
                        PayloadSha256: Convert.ToHexStringLower(SHA256.HashData(bytes)));
                }

            case FieldKind.Enum:
                {
                    if (value is not JsonValue enumValue || !enumValue.TryGetValue<string>(out var member))
                    {
                        return Bad("invalid-field-type", "必须是字符串", path);
                    }

                    if (spec.Values is null || !spec.Values.Contains(member, StringComparer.Ordinal))
                    {
                        return Bad("unknown-enum-value", $"不在冻结枚举 {string.Join('|', spec.Values ?? [])}", path);
                    }

                    return Good(JsonValue.Create(member));
                }

            case FieldKind.EnumArray:
            case FieldKind.IdArray:
                {
                    if (value is not JsonArray array) return Bad("invalid-field-type", "必须是数组", path);
                    if (array.Count > spec.MaxItems) return Bad("field-too-large", $"数组超过 {spec.MaxItems} 项", path);
                    var items = new JsonArray();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var index = 0; index < array.Count; index += 1)
                    {
                        var item = array[index];
                        if (item is not JsonValue itemValue || !itemValue.TryGetValue<string>(out var text))
                        {
                            return Bad(
                                spec.Kind == FieldKind.EnumArray ? "unknown-enum-value" : "invalid-field-type",
                                "数组项必须是字符串",
                                $"{path}[{index}]");
                        }

                        if (spec.Kind == FieldKind.EnumArray && (spec.Values is null || !spec.Values.Contains(text, StringComparer.Ordinal)))
                        {
                            return Bad("unknown-enum-value", "未知枚举成员", $"{path}[{index}]");
                        }

                        if (spec.Kind == FieldKind.IdArray)
                        {
                            var idOutcome = CheckField(Id("item"), itemValue, $"{path}[{index}]");
                            if (!idOutcome.Ok) return idOutcome;
                        }

                        if (!seen.Add(text)) return Bad("invalid-field-value", "成员重复", $"{path}[{index}]");
                        items.Add(text);
                    }

                    return Good(items);
                }

            case FieldKind.Limits:
                {
                    if (value is not JsonObject limits) return Bad("invalid-field-type", "必须是对象", path);
                    var keyFailure = ScanKeys(limits, LimitFields, path);
                    if (keyFailure is not null) return Bad(keyFailure.Code!, keyFailure.Detail!, keyFailure.Path!);
                    var normalized = new JsonObject();
                    foreach (var field in LimitFields)
                    {
                        if (!limits.TryGetPropertyValue(field.Name, out var fieldValue) || fieldValue is null)
                        {
                            return Bad("missing-field", $"缺少 {field.Name}", $"{path}.{field.Name}");
                        }

                        var outcome = CheckField(field, fieldValue, $"{path}.{field.Name}");
                        if (!outcome.Ok) return outcome;
                        normalized[field.Name] = outcome.Value?.DeepClone();
                    }

                    return Good(normalized);
                }

            case FieldKind.ResultArray:
                {
                    if (value is not JsonArray array) return Bad("invalid-field-type", "必须是数组", path);
                    if (array.Count < spec.MinItems || array.Count > spec.MaxItems)
                    {
                        return Bad("field-too-large", $"数组长度必须在 {spec.MinItems}..{spec.MaxItems}", path);
                    }

                    var items = new JsonArray();
                    for (var index = 0; index < array.Count; index += 1)
                    {
                        if (array[index] is not JsonObject item) return Bad("invalid-field-type", "数组项必须是对象", $"{path}[{index}]");
                        var keyFailure = ScanKeys(item, ResultItemFields, $"{path}[{index}]");
                        if (keyFailure is not null) return Bad(keyFailure.Code!, keyFailure.Detail!, keyFailure.Path!);
                        var normalized = new JsonObject();
                        foreach (var field in ResultItemFields)
                        {
                            if (!item.TryGetPropertyValue(field.Name, out var fieldValue) || fieldValue is null)
                            {
                                if (field.Required)
                                {
                                    return Bad("missing-field", $"缺少 {field.Name}", $"{path}[{index}].{field.Name}");
                                }

                                continue;
                            }

                            var outcome = CheckField(field, fieldValue, $"{path}[{index}].{field.Name}");
                            if (!outcome.Ok) return outcome;
                            normalized[field.Name] = outcome.Value?.DeepClone();
                        }

                        items.Add(normalized);
                    }

                    return Good(items);
                }

            default:
                return Bad("invalid-field-type", "未支持字段类型", path);
        }
    }

    private static Regex RegexFor(string pattern) => pattern switch
    {
        AttachmentProtocol.IdPattern => IdRegex,
        AttachmentProtocol.Sha256Pattern => Sha256Regex,
        AttachmentProtocol.LeafNamePattern => LeafNameRegex,
        AttachmentProtocol.MimePattern => MimeRegex,
        AttachmentProtocol.BuildIdPattern => BuildIdRegex,
        AttachmentProtocol.Base64Pattern => Base64Regex,
        _ => new Regex(pattern, RegexOptions.CultureInvariant),
    };

    /// <summary>按 TS 语义读取"整数"：JSON number 且数值没有小数部分。超出 long 视为越界。</summary>
    private static bool TryReadInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue jsonValue) return false;
        var text = jsonValue.ToJsonString();
        if (text.Length == 0 || text[0] == '"') return false;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        if (!double.IsFinite(number) || Math.Floor(number) != number) return false;
        if (number > long.MaxValue) return false;
        if (number < long.MinValue) return false;
        value = (long)number;
        return true;
    }

    /// <summary>严格 Base64：长度 4 的倍数、仅规范字符、padding 只在末尾。</summary>
    public static bool IsCanonicalBase64(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length % 4 != 0) return false;
        if (!Base64Regex.IsMatch(text)) return false;
        var padding = text.EndsWith("==", StringComparison.Ordinal) ? 2 : text.EndsWith('=') ? 1 : 0;
        for (var index = text.Length - padding; index < text.Length; index += 1)
        {
            if (text[index] != '=') return false;
        }

        return true;
    }

    /// <summary>严格解码；非法输入返回 false。</summary>
    public static bool TryDecodeBase64(string text, out byte[] bytes)
    {
        bytes = [];
        if (!IsCanonicalBase64(text)) return false;
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>标识类字符串的形状判定（与冻结 <c>IdPattern</c> 同一份正则，不另写一套）。</summary>
    public static bool IsIdShaped(string text) =>
        !string.IsNullOrEmpty(text) && IdRegex.IsMatch(text);

    private static FieldOutcome Good(JsonNode? value) => new(true, Value: value);

    private static FieldOutcome Bad(string code, string detail, string path) => new(false, Code: code, Detail: detail, Path: path);

    private static AttachmentDecodeResult Fail(string code, string detail, string path) =>
        new() { Ok = false, Code = code, Detail = detail, Path = path };
}
