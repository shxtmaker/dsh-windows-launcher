using System.ComponentModel;
using System.Runtime.InteropServices;
using DshLauncher.Core.Attachments;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Platform.Windows.Attachments;

/// <summary>
/// 一次原生用户动作（资源管理器复制 / CF_HDROP）捕获的真实候选。
///
/// 打开语义（全部是 Win32 事实，实机待验）：
/// <list type="bullet">
/// <item><c>GENERIC_READ</c> + <c>FILE_SHARE_READ</c>：其他进程独占占用即共享冲突，映射为 <c>source-locked</c>；</item>
/// <item><c>FILE_FLAG_OPEN_REPARSE_POINT</c>：拿到的是链接本身而不是目标，因此符号链接/联接/挂载点
/// 会被识别为 <c>candidate-reparse-point</c>，不会顺着链接读到根外；</item>
/// <item><c>GetFileType</c> + 属性位：目录、设备/管道等非常规对象分别给出确定拒绝码；</item>
/// <item>属性位 <c>RECALL_ON_OPEN</c>/<c>RECALL_ON_DATA_ACCESS</c>/<c>OFFLINE</c> 判为云占位或脱机文件，
/// 一律拒绝而不触发自动下载；</item>
/// <item><c>GetDriveTypeW</c> 判远程卷（映射网络驱动器），拒绝读取网络共享；</item>
/// <item>复制后再按路径重新解析一次身份：同名换文件、删除、变成链接都会被判定为源变化。</item>
/// </list>
/// </summary>
internal sealed class WindowsStagingSourceHandle : IStagingSourceHandle
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private bool _disposed;

    internal WindowsStagingSourceHandle(string droppedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(droppedPath);

        var classification = StagingPathPolicy.Classify(droppedPath);
        if (classification.Kind != StagingPathKind.DriveAbsolute || StagingPathPolicy.HasTraversal(droppedPath))
        {
            throw new AttachmentStagingException(
                StagingCandidatePolicy.CodeForPathKind(classification.Kind),
                $"候选路径不是本机绝对路径：{classification.Detail}");
        }

        _path = droppedPath;
        _handle = WindowsStagingNative.OpenSourceNoFollow(droppedPath);
        try
        {
            var attributes = WindowsStagingNative.GetAttributes(_handle);
            var fileType = WindowsStagingNative.GetHandleFileType(_handle);
            if (!WindowsStagingNative.TryReadHandleIdentity(_handle, out var identity))
            {
                throw new AttachmentStagingException(
                    AttachmentStagingCodes.SourceUnavailable,
                    "无法读取源文件身份。",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            InitialIdentity = identity;
            Descriptor = new StagingSourceDescriptor
            {
                PathKind = classification.Kind,
                LeafName = Path.GetFileName(droppedPath),
                Kind = KindOf(attributes, fileType),
                Length = identity.Length,
                IsReparsePoint = (attributes & WindowsStagingNative.FileAttributeReparsePoint) != 0,
                IsCloudPlaceholder =
                    (attributes & (WindowsStagingNative.FileAttributeRecallOnOpen
                        | WindowsStagingNative.FileAttributeRecallOnDataAccess)) != 0,
                IsOffline = (attributes & WindowsStagingNative.FileAttributeOffline) != 0,
                IsNetworkShare = WindowsStagingNative.IsRemoteVolume(droppedPath),
            };
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public StagingSourceDescriptor Descriptor { get; }

    public StagingSourceIdentity InitialIdentity { get; }

    public int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WindowsStagingNative.ReadFromHandle(_handle, destination);
    }

    /// <summary>
    /// 复制后重新解析源路径：路径已消失返回 <see cref="StagingSourceIdentity.Missing"/>；
    /// 同名换文件返回新文件的身份（文件标识不同）；变成目录/链接则带重解析点标记。
    /// </summary>
    public StagingSourceIdentity ReadIdentity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WindowsStagingNative.TryReadPathIdentity(_path, out var identity)
            ? identity
            : StagingSourceIdentity.Missing;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    private static StagingSourceKind KindOf(uint attributes, uint fileType)
    {
        if ((attributes & WindowsStagingNative.FileAttributeDirectory) != 0)
        {
            return StagingSourceKind.Directory;
        }

        if ((attributes & WindowsStagingNative.FileAttributeDevice) != 0
            || fileType != WindowsStagingNative.FileTypeDisk)
        {
            return StagingSourceKind.Device;
        }

        return StagingSourceKind.RegularFile;
    }
}

/// <summary>
/// 原生捕获票据：只在<b>本程序集</b>内由原生粘贴处理器用
/// <see cref="NativePasteGesture"/> 铸造。候选路径保存在私有字段里，
/// 类型上没有任何公开的路径读取口，页面/桥只能拿到 <see cref="CaptureId"/>。
/// </summary>
internal sealed class WindowsNativeDropCapture : INativeStagingCapture
{
    private readonly string _droppedPath;

    internal WindowsNativeDropCapture(string captureId, string droppedPath)
    {
        CaptureId = captureId;
        _droppedPath = droppedPath;
    }

    public string CaptureId { get; }

    public string SuggestedLeafName => Path.GetFileName(_droppedPath);

    public IStagingSourceHandle OpenSource() => new WindowsStagingSourceHandle(_droppedPath);
}

/// <summary>
/// 原生粘贴手势令牌。构造函数是 <c>internal</c> 的：只有本程序集里的原生输入处理
/// （D16 的 STA 粘贴处理器）能铸造它，桥/页面代码无法构造，因此
/// <c>RegisterNativeCapture</c> 无法被"给一个路径"的方式调用。
/// </summary>
public sealed class NativePasteGesture
{
    private NativePasteGesture()
    {
    }

    /// <summary>由原生剪贴板手势处理器铸造（同程序集内部使用）。</summary>
    internal static NativePasteGesture MintFromNativePaste() => new();
}
