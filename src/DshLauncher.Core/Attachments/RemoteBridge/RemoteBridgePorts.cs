using System.Diagnostics.CodeAnalysis;
using DshLauncher.Core.Remote;

namespace DshLauncher.Core.Attachments;

/// <summary>一次通道发送的结局：失败必须带确定码（绝不静默丢帧）。</summary>
public readonly record struct RemoteBridgeSendOutcome(bool Sent, string? Code, string? Detail)
{
    /// <summary>已交给页面通道。</summary>
    public static RemoteBridgeSendOutcome Ok() => new(true, null, null);

    /// <summary>通道拒绝发送。</summary>
    public static RemoteBridgeSendOutcome Fail(string code, string detail) => new(false, code, detail);
}

/// <summary>
/// D17 <b>生产通道适配接口</b>：一条线协议帧的发送、接收与失败上报。
///
/// 契约（Linux 侧由 <c>RemoteBridgeChannelTests</c> 逐条断言）：
/// <list type="bullet">
/// <item>交换的是<b>封包</b>文本（<see cref="RemoteBridgeEnvelopeCodec"/>），适配器不做线协议判定；</item>
/// <item><see cref="SendFrame"/> 发出一条完整封包后立即返回，不等待对端；失败返回确定码，
/// 绝不抛异常给调用方（WebView2 的 COM/线程异常也必须转成码）；</item>
/// <item><see cref="TryReceiveFrame"/> 非阻塞取一条入站封包，没有则返回 false；</item>
/// <item>入站队列有硬上界：满了必须<b>拒绝新帧并上报</b>，不得无界增长；</item>
/// <item><see cref="Close"/> 释放监听与队列，可重复调用；关闭后 <see cref="SendFrame"/> 一律失败。</item>
/// </list>
/// 生产实现是 <c>DshLauncher.Desktop.Attachments.WebViewMessageTransport</c>（真实 WebView2 WebMessage）。
/// </summary>
public interface IRemoteBridgeTransport : IDisposable
{
    /// <summary>本页面专属的通道 id（必须与页面会话的通道一致，桥会再次核对）。</summary>
    string ChannelId { get; }

    /// <summary>适配器是否已释放（监听已解除、队列已清空）。</summary>
    bool IsClosed { get; }

    /// <summary>最近一次适配器级失败码（没有失败时为 <c>null</c>）。</summary>
    string? LastFailureCode { get; }

    /// <summary>最近一次适配器级失败说明。</summary>
    string? LastFailureDetail { get; }

    /// <summary>发送一条封包。</summary>
    RemoteBridgeSendOutcome SendFrame(string envelopeJson);

    /// <summary>非阻塞取一条入站封包（没有待处理帧时返回 false，绝不阻塞）。</summary>
    bool TryReceiveFrame([NotNullWhen(true)] out string? envelopeJson);

    /// <summary>释放监听与队列（可重复调用，不抛异常）。</summary>
    void Close();
}

/// <summary>
/// D17 暂存端口：把 D16 的一次性捕获票据变成 D14 的受控快照，并把快照打开成
/// <see cref="IAttachmentByteSource"/> 交给 D11 协调器。
///
/// <b>无任意路径入口</b>：四个成员只接受不透明 id（captureId / snapshotId / batchId），
/// 因此组合层与页面都拿不到本机路径。生产实现是 Desktop 的 <c>StagingAdapterPort</c>
/// （底层是 <c>WindowsAttachmentStagingAdapter</c> + 真实 Win32 只读句柄）。
/// </summary>
public interface IRemoteStagingPort
{
    /// <summary>开始一个暂存批次（与线上批次一一对应）。</summary>
    AttachmentCoordinatorResult BeginBatch(string batchId, int fileCount);

    /// <summary>消费一个一次性捕获票据做快照；失败返回确定码，不抛异常。</summary>
    StagingCaptureReceipt Capture(string captureId, CancellationToken cancellationToken = default);

    /// <summary>按快照 id 打开只读字节源；打不开时抛 <see cref="AttachmentStagingException"/>。</summary>
    IAttachmentByteSource OpenSnapshot(string snapshotId);

    /// <summary>释放一个快照（只接受台账 id）。</summary>
    StagingCleanupResult Release(string snapshotId, CancellationToken cancellationToken = default);
}

/// <summary>
/// D17 握手消费者端口：把真实解析出来的 <c>capabilities</c>/<c>context</c> 事实交给
/// <b>真正的消费者</b>——截图所有者台账（决定 native-paste / bridge）与 composer 上下文台账
/// （决定导入闸门）。桌面实现直接转发到 D16 的
/// <c>RemoteWindow.ReportScreenshotHandshake</c> / <c>ReportComposerContext</c>。
/// </summary>
public interface IRemoteBridgeHandshakeSink
{
    /// <summary>记录一次截图所有者握手结论（按页面代际绑定）。</summary>
    ScreenshotOwnerDecision RecordScreenshotOwner(Guid targetId, RemotePageEpoch epoch, ScreenshotOwnerFacts facts);

    /// <summary>记录一次 composer 上下文声明；<paramref name="pageAdmitted"/> 必须来自页面准入结果。</summary>
    bool RecordComposerContext(Guid targetId, RemotePageEpoch epoch, ImportContextFacts facts, bool pageAdmitted);
}

/// <summary>D17 桥的构造选项（全部有界且有缺省）。</summary>
public sealed record RemoteBridgeOptions
{
    /// <summary>宿主 <c>hello.clientBuild</c>（必须匹配冻结的 <c>BuildIdPattern</c>）。</summary>
    public string ClientBuild { get; init; } = "DshWindowsLauncher/remote-attachments-1";

    /// <summary>
    /// 宿主原生采集是否可用（D16：暂存适配器与剪贴板通路是否都接上）。
    /// 不可用时截图所有者恒为 bridge——宿主绝不抢图片粘贴。
    /// </summary>
    public bool NativeCaptureAvailable { get; init; } = true;

    /// <summary>宿主生效限额（本身不得超过冻结上限）。</summary>
    public AttachmentLimits Limits { get; init; } = new();
}
