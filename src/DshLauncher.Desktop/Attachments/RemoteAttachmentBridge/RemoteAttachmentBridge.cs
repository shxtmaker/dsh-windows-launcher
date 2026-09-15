using System.Windows.Threading;
using DshLauncher.Core.Attachments;
using DshLauncher.Desktop.Remote;
using DshLauncher.Platform.Windows.Attachments;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher.Desktop.Attachments;

/// <summary>
/// D17 <b>生产组合根</b>：一个目标页面一个实例，把
/// 原生输入（D16 手势 → 捕获票据）→ 受控暂存（D14）→ 生产 Core 传输（D10/D11）→
/// WebView2 WebMessage 通道（<see cref="WebViewMessageTransport"/>）接成一条完整链路。
///
/// 生命周期归属：本类是 <see cref="RemotePageSession"/> 的<b>组件</b>，不是第二个生命周期。
/// 页面会话在初始化完成后调用 <see cref="AttachTo"/>，在导航/能力变化/关闭/释放时分别调用
/// <see cref="OnPageAdvanced"/>、<see cref="OnCapabilityLost"/>、<see cref="Close"/>、<see cref="Dispose"/>；
/// 判定规则全部在可移植的 <see cref="RemoteBridgeComposition"/> 里（Linux 已测），
/// 这里只做三件真实系统的事：
/// <list type="number">
/// <item>把 WebView2 的 WebMessage 事件接成通道适配器（并把它挂到 UI 线程）；</item>
/// <item>用一个 <see cref="DispatcherTimer"/> 驱动超时判定（批次结束/取消/失败/关闭一律停表）；</item>
/// <item>把原生手势结局（<see cref="NativePasteOutcome"/>）翻译成一次批次请求。</item>
/// </list>
/// 本类<b>不</b>引用配对、心跳或目标库：关闭桥在任何路径上都不会停止保活。
/// 真实 WebMessage 投递与事件顺序属 WindowsPending（见
/// <c>RemoteAttachmentBridge.WindowsPending.md</c>）。
/// </summary>
public sealed class RemoteAttachmentBridge : IDisposable
{
    /// <summary>宿主 <c>hello.clientBuild</c>（匹配冻结 <c>BuildIdPattern</c>，≤64 字符）。</summary>
    public const string DesktopClientBuild = "DshWindowsLauncher.Desktop/remote-attachments-1";

    /// <summary>超时轮询间隔（毫秒）：只驱动协调器的确定性超时判定，不引入 sleep。</summary>
    public const int PumpIntervalMs = 500;

    private readonly RemotePageSession _page;
    private readonly WindowsAttachmentStagingAdapter _staging;
    private readonly IRemoteBridgeHandshakeSink _sink;
    private readonly RemoteBridgeOptions _options;
    private readonly AttachmentConcurrencyGate _gate;
    private WebViewMessageTransport? _transport;
    private RemoteBridgeComposition? _composition;
    private DispatcherTimer? _pumpTimer;
    private bool _reportedTerminal;
    private bool _disposed;

    public RemoteAttachmentBridge(
        RemotePageSession page,
        WindowsAttachmentStagingAdapter staging,
        IRemoteBridgeHandshakeSink sink,
        AttachmentConcurrencyGate? gate = null,
        bool nativeCaptureAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(sink);
        _page = page;
        _staging = staging;
        _sink = sink;
        _gate = gate ?? new AttachmentConcurrencyGate();
        _options = new RemoteBridgeOptions
        {
            ClientBuild = DesktopClientBuild,
            NativeCaptureAvailable = nativeCaptureAvailable,
        };
    }

    /// <summary>可移植组合根（尚未接通道时为 <c>null</c>）。</summary>
    public RemoteBridgeComposition? Composition => _composition;

    /// <summary>通道适配器是否已接上（WebView2 已初始化）。</summary>
    public bool IsAttached => _composition?.IsAttached ?? false;

    /// <summary>最近一次批次开始的结果。</summary>
    public RemoteBridgeBatchResult? LastBatch { get; private set; }

    /// <summary>最近一次传输状态文字（窗口据此提示用户）；从未传输时为 <c>null</c>。</summary>
    public string? LastStatusText { get; private set; }

    /// <summary>最近一次传输状态码（确定码；成功完成时为 <c>staged</c>）。</summary>
    public string? LastStatusCode { get; private set; }

    /// <summary>状态变化（窗口顶栏显示）；在 UI 线程上触发。</summary>
    public event Action<RemoteAttachmentBridge>? StatusChanged;

    /// <summary>
    /// 把 WebView2 的真实 CoreWebView2 接上：建立通道适配器与组合根，订阅入站帧。
    /// 重复调用是幂等的；已释放/已关闭时返回 <c>false</c>（确定拒绝，不抛异常）。
    /// </summary>
    public bool AttachTo(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        if (_disposed || _composition is { IsUsable: false })
        {
            SetStatus(RemoteBridgeCodes.NotAttached, "附件桥不可用：页面会话已关闭");
            return false;
        }

        if (_composition is not null)
        {
            return _composition.IsAttached;
        }

        var composition = new RemoteBridgeComposition(
            _page.TargetId,
            _page.Lifecycle,
            new StagingAdapterPort(_staging),
            _sink,
            _options,
            _gate);
        var transport = new WebViewMessageTransport(core, composition.ChannelId);
        if (!composition.AttachTransport(transport))
        {
            transport.Dispose();
            SetStatus(composition.Rejections[^1].Code, "附件桥拒绝接入通道适配器");
            return false;
        }

        transport.FrameQueued += OnFrameQueued;
        _transport = transport;
        _composition = composition;
        return true;
    }

    /// <summary>主文档已验证且能力已授予：开始宿主握手（幂等）。</summary>
    public void OnMainDocumentLoaded()
    {
        var composition = _composition;
        if (composition is null || !composition.IsUsable)
        {
            return;
        }

        var handshake = composition.StartHandshake();
        if (!handshake.Sent)
        {
            // 握手发不出去必须如实上报（确定码），不能假装已经握手。
            SetStatus(handshake.Code ?? RemoteBridgeCodes.ChannelUnavailable, handshake.Detail ?? "宿主握手发送失败");
        }

        DrainInbound();
    }

    /// <summary>能力丢失（来源不符/外链/未加载完）：不再接收新批次并释放本代际资源。</summary>
    public void OnCapabilityLost() => _composition?.OnCapabilityLost();

    /// <summary>页面导航：静默作废本代际（不发帧），释放协调器、字节源与暂存快照。</summary>
    public void OnPageAdvanced()
    {
        _composition?.OnPageAdvanced();
        StopPumpTimer();
        _reportedTerminal = false;
    }

    /// <summary>
    /// 一次原生手势的结局进入组合链路：只有"原生通路已产生捕获票据"才开批次；
    /// 其余结局（纯文本、交给桥、拒绝）在这里不做任何事——它们不属于附件桥。
    /// </summary>
    public RemoteBridgeBatchResult StartTransfer(NativePasteOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var composition = _composition;
        if (composition is null)
        {
            var refused = new RemoteBridgeBatchResult
            {
                Ok = false,
                Code = RemoteBridgeCodes.NotAttached,
                Detail = "附件桥尚未接入（WebView2 未初始化）",
            };
            LastBatch = refused;
            SetStatus(refused.Code!, refused.Detail!);
            return refused;
        }

        if (!outcome.Accepted || outcome.Route != ClipboardImportRoute.NativePaste || outcome.CaptureIds.Count == 0)
        {
            var skipped = new RemoteBridgeBatchResult
            {
                Ok = false,
                Code = outcome.Code,
                Detail = "本次手势没有原生捕获票据：不进入附件桥",
                FileCount = outcome.FileCount,
            };
            LastBatch = skipped;
            return skipped;
        }

        _reportedTerminal = false;
        var result = composition.StartBatch(new RemoteBridgeBatchRequest(
            "batch-" + Guid.NewGuid().ToString("N"),
            outcome.CaptureIds));
        LastBatch = result;
        if (!result.Ok)
        {
            SetStatus(result.Code!, result.Detail ?? "批次未开始");
            return result;
        }

        var report = composition.Pump();
        SetStatus(
            "transfer-started",
            $"正在发送 {result.FileCount} 个附件（{result.TotalBytes} 字节）");
        if (report.Stop is null)
        {
            StartPumpTimer();
        }

        return result;
    }

    /// <summary>主动取消：发出 cancel、停止传输、释放源与暂存快照，迟到结果一律拒绝。</summary>
    public void CancelTransfer(string reason = "cancelled")
    {
        var composition = _composition;
        if (composition is null)
        {
            return;
        }

        var result = composition.CancelBatch(reason);
        StopPumpTimer();
        SetStatus(result.Code ?? "cancelled", result.Ok ? "已取消本次附件传输" : "没有可取消的批次");
    }

    /// <summary>终止：取消在途批次、释放全部桥资源并停止定时器（幂等；不触碰配对/心跳）。</summary>
    public void Close(string code)
    {
        StopPumpTimer();
        if (_transport is not null)
        {
            _transport.FrameQueued -= OnFrameQueued;
        }

        _composition?.Close(code);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close(RemoteBridgeCodes.Disposed);
        _composition?.Dispose();
        _composition = null;
        _transport?.Dispose();
        _transport = null;
    }

    private void OnFrameQueued()
    {
        // WebView2 在 UI 线程上回调；这里立即排空，避免消息堆在队列里。
        DrainInbound();
    }

    private void DrainInbound()
    {
        var composition = _composition;
        if (composition is null)
        {
            return;
        }

        composition.DrainTransport();
        ReportTerminal();
        UpdatePumpTimer();
    }

    private void OnPumpTick(object? sender, EventArgs e)
    {
        var composition = _composition;
        if (composition is null)
        {
            StopPumpTimer();
            return;
        }

        composition.PumpIdle();
        ReportTerminal();
        UpdatePumpTimer();
    }

    private void StartPumpTimer()
    {
        var timer = _pumpTimer;
        if (timer is null)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PumpIntervalMs),
            };
            timer.Tick += OnPumpTick;
            _pumpTimer = timer;
        }

        if (!timer.IsEnabled)
        {
            timer.Start();
        }
    }

    private void StopPumpTimer()
    {
        var timer = _pumpTimer;
        if (timer is { IsEnabled: true })
        {
            timer.Stop();
        }
    }

    private void UpdatePumpTimer()
    {
        var coordinator = _composition?.Coordinator;
        if (coordinator is null || coordinator.BatchPhase != AttachmentBatchPhase.Open)
        {
            StopPumpTimer();
            return;
        }

        StartPumpTimer();
    }

    /// <summary>批次进入终态时给出一次确定的状态文字，并停表（每个退出路径都释放定时器）。</summary>
    private void ReportTerminal()
    {
        var composition = _composition;
        var coordinator = composition?.Coordinator;
        if (composition is null || coordinator is null || _reportedTerminal)
        {
            return;
        }

        switch (coordinator.BatchPhase)
        {
            case AttachmentBatchPhase.Closed:
                _reportedTerminal = true;
                var staged = coordinator.Files.Count(file => file.Phase == AttachmentFilePhase.Staged);
                var failed = coordinator.Files.Count(file => file.Phase is AttachmentFilePhase.Failed or AttachmentFilePhase.Partial);
                if (failed == 0)
                {
                    SetStatus("staged", $"已发送 {staged} 个附件，等待页面草稿确认");
                }
                else
                {
                    SetStatus("partial-import", $"{staged} 个成功、{failed} 个失败（可在页面重试失败项）");
                }

                StopPumpTimer();
                break;
            case AttachmentBatchPhase.Cancelled:
                _reportedTerminal = true;
                SetStatus("cancelled", "附件传输已取消");
                StopPumpTimer();
                break;
            default:
                break;
        }
    }

    private void SetStatus(string code, string detail)
    {
        LastStatusCode = code;
        LastStatusText = detail;
        StatusChanged?.Invoke(this);
    }
}
