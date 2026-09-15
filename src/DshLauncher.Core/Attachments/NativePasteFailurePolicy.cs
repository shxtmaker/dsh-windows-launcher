using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>失败后 UI 应该做什么（可恢复结果的一部分，必须确定）。</summary>
public enum NativePasteRetry
{
    /// <summary>重试没有意义（需要换来源或属于内部不变量失败）。</summary>
    None,

    /// <summary>平台瞬时状态（剪贴板 busy/变化、源被占用）：UI 可以就地重试<b>同一</b>手势。</summary>
    RetrySameGesture,

    /// <summary>需要用户先做点什么（建立会话、解锁、聚焦窗口、少选几个文件），然后再粘贴一次。</summary>
    RetryAfterUserAction,
}

/// <summary>失败分类结论：是否可恢复 + 确定的下一步。</summary>
public sealed record NativePasteFailureDecision(
    bool Recoverable,
    NativePasteRetry Retry,
    string Code,
    string Detail)
{
    /// <summary>UI 是否应当提供"重试"入口。</summary>
    public bool OffersRetry => Recoverable && Retry != NativePasteRetry.None;
}

/// <summary>
/// D16 失败分类（平台中立纯函数）。要求：剪贴板 busy / 打开失败 / 数据损坏 / 来源不支持 /
/// 无会话 / 锁定 / 子代理这些情形都必须给出<b>确定码 + 可恢复性 + 下一步</b>，
/// 而不是抛异常、挂住或静默丢弃。
///
/// 分类表是封闭的：不在表里的码按"不可恢复、不重试"处理（fail-closed），
/// 因此新增码必须显式登记，不能靠默认值蒙混。
/// </summary>
public static class NativePasteFailurePolicy
{
    /// <summary>可恢复且需要用户动作前的说明（例如先建立会话）。</summary>
    public const string NoSessionDetail = "当前没有有效会话：不自动创建、不猜测归属，请先建立会话再粘贴";

    /// <summary>确定的分类结果。</summary>
    public static NativePasteFailureDecision Classify(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new NativePasteFailureDecision(
                false,
                NativePasteRetry.None,
                NativePasteCodes.AuthorizationAbsent,
                "没有失败码：调用方必须先给出确定码");
        }

        return code switch
        {
            // —— 剪贴板瞬时状态：可恢复，可就地重试同一手势 ——
            "clipboard-busy" => Recoverable(NativePasteRetry.RetrySameGesture, code, "剪贴板被其他进程占用"),
            "clipboard-open-failed" => Recoverable(NativePasteRetry.RetrySameGesture, code, "打开剪贴板失败"),
            "clipboard-changed" => Recoverable(NativePasteRetry.RetrySameGesture, code, "剪贴板内容在读取期间变化"),
            NativePasteOrchestrator.ClipboardTimeoutCode =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "原生读取超时：请重新粘贴"),

            // —— 剪贴板内容问题：可恢复，但需要用户重新复制 ——
            ClipboardSourcePolicy.EmptyCode => Recoverable(NativePasteRetry.RetryAfterUserAction, code, "剪贴板为空"),
            ClipboardSourcePolicy.FileListEmptyCode =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "文件列表为空"),
            "clipboard-malformed" => Recoverable(NativePasteRetry.RetryAfterUserAction, code, "剪贴板数据畸形"),
            ClipboardSourcePolicy.UnsupportedCode =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "剪贴板格式不受支持"),

            // —— 尺寸/像素格式：前者换一张图，后者确定不支持 ——
            ScreenshotPngPolicy.DimensionsInvalidCode =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "位图尺寸非法或超出上限"),
            ScreenshotPngPolicy.PixelFormatUnsupportedCode =>
                new NativePasteFailureDecision(false, NativePasteRetry.None, code, "位图像素格式不受支持"),
            ScreenshotPngPolicy.EncodeFailedCode =>
                new NativePasteFailureDecision(false, NativePasteRetry.None, code, "PNG 编码失败"),

            // —— 冻结限额：换更小的输入 ——
            "limit-file-bytes" or "limit-batch-files" or "limit-batch-bytes" or "limit-staging-bytes"
                or "limit-screenshot-pixels" =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "超出冻结限额"),

            // —— D14 来源判定：源可能被占用/消失（可重试），或根本不是可接受的候选 ——
            AttachmentStagingCodes.SourceLocked or AttachmentStagingCodes.SourceUnavailable
                or AttachmentStagingCodes.SourceReadFailed =>
                Recoverable(NativePasteRetry.RetrySameGesture, code, "源文件暂时不可读"),
            AttachmentStagingCodes.SourceChanged =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "源文件在复制期间变化：请重新复制"),
            AttachmentStagingCodes.Directory or AttachmentStagingCodes.UncPath
                or AttachmentStagingCodes.DevicePath or AttachmentStagingCodes.PathTraversal
                or AttachmentStagingCodes.PathNotAbsolute or AttachmentStagingCodes.ReparsePoint
                or AttachmentStagingCodes.CloudPlaceholder or AttachmentStagingCodes.OfflineFile
                or AttachmentStagingCodes.NetworkShare or AttachmentStagingCodes.NotRegularFile =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "候选不是本功能支持的普通本机文件"),
            AttachmentStagingCodes.InsufficientFreeSpace =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "暂存空间不足"),

            // —— 会话/编辑器状态：确定"现在不能导入"，绝不自动创建会话 ——
            // no-session 是冻结线协议码，这里直接复用，不另造同义码。
            "no-session" => Recoverable(NativePasteRetry.RetryAfterUserAction, code, NoSessionDetail),
            AttachmentImportGate.ComposerLockedCode =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "编辑器已锁定：现在不能导入"),
            AttachmentImportGate.SubagentPromptCode =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "当前是子代理提示：不导入附件"),
            AttachmentImportGate.ComposerNotEditableCode =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "编辑器当前不可编辑"),

            // —— 页面生命周期：等页面可用后再粘贴 ——
            RemotePageSessionCodes.WorkPageSuspended or RemotePageSessionCodes.WorkCapabilityAbsent
                or RemotePageSessionCodes.SessionClosed =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "页面当前不接受新操作"),

            // —— 手势授权：必须由一次新的真实手势重来，旧授权不复活 ——
            NativePasteCodes.GestureWindowHidden or NativePasteCodes.GestureWindowInactive
                or NativePasteCodes.GestureFocusOutsidePage or NativePasteCodes.GestureTabNotActive =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "先让窗口/标签获得焦点，再重新粘贴"),
            NativePasteCodes.AuthorizationUnknown or NativePasteCodes.AuthorizationReplayed
                or NativePasteCodes.AuthorizationExpired or NativePasteCodes.AuthorizationNotYetValid
                or NativePasteCodes.AuthorizationTargetMismatch or NativePasteCodes.AuthorizationEpochMismatch
                or NativePasteCodes.AuthorizationFocusMismatch or NativePasteCodes.AuthorizationAbsent
                or NativePasteCodes.GestureAlreadyMinted or NativePasteCodes.GestureIdInvalid
                or NativePasteCodes.GestureLifetimeTooLong or NativePasteCodes.GestureRegistryFull
                or NativePasteCodes.GestureTargetInvalid =>
                Recoverable(NativePasteRetry.RetryAfterUserAction, code, "授权无效：请重新执行一次粘贴手势"),

            // —— 取消：用户意图，重新发起即可 ——
            "cancelled" => Recoverable(NativePasteRetry.RetryAfterUserAction, code, "操作已取消"),

            // —— 唯一消费者不变量：这些码说明有第二次导入企图，绝不能重试 ——
            NativePasteConsumerLedger.AlreadyElectedCode or NativePasteConsumerLedger.NotElectedCode
                or NativePasteConsumerLedger.UnknownGestureCode
                or NativePasteConsumerLedger.BridgeAfterNativeTimeoutCode
                or NativePasteConsumerLedger.NativeAbandonedCode =>
                new NativePasteFailureDecision(false, NativePasteRetry.None, code, "唯一消费者不变量：不得重复导入"),

            // —— 其余一律 fail-closed：不可恢复、不重试 ——
            _ => new NativePasteFailureDecision(
                false,
                NativePasteRetry.None,
                code,
                "未登记的失败码：按不可恢复处理"),
        };
    }

    private static NativePasteFailureDecision Recoverable(NativePasteRetry retry, string code, string detail) =>
        new(true, retry, code, detail);
}

/// <summary>
/// 导入前的会话/编辑器事实（由页面握手声明；宿主只做判定，不据此读取任何本机文件）。
/// </summary>
public readonly record struct ImportContextFacts(
    string? SessionId,
    bool ComposerEditable,
    bool ComposerLocked,
    bool SubagentActive,
    bool PageAdmitted,
    string PageAdmissionCode);

/// <summary>导入闸门判定结果。</summary>
public readonly record struct ImportGateDecision(bool Allowed, string Code, string Detail);

/// <summary>
/// D16 导入闸门（纯函数）：无 session / 锁定 / 子代理提示等状态下给出确定的"现在不能导入"结果，
/// 既不静默丢弃，也不自动创建会话。判定顺序固定：
/// 会话 → 子代理 → 锁定 → 可编辑 → 页面准入。
/// </summary>
public static class AttachmentImportGate
{
    /// <summary>编辑器被锁定。</summary>
    public const string ComposerLockedCode = "composer-locked";

    /// <summary>当前处于子代理提示。</summary>
    public const string SubagentPromptCode = "subagent-prompt";

    /// <summary>编辑器存在但当前不可编辑。</summary>
    public const string ComposerNotEditableCode = "composer-not-editable";

    /// <summary>允许导入。</summary>
    public const string AllowedCode = "import-allowed";

    /// <summary>按固定顺序判定。</summary>
    public static ImportGateDecision Evaluate(ImportContextFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.SessionId))
        {
            return new ImportGateDecision(false, "no-session", NativePasteFailurePolicy.NoSessionDetail);
        }

        if (facts.SubagentActive)
        {
            return new ImportGateDecision(false, SubagentPromptCode, "当前是子代理提示：不接收附件");
        }

        if (facts.ComposerLocked)
        {
            return new ImportGateDecision(false, ComposerLockedCode, "编辑器已锁定：不接收附件");
        }

        if (!facts.ComposerEditable)
        {
            return new ImportGateDecision(false, ComposerNotEditableCode, "编辑器当前不可编辑");
        }

        if (!facts.PageAdmitted)
        {
            return new ImportGateDecision(
                false,
                string.IsNullOrEmpty(facts.PageAdmissionCode) ? RemotePageSessionCodes.WorkCapabilityAbsent : facts.PageAdmissionCode,
                "页面当前不接受新操作");
        }

        return new ImportGateDecision(true, AllowedCode, "可以导入");
    }
}
