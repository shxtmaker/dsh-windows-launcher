using System.Text.Json.Nodes;
using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>解析 <c>capabilities</c> 的结果：特性、钳制后的生效限额与截图所有者结论。</summary>
public sealed record RemoteBridgeCapabilities
{
    /// <summary>对端声明的线协议特性（冻结枚举，codec 已逐项校验）。</summary>
    public required IReadOnlyList<string> Features { get; init; }

    /// <summary>对端声明的限额（原文）。</summary>
    public required AttachmentLimits DeclaredLimits { get; init; }

    /// <summary>生效限额 = 逐项 min(宿主策略, 对端声明)。</summary>
    public required AttachmentLimits EffectiveLimits { get; init; }

    /// <summary>交给截图所有者判定的握手事实。</summary>
    public required ScreenshotOwnerFacts Facts { get; init; }

    /// <summary>截图所有者结论（D16 纯函数给出）。</summary>
    public required ScreenshotOwnerDecision Decision { get; init; }

    /// <summary>对端是否声明了 <c>screenshot</c> 特性。</summary>
    public bool AdvertisesScreenshot => Facts.PluginAdvertisesScreenshot;
}

/// <summary>解析 <c>context</c> 的结果：线协议身份与导入闸门要用的 composer 事实。</summary>
public sealed record RemoteBridgeContext
{
    /// <summary>对端声明的身份（session / target / documentEpoch / composerEpoch / composerScope）。</summary>
    public required AttachmentIdentity Identity { get; init; }

    /// <summary>composer 上下文事实（<c>PageAdmitted</c> 由调用方按准入结果补齐）。</summary>
    public required ImportContextFacts Facts { get; init; }
}

/// <summary>
/// D17 握手解析（<b>纯函数</b>，平台中立）：把已由生产 codec 解码的 <c>hello</c> / <c>capabilities</c> /
/// <c>context</c> 翻译成 D16 的两个真实消费者要的事实。
///
/// 这一层补上 D16 明确登记的缺口：D16 只交付了 <c>ReportScreenshotHandshake</c> /
/// <c>ReportComposerContext</c> 两个接入点，在握手被真正解析之前，位图粘贴会以
/// <c>consumer-mode-undecided</c> 被确定拒绝。D17 之后：
/// <list type="bullet">
/// <item><c>capabilities.features</c> 含 <c>screenshot</c> → 对端<b>要求</b>宿主原生采集，
/// 且宿主原生采集可用时，截图所有者 = <c>native-paste</c>；</item>
/// <item>不含 <c>screenshot</c>（当前生产插件的 <c>CLIENT_FEATURES</c> 就是这一种）→
/// 截图所有者 = <c>bridge</c>，宿主完全不碰剪贴板位图，由页面自己的真实 paste 处理；
/// 这与方案 §4.1「普通截图优先保留浏览器真实 paste」一致；</item>
/// <item>宿主原生采集不可用 → 一律 <c>bridge</c>；</item>
/// <item>握手到达之前 → <c>undecided</c>，位图粘贴确定拒绝，绝不猜。</item>
/// </list>
/// v1 线协议里与截图相关的信号只有 <c>screenshot</c> 这一个特性位，因此
/// <c>PluginPrefersNativePaste</c> 与 <c>PluginAdvertisesScreenshot</c> 同源（都来自该特性位）。
/// </summary>
public static class RemoteBridgeHandshake
{
    /// <summary>页面必须携带非空 sessionId 才建立身份（冻结协议：没有有效 sessionId 不接收批次）。</summary>
    public const string NoSessionDetail = "context 没有有效 sessionId：不建立身份、不接收批次";

    /// <summary>解析 <c>capabilities</c> 并给出生效限额与截图所有者事实。</summary>
    public static RemoteBridgeCapabilities ParseCapabilities(
        AttachmentMessage message,
        AttachmentLimits hostLimits,
        bool nativeCaptureAvailable)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(hostLimits);
        if (message.Kind != WireMessageKind.Capabilities)
        {
            throw new ArgumentException("只接受 capabilities 消息", nameof(message));
        }

        var declared = ReadLimits(message.Limits) ?? hostLimits;
        var effective = Clamp(hostLimits, declared);
        var features = message.Features;
        var advertisesScreenshot = features.Contains("screenshot", StringComparer.Ordinal);
        var facts = new ScreenshotOwnerFacts(
            PluginAdvertisesScreenshot: advertisesScreenshot,
            PluginPrefersNativePaste: advertisesScreenshot,
            NativeCaptureAvailable: nativeCaptureAvailable);

        return new RemoteBridgeCapabilities
        {
            Features = features,
            DeclaredLimits = declared,
            EffectiveLimits = effective,
            Facts = facts,
            Decision = ScreenshotOwnerHandshake.Decide(facts),
        };
    }

    /// <summary>解析 <c>context</c>；调用方用页面准入结果补齐 <c>PageAdmitted</c>。</summary>
    public static RemoteBridgeContext ParseContext(AttachmentMessage message, bool pageAdmitted, string admissionCode)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Kind != WireMessageKind.Context)
        {
            throw new ArgumentException("只接受 context 消息", nameof(message));
        }

        var identity = new AttachmentIdentity(
            message.SessionId!,
            message.TargetId!,
            message.DocumentEpoch!.Value,
            message.ComposerEpoch!.Value,
            message.ComposerScope!);

        // v1 的 context 只承载身份：能建立绑定就说明页面有一个当前可编辑的 composer
        // （D12/D13 在绑定前已核对当前会话与能力）。锁定/子代理提示不在线协议里，
        // 它们会在 import-result 阶段以确定码返回，由 UI 如实显示。
        var facts = new ImportContextFacts(
            identity.SessionId,
            ComposerEditable: true,
            ComposerLocked: false,
            SubagentActive: false,
            PageAdmitted: pageAdmitted,
            PageAdmissionCode: admissionCode);

        return new RemoteBridgeContext { Identity = identity, Facts = facts };
    }

    /// <summary>逐项取 min（对端声明不得超过冻结上限；codec 已判，这里再夹一次）。</summary>
    public static AttachmentLimits Clamp(AttachmentLimits host, AttachmentLimits peer) => new()
    {
        MaxFileBytes = Math.Min(host.MaxFileBytes, peer.MaxFileBytes),
        MaxFilesPerBatch = Math.Min(host.MaxFilesPerBatch, peer.MaxFilesPerBatch),
        MaxBatchBytes = Math.Min(host.MaxBatchBytes, peer.MaxBatchBytes),
        MaxScreenshotPixels = Math.Min(host.MaxScreenshotPixels, peer.MaxScreenshotPixels),
        MaxStagingBytesPerTarget = Math.Min(host.MaxStagingBytesPerTarget, peer.MaxStagingBytesPerTarget),
        MaxConcurrentTargets = Math.Min(host.MaxConcurrentTargets, peer.MaxConcurrentTargets),
    };

    private static AttachmentLimits? ReadLimits(JsonObject? limits)
    {
        if (limits is null)
        {
            return null;
        }

        return new AttachmentLimits
        {
            MaxFileBytes = Int(limits, "maxFileBytes", AttachmentProtocol.MaxFileBytes),
            MaxFilesPerBatch = Int(limits, "maxFilesPerBatch", AttachmentProtocol.MaxFilesPerBatch),
            MaxBatchBytes = Int(limits, "maxBatchBytes", AttachmentProtocol.MaxBatchBytes),
            MaxScreenshotPixels = Long(limits, "maxScreenshotPixels", AttachmentProtocol.MaxScreenshotPixels),
            MaxStagingBytesPerTarget = Int(
                limits,
                "maxStagingBytesPerTarget",
                AttachmentProtocol.MaxStagingBytesPerTarget),
            MaxConcurrentTargets = Int(limits, "maxConcurrentTargets", AttachmentProtocol.MaxConcurrentTargets),
        };
    }

    private static int Int(JsonObject limits, string name, int fallback) =>
        limits.TryGetPropertyValue(name, out var node)
        && node is JsonValue value
        && value.TryGetValue<int>(out var parsed)
            ? parsed
            : fallback;

    private static long Long(JsonObject limits, string name, long fallback) =>
        limits.TryGetPropertyValue(name, out var node)
        && node is JsonValue value
        && value.TryGetValue<long>(out var parsed)
            ? parsed
            : fallback;
}
