using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>
/// 每个已验证页面的<b>截图输入所有者</b>（D16）。它在能力握手时确定，之后只影响<b>后续</b>用户动作；
/// 一次已经交给原生 paste 的动作不会因为模式变化而改走桥。
/// </summary>
public enum ScreenshotOwnerMode
{
    /// <summary>尚未完成握手：位图粘贴一律拒绝而不是猜。</summary>
    Undecided,

    /// <summary>原生 paste 拥有截图输入：宿主读取位图并编码 PNG。</summary>
    NativePaste,

    /// <summary>页面桥拥有截图输入：宿主动都不动，由页面的真实 paste 处理。</summary>
    Bridge,
}

/// <summary>握手事实（来自已验证页面的一次能力协商，不是页面每帧的声明）。</summary>
public readonly record struct ScreenshotOwnerFacts(
    bool PluginAdvertisesScreenshot,
    bool PluginPrefersNativePaste,
    bool NativeCaptureAvailable);

/// <summary>握手结论。</summary>
public readonly record struct ScreenshotOwnerDecision(ScreenshotOwnerMode Mode, string Code, string Detail);

/// <summary>
/// 握手 → 截图所有者模式的确定映射（纯函数）。规则顺序固定：
/// <list type="number">
/// <item>插件没有声明 <c>screenshot</c> 能力 → <see cref="ScreenshotOwnerMode.Bridge"/>（宿主绝不抢图片粘贴）；</item>
/// <item>宿主原生采集不可用（例如缺少 WebView2 宿主侧能力） → <see cref="ScreenshotOwnerMode.Bridge"/>；</item>
/// <item>插件明确要求原生 paste → <see cref="ScreenshotOwnerMode.NativePaste"/>；</item>
/// <item>其余 → <see cref="ScreenshotOwnerMode.Bridge"/>（保守：默认不碰剪贴板）。</item>
/// </list>
/// 一次握手只对<b>当前</b>页面代际有效；导航/重载后必须重新协商，否则模式回到
/// <see cref="ScreenshotOwnerMode.Undecided"/>（见 <see cref="ScreenshotOwnerRegistry"/>）。
/// </summary>
public static class ScreenshotOwnerHandshake
{
    /// <summary>插件未声明截图能力。</summary>
    public const string NoFeatureCode = "screenshot-owner-bridge-no-feature";

    /// <summary>宿主原生采集不可用。</summary>
    public const string NativeUnavailableCode = "screenshot-owner-bridge-native-unavailable";

    /// <summary>插件要求原生 paste 且能力具备。</summary>
    public const string NativeRequestedCode = "screenshot-owner-native-paste";

    /// <summary>插件声明了能力但没要求原生 paste：保守交给桥。</summary>
    public const string BridgePreferredCode = "screenshot-owner-bridge-plugin-preference";

    /// <summary>尚未握手。</summary>
    public const string UndecidedCode = "screenshot-owner-undecided";

    /// <summary>握手结论已过期（页面代际变了）。</summary>
    public const string EpochStaleCode = "screenshot-owner-epoch-stale";

    /// <summary>按固定顺序给出模式。</summary>
    public static ScreenshotOwnerDecision Decide(ScreenshotOwnerFacts facts)
    {
        if (!facts.PluginAdvertisesScreenshot)
        {
            return new ScreenshotOwnerDecision(
                ScreenshotOwnerMode.Bridge,
                NoFeatureCode,
                "插件未声明 screenshot 能力：截图粘贴完全留给页面");
        }

        if (!facts.NativeCaptureAvailable)
        {
            return new ScreenshotOwnerDecision(
                ScreenshotOwnerMode.Bridge,
                NativeUnavailableCode,
                "宿主原生采集不可用：退回桥模式");
        }

        if (facts.PluginPrefersNativePaste)
        {
            return new ScreenshotOwnerDecision(
                ScreenshotOwnerMode.NativePaste,
                NativeRequestedCode,
                "插件要求原生 paste 且宿主具备原生采集：由原生 paste 拥有截图输入");
        }

        return new ScreenshotOwnerDecision(
            ScreenshotOwnerMode.Bridge,
            BridgePreferredCode,
            "插件声明了截图能力但未要求原生 paste：保守交给桥");
    }
}

/// <summary>
/// 每目标页面的截图所有者台账：值与<b>页面代际</b>绑定，导航/刷新后自动失效。
/// "切换模式只影响后续用户动作"因此是结构性成立的：模式属于代际，而一次手势在开始时
/// 就把当时的模式记进 <see cref="NativePasteConsumerLedger"/>。
/// </summary>
public sealed class ScreenshotOwnerRegistry
{
    private readonly Dictionary<Guid, (RemotePageEpoch Epoch, ScreenshotOwnerMode Mode)> _modes = [];

    /// <summary>记录一次握手结论（按当前页面代际）。</summary>
    public ScreenshotOwnerDecision Record(Guid targetId, RemotePageEpoch epoch, ScreenshotOwnerFacts facts)
    {
        var decision = ScreenshotOwnerHandshake.Decide(facts);
        if (targetId != Guid.Empty)
        {
            _modes[targetId] = (epoch, decision.Mode);
        }

        return decision;
    }

    /// <summary>读取当前模式；目标没有记录或代际已变时返回 <see cref="ScreenshotOwnerMode.Undecided"/>。</summary>
    public ScreenshotOwnerMode ModeFor(Guid targetId, RemotePageEpoch epoch) =>
        _modes.TryGetValue(targetId, out var entry) && entry.Epoch == epoch
            ? entry.Mode
            : ScreenshotOwnerMode.Undecided;

    /// <summary>页面关闭 / 目标删除时丢弃记录。</summary>
    public bool Forget(Guid targetId) => _modes.Remove(targetId);

    /// <summary>当前记录数。</summary>
    public int Count => _modes.Count;
}
