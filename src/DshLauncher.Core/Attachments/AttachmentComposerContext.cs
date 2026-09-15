using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>
/// 每个目标页面最近一次<b>已准入</b>的 composer 上下文（会话 id / 可编辑 / 锁定 / 子代理）。
///
/// 页面只能"声明"这些事实，不能凭它换到任何本机读取：上下文只让导入闸门更严格，
/// 从不参与手势授权，也从不决定剪贴板读不读。
/// 同一目标的记录与页面代际绑定：导航/关闭后立即失效（回到"无会话"），
/// 因此旧页面的迟到声明不会给新页面带来会话。
/// </summary>
public sealed class AttachmentComposerContextStore
{
    private readonly Dictionary<Guid, (RemotePageEpoch Epoch, ImportContextFacts Facts)> _facts = [];

    /// <summary>尚未收到任何声明时的保守值：没有会话、不可编辑。</summary>
    public static ImportContextFacts Unknown { get; } =
        new(null, ComposerEditable: false, ComposerLocked: false, SubagentActive: false, PageAdmitted: false, string.Empty);

    /// <summary>读取某目标的当前上下文（无记录或代际不符时返回 <see cref="Unknown"/>）。</summary>
    public ImportContextFacts Current(Guid targetId) =>
        _facts.TryGetValue(targetId, out var entry) ? entry.Facts : Unknown;

    /// <summary>读取某目标在指定代际下的上下文；代际不符返回 <see cref="Unknown"/>。</summary>
    public ImportContextFacts Current(Guid targetId, RemotePageEpoch epoch) =>
        _facts.TryGetValue(targetId, out var entry) && entry.Epoch == epoch ? entry.Facts : Unknown;

    /// <summary>
    /// 记录一次页面声明。<paramref name="pageAdmitted"/> 必须来自
    /// <see cref="RemotePageSessionState.AdmitMessage"/> 的准入结果，未准入的声明一律不记录。
    /// </summary>
    public bool Record(
        Guid targetId,
        RemotePageEpoch epoch,
        ImportContextFacts facts,
        bool pageAdmitted)
    {
        if (targetId == Guid.Empty || !pageAdmitted)
        {
            return false;
        }

        _facts[targetId] = (epoch, facts);
        return true;
    }

    /// <summary>页面导航/关闭/目标删除时失效。</summary>
    public bool Invalidate(Guid targetId) => _facts.Remove(targetId);

    /// <summary>当前有记录的目标数。</summary>
    public int Count => _facts.Count;
}
