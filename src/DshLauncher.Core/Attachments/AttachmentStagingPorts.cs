namespace DshLauncher.Core.Attachments;

/// <summary>
/// 平台层打开的一个候选源（D14）。句柄由原生适配层用<b>不跟随重解析点</b>的语义打开，
/// 因此 Core 拿不到"路径"，只拿到已判定的事实与顺序读取能力。契约：
/// <list type="bullet">
/// <item><see cref="Read"/> 允许短读；返回 0 表示 EOF；返回负数或超过请求长度是源故障；</item>
/// <item><see cref="ReadIdentity"/> 必须反映<b>当前源路径</b>的真实现状（Windows 侧重新解析路径并比较文件标识），
/// 因此复制期间被改名替换的同名文件也会被识别为变化；</item>
/// <item>无论成功、失败还是取消，暂存服务都会在 finally 中调用 <see cref="IDisposable.Dispose"/>。</item>
/// </list>
/// </summary>
public interface IStagingSourceHandle : IDisposable
{
    /// <summary>打开时判定的事实（种类/路径形态/属性/长度）。</summary>
    StagingSourceDescriptor Descriptor { get; }

    /// <summary>打开时的源身份。</summary>
    StagingSourceIdentity InitialIdentity { get; }

    /// <summary>复制结束后重新读取的源身份；源已消失时返回 <see cref="StagingSourceIdentity.Missing"/>。</summary>
    StagingSourceIdentity ReadIdentity();

    /// <summary>顺序读取至多 <c>destination.Length</c> 字节；短读合法，0 表示 EOF。</summary>
    int Read(Span<byte> destination);
}

/// <summary>
/// 平台层创建的一个暂存目标文件。平台实现必须用<b>独占创建</b>（CREATE_NEW）建立文件，
/// 并在 <see cref="FinalPath"/> 里给出句柄解析出的真实最终路径，供 Core 复核包含关系。
/// </summary>
public interface IStagingDestinationFile : IDisposable
{
    /// <summary>句柄解析出的最终路径（Windows：GetFinalPathNameByHandle）。</summary>
    string FinalPath { get; }

    /// <summary>顺序写入（绝不整文件缓冲）。</summary>
    void Write(ReadOnlySpan<byte> buffer);

    /// <summary>写入落盘（Windows：FlushFileBuffers）。</summary>
    void FlushToDisk();

    /// <summary>删除这个刚创建的文件（失败/取消清理半成品）；删除也被限制在自有根内。</summary>
    void Delete();
}

/// <summary>自有根内一个条目的无跟随解析结果。</summary>
public sealed record StagingOwnedEntryInfo(
    string FullPath,
    string FinalPath,
    bool Exists,
    bool IsDirectory,
    bool IsReparsePoint);

/// <summary>
/// 注入的文件系统端口（D14）：暂存服务只经它访问磁盘，因此不接触任何 Windows API，
/// 也能在 Linux 上由假实现精确驱动（配额、失败、取消、路径逃逸）。
/// 生产实现是 <c>DshLauncher.Platform.Windows.Attachments.WindowsStagingFileSystem</c>。
/// </summary>
public interface IStagingFileSystem
{
    /// <summary>自有根所在卷的可用字节数（Windows：GetDiskFreeSpaceExW）。</summary>
    long GetAvailableFreeBytes(string ownedRootPath);

    /// <summary>列出自有根下的条目<b>绝对路径</b>；只列一层，不递归。</summary>
    IReadOnlyList<string> ListOwnedEntries(string ownedRootPath);

    /// <summary>用无跟随语义解析自有根内的一个条目。</summary>
    StagingOwnedEntryInfo InspectOwnedEntry(string ownedRootPath, string fullPath);

    /// <summary>在自有根内独占创建一个新文件（存在即失败，绝不覆盖、绝不跟随链接）。</summary>
    IStagingDestinationFile CreateExclusiveOwnedFile(string fullPath);

    /// <summary>
    /// 删除自有根内的一个文件。调用方（暂存服务）只会在
    /// <see cref="StagingCleanupPolicy.Evaluate"/> 允许后调用，生产实现仍会再次复核包含关系。
    /// </summary>
    void DeleteOwnedEntry(string ownedRootPath, string fullPath);
}

/// <summary>
/// 一次原生用户动作产生的一次性候选票据（D14）。
///
/// 这是"无任意路径读取入口"的结构性保证：暂存服务只接受票据，票据由原生粘贴处理器在
/// 用户手势中铸造，<b>页面/桥只能拿到票据 id，拿不到也不能构造路径</b>。
/// <see cref="OpenSource"/> 的实现负责用平台语义完成打开与事实判定；
/// 打不开时抛出 <see cref="AttachmentStagingException"/>（携带稳定码）。
/// </summary>
public interface INativeStagingCapture
{
    /// <summary>一次性票据 id（本地生成，桥只传它，不传路径）。</summary>
    string CaptureId { get; }

    /// <summary>用户动作里的建议文件名（仅用于展示；不得当作路径使用）。</summary>
    string SuggestedLeafName { get; }

    /// <summary>用平台无跟随语义打开候选；失败抛 <see cref="AttachmentStagingException"/>。</summary>
    IStagingSourceHandle OpenSource();
}

/// <summary>暂存适配的失败异常：携带本地判定码，绝不写入线协议报文。</summary>
public sealed class AttachmentStagingException : InvalidOperationException
{
    public AttachmentStagingException()
    {
        Code = string.Empty;
    }

    public AttachmentStagingException(string? message)
        : base(message)
    {
        Code = string.Empty;
    }

    public AttachmentStagingException(string? message, Exception? innerException)
        : base(message, innerException)
    {
        Code = string.Empty;
    }

    public AttachmentStagingException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public AttachmentStagingException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>本地判定码（<see cref="AttachmentStagingCodes"/>）。</summary>
    public string Code { get; }
}

/// <summary>应用自有的暂存根：只接受本机绝对路径，拒绝 UNC/设备/相对路径与 <c>..</c> 段。</summary>
public sealed class AttachmentStagingRoot
{
    /// <summary>所有权标记文件名（与 D01 应用数据根同一套所有权思路）。</summary>
    public const string OwnershipMarkerName = ".dsh-attachment-staging-owner";

    /// <summary>所有权标记内容。</summary>
    public const string OwnershipMarkerContent = "DshWindowsLauncher:attachment-staging:v1";

    public AttachmentStagingRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        FullPath = StagingPathPolicy.NormalizeOwnedRoot(rootPath);

        // 词法拼接而不是 Path.Combine：本策略刻意使用 Windows 分隔符，
        // 这样同一套包含关系判定在 Linux 测试里逐字可复现。
        OwnershipMarkerPath = FullPath + "\\" + OwnershipMarkerName;
    }

    /// <summary>去掉尾部分隔符的本机绝对路径。</summary>
    public string FullPath { get; }

    /// <summary>所有权标记路径。</summary>
    public string OwnershipMarkerPath { get; }

    /// <summary>把叶名解析为自有根下的绝对路径（越界/非法叶名一律拒绝）。</summary>
    public StagingPathDecision ResolveOwnedLeaf(string leafName) =>
        StagingPathPolicy.ResolveOwnedLeaf(FullPath, leafName);
}
