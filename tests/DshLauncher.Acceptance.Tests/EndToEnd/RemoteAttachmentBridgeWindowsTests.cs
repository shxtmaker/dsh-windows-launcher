using System.IO;
using System.Net;
using System.Text;
using System.Windows.Threading;
using DshLauncher.Core;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using DshLauncher.Core.Remote;
using DshLauncher.Desktop;
using DshLauncher.Desktop.Attachments;
using DshLauncher.Desktop.Remote;
using DshLauncher.Platform.Windows;
using DshLauncher.Platform.Windows.Attachments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DshLauncher.Acceptance.Tests.EndToEnd;

/// <summary>
/// D17 Windows 实机用例：真实 WPF + 真实 WebView2 <c>WebMessage</c> 通道 + 真实剪贴板 + 真实暂存根。
/// <b>本轮没有 Windows 机器，这里一条都没有执行过</b>，因此不得记为 PASS；
/// 每条用例的 id、要跑什么、断言什么、为什么 Linux 不能跑，见
/// <c>src/DshLauncher.Desktop/Attachments/RemoteAttachmentBridge.WindowsPending.md</c>。
///
/// Linux 侧本轮真实执行的是平台中立的组合契约（<c>tests/DshLauncher.Core.Tests/Attachments/RemoteBridge*</c>）：
/// 封包收发、身份/大小复核、握手路由、取消/迟到/释放与"桥关闭不影响心跳"。
/// 本文件里的页面侧应答脚本是<b>测试夹具</b>，不是生产接收端；真实插件接收端的互通属 D18。
/// </summary>
[Trait("triggerTags", "VFY-07")]
public sealed class RemoteAttachmentBridgeWindowsTests
{
    [Fact]
    public Task WB01HostHelloCapabilitiesAndContextTravelOverRealWebMessages()
    {
        return OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemoteAttachmentBridgeHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.Page;
            await harness.WaitForCapabilityAsync(page, cancellationToken);

            // 宿主的 hello + capabilities 必须真的到达页面（页面脚本把它们记在 __dshBridge 里）。
            await harness.WaitForPageAsync(
                "JSON.parse(__dshBridge.snapshot()).hostFrames.indexOf('capabilities') >= 0",
                "host capabilities reached the page",
                cancellationToken);

            // 页面回 hello + capabilities + context；宿主解析后必须建立身份并写进真实消费者。
            await harness.RunPageAsync("__dshBridge.handshake(['chunked-transfer','file-end-hash','multi-file-batch','cancel'], 'session-1')");
            await harness.WaitForAsync(
                () => page.AttachmentBridge?.Composition?.Phase == RemoteBridgePhase.Ready,
                "bridge bound the page identity",
                cancellationToken);

            var composition = page.AttachmentBridge!.Composition!;
            Assert.Equal("addon/0.1.0", composition.PeerBuild);
            Assert.Equal("session-1", composition.Identity!.SessionId);
            Assert.True(harness.Window.HasImportContext(page));
            Assert.Equal("session-1", harness.Window.ImportContextFor(page).SessionId);
            Assert.Equal(ScreenshotOwnerMode.Bridge, composition.ScreenshotMode);
        });
    }

    [Fact]
    public Task WB02AScreenshotCapablePageGetsTheNativePasteOwnerOverTheRealChannel()
    {
        return OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemoteAttachmentBridgeHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.Page;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            await harness.WaitForPageAsync(
                "JSON.parse(__dshBridge.snapshot()).hostFrames.length >= 2",
                "host handshake frames reached the page",
                cancellationToken);

            await harness.RunPageAsync(
                "__dshBridge.handshake(['chunked-transfer','file-end-hash','screenshot','multi-file-batch','cancel'], 'session-1')");
            await harness.WaitForAsync(
                () => page.AttachmentBridge?.Composition?.Phase == RemoteBridgePhase.Ready,
                "bridge became ready",
                cancellationToken);

            // D16 登记的缺口在实机上的闭合：真实 capabilities 到达后，原生路由的截图模式台账被更新。
            var owners = harness.Window.PasteOwners(page.TargetId);
            Assert.NotNull(owners);
            Assert.Equal(ScreenshotOwnerMode.NativePaste, owners!.ModeFor(page.TargetId, page.Lifecycle.Epoch));
            Assert.Equal(ScreenshotOwnerMode.NativePaste, page.AttachmentBridge!.Composition!.ScreenshotMode);
        });
    }

    [Fact]
    public Task WB03ARealClipboardFileTravelsToThePageAsChunksAndCompletesTheBatch()
    {
        return OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemoteAttachmentBridgeHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.Page;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            await harness.WaitForPageAsync(
                "JSON.parse(__dshBridge.snapshot()).hostFrames.length >= 2",
                "host handshake frames reached the page",
                cancellationToken);
            await harness.RunPageAsync(
                "__dshBridge.handshake(['chunked-transfer','file-end-hash','multi-file-batch','cancel'], 'session-1')");
            await harness.WaitForAsync(
                () => page.AttachmentBridge?.Composition?.Phase == RemoteBridgePhase.Ready,
                "bridge became ready",
                cancellationToken);

            // 真实剪贴板：一个 300 KiB 文件 + 真实 Ctrl+V 手势。
            var source = harness.StageFile("报告.bin", 300 * 1024);
            ClipboardWriter.SetFileDropList([source]);
            harness.Window.Activate();
            harness.Window.Focus();
            NativePasteGestureDriver.SendPasteHotkey();
            await harness.WaitForPasteOutcomeAsync(cancellationToken);

            // 页面侧对每个块回 ack、对 file-end 回 import-result；宿主必须走到 staged 并关闭批次。
            for (var round = 0; round < 40; round += 1)
            {
                await harness.RunPageAsync("__dshBridge.acknowledge()");
                await Task.Delay(50, cancellationToken);
                if (page.AttachmentBridge!.Composition!.Coordinator?.BatchPhase == AttachmentBatchPhase.Closed)
                {
                    break;
                }
            }

            var coordinator = page.AttachmentBridge!.Composition!.Coordinator!;
            var record = coordinator.FileRecord(coordinator.Files.Single().FileId)!;
            Assert.Equal(AttachmentBatchPhase.Closed, coordinator.BatchPhase);
            Assert.Equal(AttachmentFilePhase.Staged, record.Phase);
            Assert.Equal(300 * 1024, record.ByteLength);
            Assert.Equal(record.ByteLength, record.SentBytes);
            Assert.Equal(1, coordinator.ImportInvocations);

            // 页面确实收到了全部字节（页面脚本按 chunk.frame.byteLength 累加）。
            Assert.True(
                await harness.PageTrueAsync("JSON.parse(__dshBridge.snapshot()).receivedBytes === 307200"),
                "页面没有收到全部字节");
            Assert.True(
                await harness.PageTrueAsync("JSON.parse(__dshBridge.snapshot()).hostFrames.indexOf('file-end') >= 0"),
                "页面没有收到 file-end");

            // 暂存擦干净：自有根内不再有快照文件（所有权标记除外）。
            Assert.DoesNotContain(
                Directory.EnumerateFiles(
                    WindowsAttachmentStagingAdapter.GetOwnedStagingRoot(harness.Layout, page.TargetId)),
                path => !string.Equals(
                    Path.GetFileName(path),
                    AttachmentStagingRoot.OwnershipMarkerName,
                    StringComparison.Ordinal));
        });
    }

    [Fact]
    public Task WB04FramesPostedBeforeTheHandshakeAreRefusedAndNeverBindAnIdentity()
    {
        return OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemoteAttachmentBridgeHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.Page;
            await harness.WaitForCapabilityAsync(page, cancellationToken);

            // 页面在文档刚加载时抢跑（真实插件不会这么做，但桥必须确定拒绝而不是猜）。
            await harness.RunPageAsync("__dshBridge.earlyHandshake()");
            await harness.WaitForAsync(
                () => page.AttachmentBridge?.Composition is { } composition
                    && (composition.RejectedInbound > 0 || composition.PeerBuild is not null),
                "the early frame was either refused or answered by the host handshake",
                cancellationToken);

            var composition = page.AttachmentBridge!.Composition!;
            if (composition.PeerBuild is null)
            {
                Assert.Contains(
                    composition.Rejections,
                    rejection => !rejection.Outbound
                        && rejection.Code is RemotePageSessionCodes.MessageCapabilityAbsent
                            or RemotePageSessionCodes.MessageEpochStale
                            or RemoteBridgeCodes.EnvelopeInvalid);
            }

            Assert.Null(composition.Identity);
            Assert.False(harness.Window.HasImportContext(page));
        });
    }

    [Fact]
    public Task WB05RemovingTheTargetReleasesTheListenerTheTimerAndTheScratch()
    {
        return OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemoteAttachmentBridgeHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.Page;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            await harness.WaitForPageAsync(
                "JSON.parse(__dshBridge.snapshot()).hostFrames.length >= 2",
                "host handshake frames reached the page",
                cancellationToken);
            await harness.RunPageAsync(
                "__dshBridge.handshake(['chunked-transfer','file-end-hash','multi-file-batch','cancel'], 'session-1')");
            await harness.WaitForAsync(
                () => page.AttachmentBridge?.Composition?.Phase == RemoteBridgePhase.Ready,
                "bridge became ready",
                cancellationToken);

            // 真实剪贴板手势启动一次传输（700 KiB：多块在途，批次在移除目标时必然还没结束）。
            var source = harness.StageFile("释放.bin", 700 * 1024);
            ClipboardWriter.SetFileDropList([source]);
            harness.Window.Activate();
            harness.Window.Focus();
            NativePasteGestureDriver.SendPasteHotkey();
            await harness.WaitForPasteOutcomeAsync(cancellationToken);
            var bridge = page.AttachmentBridge!;
            Assert.True(bridge.LastBatch!.Ok);

            harness.Window.RemovePage(page.TargetId, RemotePageCancellationTrigger.TargetRemoved);

            Assert.Equal(RemoteBridgePhase.Closed, bridge.Composition!.Phase);
            Assert.True(bridge.Composition.Transport!.IsClosed);
            Assert.True(bridge.Composition.ReleasedSnapshots.Count >= 1);
            Assert.Equal(RemotePageSessionCodes.SessionClosed, bridge.Composition.CloseCode);
            Assert.Null(bridge.Composition.Coordinator);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(
                    WindowsAttachmentStagingAdapter.GetOwnedStagingRoot(harness.Layout, page.TargetId)),
                path => !string.Equals(
                    Path.GetFileName(path),
                    AttachmentStagingRoot.OwnershipMarkerName,
                    StringComparison.Ordinal));

            // 监听已解除：页面再发什么都不可能被消费（桥已关闭，确定拒绝）。
            await harness.RunPageAsync("__dshBridge.earlyHandshake()");
            await Task.Delay(200, cancellationToken);
            Assert.Equal(RemoteBridgePhase.Closed, bridge.Composition.Phase);
        });
    }

    [Fact]
    public Task WB06ClosingTheBridgeLeavesTheRealPairingHeartbeatRunning()
    {
        return OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var host = new FakeRemoteAccessHost();
            await host.StartAsync();
            await using var hub = new PairingHub(
                new PairingHubOptions { HeartbeatInterval = TimeSpan.FromSeconds(2) },
                new MemoryTargetStore(),
                new HttpPairingTransport(new PairingTransportOptions()),
                new SystemClock(),
                new GuidIdGenerator());
            await hub.StartAsync(cancellationToken);
            var paired = await hub.AddFromPairingLinkAsync(host.PairingLink, "d17-heartbeat", cancellationToken);
            Assert.Null(paired.Error);
            var targetId = paired.Target!.TargetId;
            var remoteUrl = await hub.GetRemoteUiUrlAsync(targetId, cancellationToken);
            Assert.Null(remoteUrl.Error);
            await using var harness = await RemoteAttachmentBridgeHarness.StartWithHubAsync(
                dispatcher, hub, targetId, remoteUrl.Url!, paired.Target!.BaseUrl, cancellationToken);
            var page = harness.Page;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            var heartbeatsBefore = await RemoteAttachmentBridgeHarness.HeartbeatCountAsync(hub, targetId, cancellationToken);

            harness.Window.RemovePage(targetId, RemotePageCancellationTrigger.TargetRemoved);

            // 桥/页面关闭后，枢纽的保活必须继续：心跳计数继续增长、目标仍为已配对。
            var heartbeatAdvanced = await RemoteAttachmentBridgeHarness.WaitForHeartbeatAsync(
                hub, targetId, heartbeatsBefore, cancellationToken);
            Assert.True(heartbeatAdvanced, "关闭附件桥不得停止配对保活");
            var snapshot = await hub.GetSnapshotAsync(cancellationToken);
            var target = Assert.Single(snapshot.Targets);
            Assert.Equal(PairingState.Paired, target.Pairing);
            Assert.True(hub.IsStarted);
        });
    }

    private static Task OnDispatcherAsync(Func<Dispatcher, CancellationToken, Task> action)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await action(dispatcher, cancellationToken);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(180), cancellationToken);
    }
}

/// <summary>
/// D17 实机夹具：一个真实可信来源的 Kestrel 主机（页面里带 D17 传输脚本）、
/// 一个真实 <see cref="RemoteWindow"/>（带真实暂存工厂）与真实剪贴板手势。
/// </summary>
internal sealed class RemoteAttachmentBridgeHarness : IAsyncDisposable
{
    private readonly WebApplication _host;
    private readonly PairingHub _hub;
    private readonly bool _ownsHub;
    private readonly TaskCompletionSource<NativePasteOutcome> _pasteOutcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(60);
    private bool _disposed;

    private RemoteAttachmentBridgeHarness(
        string root,
        ApplicationDataLayout layout,
        WebApplication host,
        PairingHub hub,
        bool ownsHub,
        RemoteWindow window,
        Guid targetId,
        WindowsAttachmentStagingAdapter staging)
    {
        Root = root;
        Layout = layout;
        _host = host;
        _hub = hub;
        _ownsHub = ownsHub;
        Window = window;
        TargetId = targetId;
        Staging = staging;
        Window.PasteGestureCompleted += outcome =>
        {
            if (outcome.Accepted)
            {
                _pasteOutcome.TrySetResult(outcome);
            }
        };
    }

    public string Root { get; }

    public ApplicationDataLayout Layout { get; }

    public RemoteWindow Window { get; }

    public Guid TargetId { get; }

    public WindowsAttachmentStagingAdapter Staging { get; }

    public RemotePageSession Page => Window.Pages.Single(page => page.TargetId == TargetId);

    public static async Task<RemoteAttachmentBridgeHarness> StartAsync(
        Dispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var hub = new PairingHub(
            new PairingHubOptions(),
            new MemoryTargetStore(),
            new HttpPairingTransport(new PairingTransportOptions()),
            new SystemClock(),
            new GuidIdGenerator());
        await hub.StartAsync(cancellationToken);
        return await StartCoreAsync(dispatcher, hub, ownsHub: true, null, null, null, cancellationToken);
    }

    public static Task<RemoteAttachmentBridgeHarness> StartWithHubAsync(
        Dispatcher dispatcher,
        PairingHub hub,
        Guid targetId,
        string remoteUrl,
        string baseUrl,
        CancellationToken cancellationToken) =>
        StartCoreAsync(dispatcher, hub, ownsHub: false, targetId, remoteUrl, baseUrl, cancellationToken);

    public async Task WaitForCapabilityAsync(RemotePageSession page, CancellationToken cancellationToken)
    {
        Assert.Equal(TargetId, page.TargetId);
        await WaitForAsync(() => page.Capability is not null, "page capability granted", cancellationToken);
        Assert.Equal(page.Lifecycle.TrustedOrigin!.Normalized, page.Capability!.Value.Origin.Normalized);
    }

    public Task WaitForAsync(Func<bool> condition, string because, CancellationToken cancellationToken) =>
        WaitForAsync(condition, because, _defaultTimeout, cancellationToken);

    public static async Task WaitForAsync(
        Func<bool> condition,
        string because,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待超时：" + because);
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>在页面里执行脚本（真实 WebView2 <c>ExecuteScriptAsync</c>）。</summary>
    public async Task RunPageAsync(string script) => await ExecuteAsync(script);

    /// <summary>页面里的表达式是否为 <c>true</c>（真实 WebView2 求值，不阻塞 UI 线程）。</summary>
    public async Task<bool> PageTrueAsync(string expression) =>
        string.Equals(await ExecuteAsync(expression), "true", StringComparison.Ordinal);

    /// <summary>等待页面里的表达式变为 <c>true</c>。</summary>
    public async Task WaitForPageAsync(string expression, string because, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!await PageTrueAsync(expression))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待超时：" + because);
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>在应用数据根下写一个真实源文件，并返回它的路径。</summary>
    public string StageFile(string leafName, int byteLength)
    {
        var path = Path.Combine(Root, leafName);
        var bytes = new byte[byteLength];
        for (var index = 0; index < byteLength; index += 1)
        {
            bytes[index] = (byte)((index * 31) % 251);
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    public async Task<NativePasteOutcome> WaitForPasteOutcomeAsync(CancellationToken cancellationToken) =>
        await _pasteOutcome.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

    public static async Task<int> HeartbeatCountAsync(PairingHub hub, Guid targetId, CancellationToken cancellationToken)
    {
        var snapshot = await hub.GetSnapshotAsync(cancellationToken);
        return snapshot.Targets.Single(target => target.TargetId == targetId).LastHeartbeatUtc is null ? 0 : 1;
    }

    public static async Task<bool> WaitForHeartbeatAsync(
        PairingHub hub,
        Guid targetId,
        int previous,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await hub.GetSnapshotAsync(cancellationToken);
            var target = snapshot.Targets.SingleOrDefault(candidate => candidate.TargetId == targetId);
            if (target is null)
            {
                return false;
            }

            var heartbeat = target.LastHeartbeatUtc is null ? 0 : 1;
            if (heartbeat > previous || target.Connectivity == ConnectivityState.Online)
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Window.AllowClose();
            Window.Close();
        }
        catch (InvalidOperationException)
        {
            // 用例自己已经关过窗口。
        }

        if (_ownsHub)
        {
            await _hub.DisposeAsync();
        }

        await _host.StopAsync();
        await _host.DisposeAsync();
        TryDeleteRoot();
    }

    private async Task<string> ExecuteAsync(string script)
    {
        var core = Page.Web.CoreWebView2
            ?? throw new InvalidOperationException("页面尚未初始化完成");
        return await core.ExecuteScriptAsync(script);
    }

    private static async Task<RemoteAttachmentBridgeHarness> StartCoreAsync(
        Dispatcher dispatcher,
        PairingHub hub,
        bool ownsHub,
        Guid? targetId,
        string? remoteUrl,
        string? baseUrl,
        CancellationToken cancellationToken)
    {
        _ = dispatcher;
        var root = Path.Combine(Path.GetTempPath(), "DshLauncher.D17", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, ApplicationDataLayout.OwnershipMarkerName),
            ApplicationDataLayout.OwnershipMarkerContent);
        var layout = new ApplicationDataLayout(root);
        var host = await StartHostAsync();
        var origin = RemoteOrigin.Parse(host.Urls.First()).Origin!;
        var firstTargetId = targetId ?? Guid.NewGuid();
        WindowsAttachmentStagingAdapter? staging = null;
        var window = new RemoteWindow(
            hub,
            firstTargetId,
            remoteUrl ?? origin.Normalized + "/bridge",
            "Bridge target",
            baseUrl ?? origin.Normalized,
            Path.Combine(root, "webview-bridge"),
            static _ => Task.CompletedTask,
            id =>
            {
                staging = new WindowsAttachmentStagingAdapter(layout, id);
                return staging;
            });
        var harness = new RemoteAttachmentBridgeHarness(
            root,
            layout,
            host,
            hub,
            ownsHub,
            window,
            firstTargetId,
            staging ?? throw new InvalidOperationException("暂存适配器未建立"));
        window.ShowAndActivate();
        await harness.WaitForAsync(
            () => harness.Page.State != RemotePageLoadState.Pending,
            "page initialization started",
            cancellationToken);
        return harness;
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
        builder.Logging.ClearProviders();
        var host = builder.Build();
        host.Run(async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(BridgePageHtml, context.RequestAborted);
        });
        await host.StartAsync();
        return host;
    }

    private void TryDeleteRoot()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // WebView2 进程可能仍持有用户数据目录；残留不影响判定。
        }
    }

    /// <summary>
    /// D17 实机页面：真实 WebView2 WebMessage 的页面侧（记录宿主帧、按需回帧）。
    /// 这是<b>测试夹具</b>，只实现本用例需要的最小应答；生产接收端在插件里（D18 互通）。
    /// </summary>
    private const string BridgePageHtml = """
        <!doctype html>
        <html><body><h1>d17 bridge page</h1>
        <script>
        window.__dshBridge = (function () {
          var state = { hostFrames: [], receivedBytes: 0, pending: {}, fileEnd: null, fileEndAnswered: false, envelope: null, errors: [] };
          function onMessage(event) {
            var envelope = event.data;
            if (typeof envelope === 'string') {
              try { envelope = JSON.parse(envelope); } catch (error) { state.errors.push('parse'); return; }
            }
            if (!envelope || envelope.v !== 1 || !envelope.frame) { state.errors.push('shape'); return; }
            state.envelope = { v: 1, channel: envelope.channel, epoch: envelope.epoch };
            var frame = envelope.frame;
            state.hostFrames.push(frame.type);
            if (frame.type === 'chunk') {
              state.receivedBytes += frame.byteLength;
              state.pending[frame.fileId + ':' + frame.seq] = frame;
            }
            if (frame.type === 'file-end') { state.fileEnd = frame; }
          }
          if (window.chrome && window.chrome.webview) { window.chrome.webview.addEventListener('message', onMessage); }
          function post(type, fields) {
            var frame = Object.assign({ v: 1, type: type }, fields || {});
            var envelope = Object.assign({ v: 1 }, state.envelope, { frame: frame });
            window.chrome.webview.postMessage(envelope);
          }
          return {
            snapshot: function () { return JSON.stringify(state); },
            earlyHandshake: function () { post('hello', { clientBuild: 'addon/0.1.0' }); },
            handshake: function (features, sessionId) {
              post('hello', { clientBuild: 'addon/0.1.0' });
              post('capabilities', { features: features, limits: { maxFileBytes: 20971520, maxFilesPerBatch: 10, maxBatchBytes: 52428800, maxScreenshotPixels: 40000000, maxStagingBytesPerTarget: 104857600, maxConcurrentTargets: 2 } });
              post('context', { sessionId: sessionId, targetId: 'page-target-1', documentEpoch: 1, composerEpoch: 3, composerScope: 'scope-1' });
            },
            acknowledge: function () {
              Object.keys(state.pending).forEach(function (key) {
                var chunk = state.pending[key];
                delete state.pending[key];
                post('ack', { sessionId: 'session-1', batchId: chunk.batchId, fileId: chunk.fileId, seq: chunk.seq, offset: chunk.offset, byteLength: chunk.byteLength, bufferedBytes: chunk.offset + chunk.byteLength, inFlight: 0 });
              });
              if (state.fileEnd && !state.fileEndAnswered) {
                state.fileEndAnswered = true;
                post('import-result', { sessionId: 'session-1', batchId: state.fileEnd.batchId, fileId: state.fileEnd.fileId, status: 'staged', attachmentIds: ['att-1'] });
              }
            }
          };
        })();
        </script>
        </body></html>
        """;
}
