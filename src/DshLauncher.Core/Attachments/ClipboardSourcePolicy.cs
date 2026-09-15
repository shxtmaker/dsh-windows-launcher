namespace DshLauncher.Core.Attachments;

/// <summary>
/// 一次剪贴板探测的<b>事实</b>（由平台半区在短窗口内取得，不搬移大块数据）：
/// 序号、格式可用性、文件个数、位图尺寸与载荷字节数。
/// 这里刻意<b>不</b>含任何路径、句柄或剪贴板指针：Core 只按事实做判定。
/// </summary>
public readonly record struct ClipboardProbe(
    long SequenceNumber,
    int FormatCount,
    bool HasText,
    bool HasFileList,
    bool HasBitmap,
    int FileCount,
    int BitmapWidth,
    int BitmapHeight,
    int BitmapBitsPerPixel,
    long BitmapPayloadBytes)
{
    /// <summary>剪贴板为空（没有任何格式）。</summary>
    public static ClipboardProbe Empty => default;

    /// <summary>剪贴板确实持有至少一种格式。</summary>
    public bool HasAnyFormat => FormatCount > 0;

    /// <summary>同时具有文件列表与位图。</summary>
    public bool HasFileListAndBitmap => HasFileList && HasBitmap;
}

/// <summary>剪贴板内容的归一化种类（按"有没有附件价值"分类）。</summary>
public enum ClipboardSourceKind
{
    /// <summary>剪贴板为空。</summary>
    Empty,

    /// <summary>只有文本：保持原行为，附件通路完全不参与。</summary>
    TextOnly,

    /// <summary>只有文件列表。</summary>
    FileList,

    /// <summary>只有位图。</summary>
    Bitmap,

    /// <summary>文件列表与位图同时存在。</summary>
    FileListAndBitmap,

    /// <summary>有格式，但没有一种本功能认识。</summary>
    Unsupported,
}

/// <summary>一次手势的消费去向。</summary>
public enum ClipboardImportRoute
{
    /// <summary>不导入（拒绝或无需附件通路）。</summary>
    None,

    /// <summary>纯文本：宿主不介入，浏览器保留原生粘贴行为。</summary>
    OriginalTextBehaviour,

    /// <summary>宿主原生读取剪贴板并走 D14 暂存。</summary>
    NativePaste,

    /// <summary>页面桥自己处理（宿主不碰剪贴板内容）。</summary>
    Bridge,
}

/// <summary>来源优先与路由判定结果。</summary>
public sealed record ClipboardSourceDecision
{
    /// <summary>归一化后的来源种类。</summary>
    public required ClipboardSourceKind Kind { get; init; }

    /// <summary>消费去向。</summary>
    public required ClipboardImportRoute Route { get; init; }

    /// <summary>确定判定码（见 <see cref="NativePasteCodes"/> 与冻结码）。</summary>
    public required string Code { get; init; }

    /// <summary>人类可读说明。</summary>
    public string? Detail { get; init; }

    /// <summary>本次将导入的文件数（原生文件列表用）。</summary>
    public int FileCount { get; init; }

    /// <summary>是否会产生附件导入（纯文本与拒绝都不产生）。</summary>
    public bool ImportsAttachments => Route is ClipboardImportRoute.NativePaste or ClipboardImportRoute.Bridge;
}

/// <summary>
/// D16 来源优先规则（平台中立纯函数）。判定顺序固定，因此同一个剪贴板状态总有同一个结论：
/// <list type="number">
/// <item>文件列表优先：只要剪贴板里有 CF_HDROP，就按文件列表处理，<b>即使同时还有位图</b>；</item>
/// <item>文件列表为空或超过批内文件数上限 → 确定拒绝（<c>clipboard-file-list-empty</c> / 冻结码 <c>limit-batch-files</c>）；</item>
/// <item>文件列表只能走原生通路（浏览器拿不到 CF_HDROP，也不允许页面要求宿主读取本机文件）；</item>
/// <item>只有位图时按<b>握手确定的截图所有者</b>选 native-paste 或 bridge；尚未确定则拒绝而不是猜；</item>
/// <item>只有文本 → <see cref="ClipboardImportRoute.OriginalTextBehaviour"/>：保留原行为，附件通路不参与；</item>
/// <item>其余 → <c>clipboard-unsupported-format</c>。</item>
/// </list>
/// 位图在决定路由前先过尺寸/像素/字节上限（<see cref="ScreenshotPngPolicy"/>），
/// 因此"荒谬尺寸"在分配任何缓冲区<b>之前</b>就被拒绝。
/// </summary>
public static class ClipboardSourcePolicy
{
    /// <summary>剪贴板为空。</summary>
    public const string EmptyCode = "clipboard-empty";

    /// <summary>有 CF_HDROP 但一个文件都没有（畸形/已被清空）。</summary>
    public const string FileListEmptyCode = "clipboard-file-list-empty";

    /// <summary>文件列表与位图同时存在，按文件列表处理。</summary>
    public const string FileListPreferredCode = "clipboard-file-list-preferred";

    /// <summary>只有文件列表。</summary>
    public const string FileListOnlyCode = "clipboard-file-list-only";

    /// <summary>只有位图，按 native-paste 模式处理。</summary>
    public const string BitmapNativePasteCode = "clipboard-bitmap-native-paste";

    /// <summary>只有位图，按 bridge 模式交给页面。</summary>
    public const string BitmapBridgeCode = "clipboard-bitmap-bridge";

    /// <summary>只有文本：保留原行为。</summary>
    public const string TextOriginalCode = "clipboard-text-original";

    /// <summary>没有任何本功能认识的格式。</summary>
    public const string UnsupportedCode = "clipboard-unsupported-format";

    /// <summary>能力握手尚未确定截图所有者，不能猜。</summary>
    public const string ModeUndecidedCode = "consumer-mode-undecided";

    /// <summary>按固定顺序给出一次手势的来源优先与路由结论。</summary>
    public static ClipboardSourceDecision Decide(
        ClipboardProbe probe,
        ScreenshotOwnerMode mode,
        AttachmentLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (!probe.HasAnyFormat && !probe.HasText && !probe.HasFileList && !probe.HasBitmap)
        {
            return new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.Empty,
                Route = ClipboardImportRoute.None,
                Code = EmptyCode,
                Detail = "剪贴板为空",
            };
        }

        if (probe.HasFileList)
        {
            return DecideFileList(probe, limits);
        }

        if (probe.HasBitmap)
        {
            return DecideBitmap(probe, mode, limits);
        }

        if (probe.HasText)
        {
            return new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.TextOnly,
                Route = ClipboardImportRoute.OriginalTextBehaviour,
                Code = TextOriginalCode,
                Detail = "纯文本：保留浏览器原生粘贴行为",
            };
        }

        return new ClipboardSourceDecision
        {
            Kind = ClipboardSourceKind.Unsupported,
            Route = ClipboardImportRoute.None,
            Code = UnsupportedCode,
            Detail = $"剪贴板有 {probe.FormatCount} 种格式，但没有文件列表/位图/文本",
        };
    }

    /// <summary>
    /// 拖放来源的选路（拖放不经过剪贴板）：数据对象声明了文件列表走原生通路，
    /// 否则给确定的不支持码。项目数与逐个路径由平台半区在读取时判定（那时的拒绝码同样确定）。
    /// </summary>
    public static ClipboardSourceDecision DecideDrop(bool hasFileList) =>
        hasFileList
            ? new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.FileList,
                Route = ClipboardImportRoute.NativePaste,
                Code = FileListOnlyCode,
                Detail = "拖放数据对象声明了文件列表：项目数与逐个路径在读取时判定",
            }
            : new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.Unsupported,
                Route = ClipboardImportRoute.None,
                Code = UnsupportedCode,
                Detail = "拖放数据对象不含文件列表",
            };

    private static ClipboardSourceDecision DecideFileList(ClipboardProbe probe, AttachmentLimits limits)
    {
        var kind = probe.HasFileListAndBitmap ? ClipboardSourceKind.FileListAndBitmap : ClipboardSourceKind.FileList;
        if (probe.FileCount <= 0)
        {
            return new ClipboardSourceDecision
            {
                Kind = kind,
                Route = ClipboardImportRoute.None,
                Code = FileListEmptyCode,
                Detail = "CF_HDROP 不含任何文件",
                FileCount = 0,
            };
        }

        if (probe.FileCount > limits.MaxFilesPerBatch)
        {
            // 复用 D10/D14 的冻结限额码，不另造同义码。
            return new ClipboardSourceDecision
            {
                Kind = kind,
                Route = ClipboardImportRoute.None,
                Code = "limit-batch-files",
                Detail = $"文件列表 {probe.FileCount} 项超过批内上限 {limits.MaxFilesPerBatch}",
                FileCount = probe.FileCount,
            };
        }

        return new ClipboardSourceDecision
        {
            Kind = kind,
            Route = ClipboardImportRoute.NativePaste,
            Code = probe.HasFileListAndBitmap ? FileListPreferredCode : FileListOnlyCode,
            Detail = probe.HasFileListAndBitmap
                ? "同时存在文件列表与位图：按规则优先文件列表"
                : "只有文件列表",
            FileCount = probe.FileCount,
        };
    }

    private static ClipboardSourceDecision DecideBitmap(
        ClipboardProbe probe,
        ScreenshotOwnerMode mode,
        AttachmentLimits limits)
    {
        var dimension = ScreenshotPngPolicy.EvaluateDimensions(probe.BitmapWidth, probe.BitmapHeight, limits);
        if (!dimension.Allowed)
        {
            return new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.Bitmap,
                Route = ClipboardImportRoute.None,
                Code = dimension.Code!,
                Detail = dimension.Detail,
            };
        }

        return mode switch
        {
            ScreenshotOwnerMode.NativePaste => new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.Bitmap,
                Route = ClipboardImportRoute.NativePaste,
                Code = BitmapNativePasteCode,
                Detail = "握手确定由原生 paste 拥有截图输入",
            },
            ScreenshotOwnerMode.Bridge => new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.Bitmap,
                Route = ClipboardImportRoute.Bridge,
                Code = BitmapBridgeCode,
                Detail = "握手确定由页面桥拥有截图输入：宿主不读取位图",
            },
            _ => new ClipboardSourceDecision
            {
                Kind = ClipboardSourceKind.Bitmap,
                Route = ClipboardImportRoute.None,
                Code = ModeUndecidedCode,
                Detail = "能力握手尚未确定截图所有者：拒绝而不是猜",
            },
        };
    }
}
