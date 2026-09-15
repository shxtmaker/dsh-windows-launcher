using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DshLauncher.Core.Attachments;

namespace DshLauncher.Platform.Windows.Attachments;

/// <summary>
/// D16 拖放手势的 Windows 侧入口：从<b>拖放操作自带的 OLE 数据对象</b>里读出 CF_HDROP 文件列表，
/// 并用同程序集铸造的手势令牌登记为原生捕获票据。
///
/// <b>无任意路径读取入口</b>：公开 API 接受的是 <see cref="IDataObject"/>（拖放动作的原生数据对象），
/// <b>不是</b>路径数组或路径字符串。路径只能在方法内部由 <c>IDataObject.GetData(CF_HDROP)</c> +
/// <c>DragQueryFileW</c> 得到；调用方无法用它读取任意位置的文件，页面/桥更无法构造 OLE 数据对象。
///
/// 真实行为（WPF 的 <c>DataObject</c> 是否能被强转成 COM <see cref="IDataObject"/>、
/// 拖放时 <c>GetData</c> 返回的 <c>STGMEDIUM</c> 形态、<c>ReleaseStgMedium</c> 的所有权）
/// 全部属 WindowsPending，见 <c>WindowsPendingCases.md</c>。
/// </summary>
public sealed unsafe class WindowsDroppedFilesSource
{
    /// <summary>拖放数据对象里没有可用的 CF_HDROP。</summary>
    public const string NoFileDropCode = "drop-no-file-list";

    /// <summary>拖放文件个数超过批内上限。</summary>
    public const string TooManyFilesCode = "limit-batch-files";

    /// <summary>拖放数据畸形（项无法解析）。</summary>
    public const string MalformedCode = "clipboard-malformed";

    private const int ErrorSuccess = 0;

    private readonly WindowsAttachmentStagingAdapter _staging;
    private readonly AttachmentLimits _limits;

    public WindowsDroppedFilesSource(WindowsAttachmentStagingAdapter staging, AttachmentLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(staging);
        _staging = staging;
        _limits = limits ?? new AttachmentLimits();
    }

    /// <summary>
    /// 读取拖放数据对象里的文件列表并登记捕获票据。失败返回确定的 <see cref="ClipboardReadResult"/>，
    /// 不抛异常（拖放回调里抛异常会直接毁掉整次拖放）。
    /// </summary>
    public ClipboardReadResult RegisterDroppedFiles(IDataObject? oleData, CancellationToken cancellationToken = default)
    {
        if (oleData is null)
        {
            return Failed(NoFileDropCode, "拖放数据对象为空");
        }

        var format = new FORMATETC
        {
            cfFormat = (short)WindowsClipboardNative.CfHDrop,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = nint.Zero,
            tymed = TYMED.TYMED_HGLOBAL,
        };

        if (oleData.QueryGetData(ref format) != ErrorSuccess)
        {
            return Failed(NoFileDropCode, "拖放数据对象不含 CF_HDROP");
        }

        STGMEDIUM medium;
        try
        {
            // ComTypes.IDataObject.GetData 在 HRESULT 失败时抛 COMException（无 PreserveSig）。
            oleData.GetData(ref format, out medium);
        }
        catch (COMException error)
        {
            return Failed(NoFileDropCode, "拖放数据对象拒绝提供 CF_HDROP：" + error.Message);
        }

        try
        {
            if (medium.unionmember == nint.Zero)
            {
                return Failed(MalformedCode, "拖放 CF_HDROP 句柄为空");
            }

            var count = (int)WindowsClipboardNative.DragQueryFileW(
                medium.unionmember,
                WindowsClipboardNative.DragQueryFileCount,
                null,
                0);
            if (count <= 0)
            {
                return Failed(NoFileDropCode, "拖放文件列表为空");
            }

            if (count > _limits.MaxFilesPerBatch)
            {
                return Failed(TooManyFilesCode, $"拖放 {count} 项超过批内上限 {_limits.MaxFilesPerBatch}");
            }

            var gesture = NativePasteGesture.MintFromNativePaste();
            var captures = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ReadDropFile(medium.unionmember, (uint)index);
                if (path is null)
                {
                    return Failed(MalformedCode, $"拖放第 {index} 项无法解析");
                }

                try
                {
                    captures.Add(_staging.RegisterNativeCapture(gesture, path));
                }
                catch (AttachmentStagingException error)
                {
                    return Failed(error.Code, error.Message);
                }
            }

            return new ClipboardReadResult
            {
                Ok = true,
                Kind = ClipboardSourceKind.FileList,
                CaptureIds = captures,
                FileCount = captures.Count,
                ReadOffSta = true,
            };
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static string? ReadDropFile(nint dropHandle, uint index)
    {
        var length = (int)WindowsClipboardNative.DragQueryFileW(dropHandle, index, null, 0);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new char[length + 1];
        unsafe
        {
            fixed (char* pointer = buffer)
            {
                var written = WindowsClipboardNative.DragQueryFileW(dropHandle, index, pointer, (uint)buffer.Length);
                return written == 0 ? null : new string(buffer, 0, (int)written);
            }
        }
    }

    // STGMEDIUM 不在 LibraryImport 支持的封送类型里（SYSLIB1051），因此这里保留 DllImport
    // 并显式抑制"改用 LibraryImport"的建议：所有权语义必须由 ole32 自己释放。
    [DllImport("ole32.dll")]
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time",
        Justification = "STGMEDIUM 不受源生成封送支持，必须走运行时分派。")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    private static ClipboardReadResult Failed(string code, string detail) =>
        new()
        {
            Ok = false,
            Code = code,
            Detail = detail,
            Kind = ClipboardSourceKind.FileList,
            ReadOffSta = true,
        };
}
