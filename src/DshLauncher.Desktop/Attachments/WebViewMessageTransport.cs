using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using DshLauncher.Core.Attachments;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher.Desktop.Attachments;

/// <summary>
/// D17 <b>生产 WebView2 WebMessage 通道适配器</b>：实现可移植的
/// <see cref="IRemoteBridgeTransport"/>，把封包文本送进/取出真实 WebView2 页面。
///
/// 真实事实与边界（属 WindowsPending，见
/// <c>RemoteAttachmentBridge.WindowsPending.md</c> 的 WB-01…WB-06）：
/// <list type="bullet">
/// <item>发送用 <see cref="CoreWebView2.PostWebMessageAsJson(string)"/>，接收用
/// <c>CoreWebView2.WebMessageReceived</c> 的 <c>WebMessageAsJson</c>；两种投递形态
/// （对象 与 <c>JSON.stringify</c> 后的字符串）都由 <see cref="RemoteBridgeEnvelopeCodec.NormalizePayload"/> 归一；</item>
/// <item>WebView2 的投递必须在拥有该控件的 UI 线程上发生；跨线程调用会抛
/// <see cref="InvalidOperationException"/>/<see cref="COMException"/>，本适配器一律转成确定失败码，
/// 绝不把异常抛给传输状态机；</item>
/// <item>入站队列有硬上界（<see cref="MaxInboundQueue"/>）：满了记录失败并丢弃新帧，内存绝不无界增长；</item>
/// <item><see cref="Close"/> 解除 <c>WebMessageReceived</c> 订阅、清空队列并标记关闭（幂等）。</item>
/// </list>
/// </summary>
public sealed class WebViewMessageTransport : IRemoteBridgeTransport
{
    /// <summary>入站队列硬上界。</summary>
    public const int MaxInboundQueue = RemoteBridgeComposition.MaxInboundQueue;

    private readonly CoreWebView2 _core;
    private readonly object _gate = new();
    private readonly Queue<string> _inbound = new();
    private bool _closed;

    /// <summary>为一个页面通道建立适配器；通道 id 必须等于页面会话的通道 id。</summary>
    public WebViewMessageTransport(CoreWebView2 core, string channelId)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        _core = core;
        ChannelId = channelId;
        core.WebMessageReceived += OnWebMessageReceived;
    }

    public string ChannelId { get; }

    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    public string? LastFailureCode { get; private set; }

    public string? LastFailureDetail { get; private set; }

    /// <summary>队列中有待处理封包时触发（在 WebView2 的 UI 线程上）；组合层据此立即排空。</summary>
    public event Action? FrameQueued;

    public RemoteBridgeSendOutcome SendFrame(string envelopeJson)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        if (IsClosed)
        {
            return RemoteBridgeSendOutcome.Fail(RemoteBridgeCodes.ChannelUnavailable, "通道适配器已关闭");
        }

        if (Encoding.UTF8.GetByteCount(envelopeJson) > RemoteBridgeEnvelopeCodec.MaxEnvelopeBytes)
        {
            RecordFailure("message-too-large", "出站封包超过封包字节上界");
            return RemoteBridgeSendOutcome.Fail("message-too-large", "出站封包超过封包字节上界");
        }

        try
        {
            _core.PostWebMessageAsJson(envelopeJson);
            return RemoteBridgeSendOutcome.Ok();
        }
        catch (Exception error) when (error is InvalidOperationException
            or COMException
            or ObjectDisposedException
            or ArgumentException)
        {
            RecordFailure(RemoteBridgeCodes.ChannelUnavailable, error.Message);
            return RemoteBridgeSendOutcome.Fail(RemoteBridgeCodes.ChannelUnavailable, error.Message);
        }
    }

    public bool TryReceiveFrame([NotNullWhen(true)] out string? envelopeJson)
    {
        lock (_gate)
        {
            if (_inbound.Count == 0)
            {
                envelopeJson = null;
                return false;
            }

            envelopeJson = _inbound.Dequeue();
            return true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _inbound.Clear();
        }

        _core.WebMessageReceived -= OnWebMessageReceived;
    }

    public void Dispose() => Close();

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try
        {
            raw = e.WebMessageAsJson;
        }
        catch (Exception error) when (error is InvalidOperationException or COMException or ObjectDisposedException)
        {
            RecordFailure(RemoteBridgeCodes.EnvelopeInvalid, error.Message);
            return;
        }

        var normalized = RemoteBridgeEnvelopeCodec.NormalizePayload(raw);
        if (normalized is null)
        {
            RecordFailure("malformed-json", "入站 WebMessage 不是合法封包文本");
            return;
        }

        if (Encoding.UTF8.GetByteCount(normalized) > RemoteBridgeEnvelopeCodec.MaxEnvelopeBytes)
        {
            RecordFailure("message-too-large", "入站封包超过封包字节上界：丢弃");
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (_inbound.Count >= MaxInboundQueue)
            {
                RecordFailureLocked(RemoteBridgeCodes.InboundOverflow, "入站队列已满：丢弃本帧");
                return;
            }

            _inbound.Enqueue(normalized);
        }

        FrameQueued?.Invoke();
    }

    private void RecordFailure(string code, string detail)
    {
        lock (_gate)
        {
            RecordFailureLocked(code, detail);
        }
    }

    private void RecordFailureLocked(string code, string detail)
    {
        LastFailureCode = code;
        LastFailureDetail = detail;
    }
}
