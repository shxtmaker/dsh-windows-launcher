using System.IO;
using System.Net;
using System.Windows.Controls;
using System.Windows.Threading;
using DshLauncher.Core;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using DshLauncher.Core.Remote;
using DshLauncher.Desktop;
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
/// D15 Windows 实机用例：真实 WPF + 真实 WebView2 + 真实导航事件。
/// <b>本轮没有 Windows 机器，与 D14 相同，这里一条都没有执行过</b>，因此不得记为 PASS；
/// 每条用例的 id、要跑什么、断言什么、为什么 Linux 不能跑见
/// <c>src/DshLauncher.Desktop/Remote/RemotePageSession.WindowsPending.md</c>。
/// Linux 侧本轮真实执行的是平台中立规则：<c>tests/DshLauncher.Core.Tests/Remote/</c>。
/// </summary>
[Trait("triggerTags", "VFY-07")]
public sealed class RemotePageSessionWindowsTests
{
    [Fact]
    public Task WS01EveryTargetPageOwnsItsOwnWebViewChannelAndUserDataFolder() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var secondTarget = Guid.NewGuid();
            harness.Window.OpenPage(secondTarget, harness.ExternalOrigin + "/second", "Second target",
                harness.ExternalOrigin, Path.Combine(harness.Root, "webview-second"));

            var pages = harness.Window.Pages.ToArray();

            Assert.Equal(2, pages.Length);
            var first = pages.Single(page => page.TargetId == harness.FirstTargetId);
            var second = pages.Single(page => page.TargetId == secondTarget);
            Assert.NotSame(first.Web, second.Web);
            Assert.NotEqual(first.UserDataFolderPath, second.UserDataFolderPath);
            Assert.NotEqual(first.ChannelId, second.ChannelId);
            Assert.NotEqual(first.Lifecycle.TrustedOrigin!.Normalized, second.Lifecycle.TrustedOrigin!.Normalized);
            Assert.Null(first.Capability);
            Assert.Null(second.Capability);
        });

    [Fact]
    public Task WS02NavigationStartingRevokesTheOldEpochBeforeTheNewDocumentLoads() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.FirstPage;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            var previousEpoch = page.Lifecycle.Epoch;
            var previousToken = page.Lifecycle.InFlightToken;

            var observedAtStart = new TaskCompletionSource<RemotePageCapability?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            page.Web.NavigationStarting += (_, _) => observedAtStart.TrySetResult(page.Capability);

            page.Web.Source = new Uri(harness.TrustedOrigin + "/next");

            var capabilityWhenNavigationStarted = await observedAtStart.Task.WaitAsync(
                TimeSpan.FromSeconds(60), cancellationToken);

            Assert.Null(capabilityWhenNavigationStarted);
            Assert.True(page.Lifecycle.Epoch.Value > previousEpoch.Value);
            Assert.True(previousToken.IsCancellationRequested);
            Assert.Equal(RemotePageCancellationTrigger.NavigationStarted, page.Lifecycle.LastCancellation!.Value.Trigger);
        });

    [Fact]
    public Task WS03ExternalLinkDoesNotInheritFileCapability() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.FirstPage;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            var staleEpoch = page.Lifecycle.Epoch;

            page.OpenExternalLink(harness.ExternalOrigin + "/external");

            Assert.Null(page.Capability);
            Assert.Equal(RemotePageSessionCodes.MessageEpochStale,
                page.AdmitMessage(new RemotePageInboundMessage(page.ChannelId, staleEpoch, "chunk")).Code);
            await harness.WaitForAsync(
                () => page.State == RemotePageLoadState.Loaded, "external navigation completed", cancellationToken);
            Assert.Null(page.Capability);
            Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, page.AdmitNewWork().Code);
        });

    [Fact]
    public Task WS04TabSwitchSuspendsNewWorkWithoutCancellingInFlightWork() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var secondTarget = Guid.NewGuid();
            harness.Window.OpenPage(secondTarget, harness.ExternalOrigin + "/second", "Second target",
                harness.ExternalOrigin, Path.Combine(harness.Root, "webview-second"));
            var first = harness.Window.Pages.Single(page => page.TargetId == harness.FirstTargetId);
            var second = harness.Window.Pages.Single(page => page.TargetId == secondTarget);
            await harness.WaitForCapabilityAsync(first, cancellationToken);
            await harness.WaitForCapabilityAsync(second, cancellationToken);
            var inFlight = first.Lifecycle.InFlightToken;
            var tabs = (ListBox)harness.Window.FindName("PageTabs")!;

            tabs.SelectedIndex = 0;
            await harness.WaitForAsync(() => first.Lifecycle.Phase == RemotePagePhase.Active, "first page active", cancellationToken);

            Assert.Equal(RemotePagePhase.Suspended, second.Lifecycle.Phase);
            Assert.Equal(RemotePageSuspensionReason.TabSwitched, second.Lifecycle.SuspensionReason);
            Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, second.AdmitNewWork().Code);
            Assert.False(second.Lifecycle.InFlightToken.IsCancellationRequested);

            tabs.SelectedIndex = 1;
            await harness.WaitForAsync(() => second.Lifecycle.Phase == RemotePagePhase.Active, "second page active", cancellationToken);

            Assert.Equal(RemotePagePhase.Suspended, first.Lifecycle.Phase);
            Assert.False(inFlight.IsCancellationRequested);
            Assert.Equal(RemotePageSessionCodes.WorkAccepted, second.AdmitNewWork().Code);
        });

    [Fact]
    public Task WS05TargetRemovalCancelsInFlightWorkAndRejectsLateResults() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.FirstPage;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            var epoch = page.Lifecycle.Epoch;
            var channel = page.ChannelId;
            var inFlight = page.Lifecycle.InFlightToken;
            var host = (Grid)harness.Window.FindName("PageHost")!;
            var tabs = (ListBox)harness.Window.FindName("PageTabs")!;

            harness.Window.RemovePage(harness.FirstTargetId);

            Assert.True(inFlight.IsCancellationRequested);
            Assert.Equal(RemotePagePhase.Closed, page.Lifecycle.Phase);
            Assert.Equal(RemotePageCancellationTrigger.TargetRemoved, page.Lifecycle.LastCancellation!.Value.Trigger);
            Assert.Equal(RemotePageSessionCodes.SessionClosed,
                page.AdmitMessage(new RemotePageInboundMessage(channel, epoch, "chunk")).Code);
            Assert.Empty(host.Children.Cast<object>());
            Assert.Empty(tabs.Items.Cast<object>());
        });

    [Fact]
    public Task WS06ApplicationExitCancelsEveryPageSession() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var secondTarget = Guid.NewGuid();
            harness.Window.OpenPage(secondTarget, harness.ExternalOrigin + "/second", "Second target",
                harness.ExternalOrigin, Path.Combine(harness.Root, "webview-second"));
            await harness.WaitForCapabilityAsync(harness.FirstPage, cancellationToken);
            var first = harness.FirstPage;
            var second = harness.Window.Pages.Single(page => page.TargetId == secondTarget);
            var firstToken = first.Lifecycle.InFlightToken;
            var secondToken = second.Lifecycle.InFlightToken;
            var host = (Grid)harness.Window.FindName("PageHost")!;

            harness.Window.AllowClose();
            harness.Window.Close();

            Assert.True(firstToken.IsCancellationRequested);
            Assert.True(secondToken.IsCancellationRequested);
            Assert.Equal(RemotePagePhase.Closed, first.Lifecycle.Phase);
            Assert.Equal(RemotePagePhase.Closed, second.Lifecycle.Phase);
            Assert.Equal(RemotePageCancellationTrigger.ApplicationExit, first.Lifecycle.LastCancellation!.Value.Trigger);
            Assert.Equal(RemotePageCancellationTrigger.ApplicationExit, second.Lifecycle.LastCancellation!.Value.Trigger);
            Assert.Empty(host.Children.Cast<object>());
        });

    [Fact]
    public Task WS07HiddenWindowRefusesNewWorkUntilShownAgain() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.FirstPage;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            var inFlight = page.Lifecycle.InFlightToken;

            // 不带 AllowClose 的 Close 只会隐藏窗口（配对凭据与保活仍在枢纽里）。
            harness.Window.Close();

            Assert.Equal(RemotePagePhase.Suspended, page.Lifecycle.Phase);
            Assert.Equal(RemotePageSuspensionReason.WindowHidden, page.Lifecycle.SuspensionReason);
            Assert.Equal(RemotePageSessionCodes.WorkPageSuspended, page.AdmitNewWork().Code);
            Assert.False(inFlight.IsCancellationRequested);

            harness.Window.ShowAndActivate();

            Assert.Equal(RemotePagePhase.Active, page.Lifecycle.Phase);
            Assert.Equal(RemotePageSessionCodes.WorkAccepted, page.AdmitNewWork().Code);
        });

    [Fact]
    public Task WS08MainDocumentOriginIsVerifiedOnTheLoadedDocumentNotOnSubresources() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.FirstPage;
            await harness.WaitForCapabilityAsync(page, cancellationToken);

            // 目标页里带一个来自另一个来源的子资源：它必须真的被请求，但不得影响能力。
            await harness.ExternalResourceRequested.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);

            Assert.NotNull(page.Capability);
            Assert.Equal(harness.TrustedOrigin, page.Capability!.Value.Origin.Normalized);
            Assert.Equal(RemotePageSessionCodes.WorkAccepted, page.AdmitNewWork().Code);
            Assert.Equal(RemotePageSessionCodes.Accepted,
                page.AdmitMessage(new RemotePageInboundMessage(page.ChannelId, page.Lifecycle.Epoch, "hello")).Code);
        });

    [Fact]
    public Task WS09MainDocumentAtAnotherOriginNeverGetsCapability() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await RemotePageHarness.StartAsync(dispatcher, cancellationToken);
            var page = harness.FirstPage;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            var previousEpoch = page.Lifecycle.Epoch;

            page.Web.Source = new Uri(harness.ExternalOrigin + "/other-origin");
            await harness.WaitForAsync(
                () => page.State == RemotePageLoadState.Loaded && page.Lifecycle.Epoch.Value > previousEpoch.Value,
                "cross-origin main document completed",
                cancellationToken);

            Assert.Null(page.Capability);
            Assert.Equal(RemoteOriginCodes.External, page.Lifecycle.CapabilityCode);
            Assert.Equal(RemotePageSessionCodes.WorkCapabilityAbsent, page.AdmitNewWork().Code);
        });

    [Fact]
    public Task WS10CredentialRevocationClosesOnlyThatTargetsPageSession() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var host = new FakeRemoteAccessHost();
            await host.StartAsync();
            var store = new MemoryTargetStore();
            await using var hub = new PairingHub(
                new PairingHubOptions { HeartbeatInterval = TimeSpan.FromSeconds(2) },
                store,
                new HttpPairingTransport(new PairingTransportOptions()),
                new SystemClock(),
                new GuidIdGenerator());
            await hub.StartAsync(cancellationToken);
            var paired = await hub.AddFromPairingLinkAsync(host.PairingLink, "revocation", cancellationToken);
            Assert.Null(paired.Error);
            var targetId = paired.Target!.TargetId;
            var remoteUrl = await hub.GetRemoteUiUrlAsync(targetId, cancellationToken);
            Assert.Null(remoteUrl.Error);
            var root = NewRoot();
            await using var harness = await RemotePageHarness.StartWithHubAsync(
                dispatcher, root, hub, targetId, remoteUrl.Url!, paired.Target!.BaseUrl, cancellationToken);
            var page = harness.FirstPage;
            await harness.WaitForCapabilityAsync(page, cancellationToken);
            var epoch = page.Lifecycle.Epoch;
            var channel = page.ChannelId;
            var inFlight = page.Lifecycle.InFlightToken;

            // 远端面板"停止"：下一次保活心跳判定为已吊销，枢纽快照变化后窗口必须关闭该页。
            host.StopPairing();
            await RemotePageHarness.WaitForAsync(
                () => page.Lifecycle.Phase == RemotePagePhase.Closed,
                "credential revocation closed the page session",
                TimeSpan.FromSeconds(60),
                cancellationToken);

            Assert.True(inFlight.IsCancellationRequested);
            Assert.Equal(RemotePageCancellationTrigger.CredentialRevoked, page.Lifecycle.LastCancellation!.Value.Trigger);
            Assert.Equal(RemotePageSessionCodes.SessionClosed,
                page.AdmitMessage(new RemotePageInboundMessage(channel, epoch, "chunk")).Code);
            Assert.Empty(((Grid)harness.Window.FindName("PageHost")!).Children.Cast<object>());
        });

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
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "DshLauncher.D15", Guid.NewGuid().ToString("N"));
}

/// <summary>
/// WS-01…WS-09 的真实页面夹具：一个可信来源的 Kestrel 主机（远程界面）、一个外部来源的
/// Kestrel 主机（外部链接与跨来源子资源）、一个真实 <see cref="RemoteWindow"/> 与其第一个页面会话。
/// </summary>
internal sealed class RemotePageHarness : IAsyncDisposable, IDisposable
{
    private readonly WebApplication _trustedHost;
    private readonly WebApplication _externalHost;
    private readonly PairingHub _hub;
    private readonly bool _ownsHub;
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(60);
    private bool _disposed;

    private RemotePageHarness(
        string root,
        WebApplication trustedHost,
        WebApplication externalHost,
        PairingHub hub,
        bool ownsHub,
        RemoteWindow window,
        Guid firstTargetId,
        string pageOrigin,
        RemoteOrigin externalOrigin,
        TaskCompletionSource externalResourceRequested)
    {
        Root = root;
        _trustedHost = trustedHost;
        _externalHost = externalHost;
        _hub = hub;
        _ownsHub = ownsHub;
        Window = window;
        FirstTargetId = firstTargetId;
        TrustedOrigin = pageOrigin;
        ExternalOrigin = externalOrigin.Normalized;
        ExternalResourceRequested = externalResourceRequested;
    }

    public string Root { get; }

    public RemoteWindow Window { get; }

    public Guid FirstTargetId { get; }

    public string TrustedOrigin { get; }

    public string ExternalOrigin { get; }

    public TaskCompletionSource ExternalResourceRequested { get; }

    public RemotePageSession FirstPage => Window.Pages.Single(page => page.TargetId == FirstTargetId);

    public static async Task<RemotePageHarness> StartAsync(Dispatcher dispatcher, CancellationToken cancellationToken)
    {
        var hub = await StartHubAsync(cancellationToken);
        return await StartCoreAsync(
            dispatcher, NewRoot(), hub, ownsHub: true, null, null, null, cancellationToken);
    }

    /// <summary>
    /// D16：带附件暂存工厂的页面夹具（原生粘贴手势需要真实的暂存适配器才能产生捕获票据）。
    /// </summary>
    public static async Task<RemotePageHarness> StartWithStagingAsync(
        Dispatcher dispatcher,
        string root,
        Func<Guid, WindowsAttachmentStagingAdapter?> stagingFactory,
        CancellationToken cancellationToken)
    {
        var hub = await StartHubAsync(cancellationToken);
        return await StartCoreAsync(
            dispatcher, root, hub, ownsHub: true, null, null, null, cancellationToken, stagingFactory);
    }

    public static Task<RemotePageHarness> StartWithHubAsync(
        Dispatcher dispatcher,
        string root,
        PairingHub hub,
        Guid targetId,
        string remoteUrl,
        string baseUrl,
        CancellationToken cancellationToken) =>
        StartCoreAsync(dispatcher, root, hub, ownsHub: false, targetId, remoteUrl, baseUrl, cancellationToken);

    public async Task WaitForCapabilityAsync(RemotePageSession page, CancellationToken cancellationToken)
    {
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // 先允许关闭，Detach 会把每个页面按"应用退出"取消并释放。
            Window.AllowClose();
            Window.Close();
        }
        catch (InvalidOperationException)
        {
            // 用例自己已经关过窗口；WPF 对重复 Close 会抛错，这里不必再关。
        }

        if (_ownsHub)
        {
            await _hub.DisposeAsync();
        }

        await _trustedHost.StopAsync();
        await _trustedHost.DisposeAsync();
        await _externalHost.StopAsync();
        await _externalHost.DisposeAsync();
        TryDeleteRoot();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private static async Task<RemotePageHarness> StartCoreAsync(
        Dispatcher dispatcher,
        string root,
        PairingHub hub,
        bool ownsHub,
        Guid? targetId,
        string? remoteUrl,
        string? baseUrl,
        CancellationToken cancellationToken,
        Func<Guid, WindowsAttachmentStagingAdapter?>? stagingFactory = null)
    {
        Directory.CreateDirectory(root);
        var externalRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var externalHost = await StartExternalHostAsync(externalRequests);
        var externalOrigin = RemoteOrigin.Parse(externalHost.Urls.First()).Origin!;
        var trustedHost = await StartTrustedHostAsync(externalOrigin.Normalized);
        var trustedOrigin = RemoteOrigin.Parse(trustedHost.Urls.First()).Origin!;
        var firstTargetId = targetId ?? Guid.NewGuid();
        var pageUrl = remoteUrl ?? trustedOrigin.Normalized + "/page";
        var pageOrigin = RemoteOrigin.Parse(pageUrl).Origin!.Normalized;
        var window = new RemoteWindow(
            hub, firstTargetId, pageUrl, "First target", baseUrl ?? trustedOrigin.Normalized,
            Path.Combine(root, "webview-first"), static _ => Task.CompletedTask, stagingFactory);
        var harness = new RemotePageHarness(
            root, trustedHost, externalHost, hub, ownsHub, window, firstTargetId, pageOrigin, externalOrigin,
            externalRequests);
        window.ShowAndActivate();
        await harness.WaitForAsync(
            () => harness.FirstPage.State != RemotePageLoadState.Pending, "page initialization started", cancellationToken);
        return harness;
    }

    private static async Task<PairingHub> StartHubAsync(CancellationToken cancellationToken)
    {
        var hub = new PairingHub(
            new PairingHubOptions(),
            new MemoryTargetStore(),
            new HttpPairingTransport(new PairingTransportOptions()),
            new SystemClock(),
            new GuidIdGenerator());
        await hub.StartAsync(cancellationToken);
        return hub;
    }

    private static async Task<WebApplication> StartTrustedHostAsync(string externalOrigin)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
        builder.Logging.ClearProviders();
        var host = builder.Build();
        host.Run(async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            var body = "<!doctype html><html><body><h1>remote ui</h1>" +
                $"<img src=\"{externalOrigin}/pixel.png\" alt=\"cross-origin subresource\">" +
                "</body></html>";
            await context.Response.WriteAsync(body, context.RequestAborted);
        });
        await host.StartAsync();
        return host;
    }

    private static async Task<WebApplication> StartExternalHostAsync(TaskCompletionSource requested)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
        builder.Logging.ClearProviders();
        var host = builder.Build();
        host.Run(async context =>
        {
            requested.TrySetResult();
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<!doctype html><html><body>external</body></html>", context.RequestAborted);
        });
        await host.StartAsync();
        return host;
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "DshLauncher.D15", Guid.NewGuid().ToString("N"));

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
            // WebView2 的浏览器进程可能还持有用户数据目录；残留目录不影响判定。
        }
    }
}

/// <summary>WS-10 用的内存目标库：只保留本进程写入的快照，不接触真实用户数据目录。</summary>
internal sealed class MemoryTargetStore : ITargetStore
{
    private StoredHubDocument? _document;

    public ValueTask<HubStorageReadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_document is null
            ? new HubStorageReadResult(HubStorageStatus.Missing)
            : new HubStorageReadResult(HubStorageStatus.Loaded, _document));
    }

    public ValueTask SaveAsync(StoredHubDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _document = document;
        return ValueTask.CompletedTask;
    }
}
