using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using DshLauncher.Desktop.Remote;
using DshLauncher.Platform.Windows.Attachments;
using Microsoft.Web.WebView2.Wpf;

// 本项目同时引用 WPF 与 WinForms（UseWPF + UseWindowsForms），同名的输入类型必须显式消歧。
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;

namespace DshLauncher.Desktop.Attachments;

/// <summary>
/// 窗口事实来源（由 <see cref="RemoteWindow"/> 实现）：只回答"现在哪个页面是活动的、
/// 窗口/焦点/标签处于什么状态"，不参与任何判定。这样 D16 的规则留在 Core 的可移植状态机里，
/// 窗口在这条链路上只提供真实 WPF 事实。
/// </summary>
public interface INativePasteWindowHost
{
    /// <summary>窗口当前可见（不是隐藏/最小化）。</summary>
    bool IsWindowVisible { get; }

    /// <summary>窗口当前是前台窗口。</summary>
    bool IsWindowActive { get; }

    /// <summary>当前活动标签页对应的页面；没有页面时为 <c>null</c>。</summary>
    RemotePageSession? ActivePage { get; }

    /// <summary>键盘焦点是否落在活动页面的 WebView 内。</summary>
    bool IsFocusInsideActivePage { get; }

    /// <summary>读取该页面当前已准入的 composer 上下文（会话/锁定/子代理）。</summary>
    ImportContextFacts ImportContext(RemotePageSession page);

    /// <summary>窗口/标签焦点令牌：把授权钉在"当时拥有焦点的那个窗口+标签+页面"上。</summary>
    string FocusToken(RemotePageSession page);
}

/// <summary>
/// D16 原生输入路由（真实 WPF）：把<b>真实原生手势</b>——粘贴快捷键、原生上下文菜单的"粘贴"、
/// 拖放到窗口——翻译成平台中立事实，交给每个目标自己的
/// <see cref="NativePasteOrchestrator"/> 判定。
///
/// 边界与安全性质：
/// <list type="bullet">
/// <item>只挂在远程窗口自己身上（<c>PreviewKeyDown</c>/<c>DragOver</c>/<c>Drop</c> 与页面 WebView 的
/// 上下文菜单），<b>不</b>注册全局键盘钩子，因此不会全局截断所有 Ctrl+V；</item>
/// <item>手势与焦点事实全部来自 WPF 真实状态；页面的 <c>isTrusted</c>、路径或文件名都不参与，
/// 桥也无法调用本类的任何方法；</item>
/// <item>只有在判定为原生通路时才 <c>e.Handled = true</c>（抑制浏览器默认粘贴）：
/// 纯文本或桥模式一律放行，默认行为不受影响；</item>
/// <item>每个目标一个作用域（暂存适配器、编排器、唯一消费者账本、截图模式台账），
/// 因此账目与资源按目标隔离，绝不串目标。</item>
/// </list>
/// 真实事件顺序、WebView2 焦点表现、拖放数据对象的 COM 形态全部属 WindowsPending，
/// 见 <c>NativePasteGesture.WindowsPending.md</c>。
/// </summary>
public sealed class NativePasteGestureRouter : IDisposable
{
    /// <summary>原生上下文菜单里"粘贴"项的标题。</summary>
    public const string PasteMenuItemHeader = "粘贴（发送为附件）";

    /// <summary>该目标的暂存适配器不可用（初始化失败或未提供）。</summary>
    public const string StagingUnavailableCode = "attachment-staging-unavailable";

    private readonly Func<Guid, WindowsAttachmentStagingAdapter?>? _stagingFactory;
    private readonly INativePasteWindowHost _host;
    private readonly AttachmentLimits _limits;
    private readonly Dictionary<Guid, TargetPasteScope> _scopes = [];
    private readonly Dictionary<Guid, string> _scopeFailures = [];
    private readonly List<RemotePageSession> _attachedPages = [];
    private ContextMenu? _menu;
    private Window? _window;
    private bool _disposed;

    public NativePasteGestureRouter(
        INativePasteWindowHost host,
        Func<Guid, WindowsAttachmentStagingAdapter?>? stagingFactory,
        AttachmentLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _stagingFactory = stagingFactory;
        _limits = limits ?? new AttachmentLimits();
    }

    /// <summary>一次手势的最终结局（窗口据此提示用户；拒绝也要显示确定原因）。</summary>
    public event Action<NativePasteOutcome>? GestureCompleted;

    /// <summary>所有已建立作用域的目标。</summary>
    public IReadOnlyCollection<Guid> ScopedTargets => [.. _scopes.Keys];

    /// <summary>把路由挂到远程窗口上。</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window = window;
        window.PreviewKeyDown += OnPreviewKeyDown;
        window.PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;
        window.DragOver += OnDragOver;
        window.Drop += OnDrop;
        window.AllowDrop = true;
    }

    /// <summary>
    /// 为一个页面建立目标作用域并挂上原生上下文菜单
    /// （WebView2 默认上下文菜单已关闭，因此这是唯一菜单）。
    /// </summary>
    public void AttachPage(RemotePageSession page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attachedPages.Contains(page))
        {
            return;
        }

        _attachedPages.Add(page);
        _ = EnsureScope(page.TargetId);
    }

    /// <summary>目标页被移除时丢弃引用与作用域。</summary>
    public void DetachPage(RemotePageSession page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _attachedPages.Remove(page);
        if (_scopes.Remove(page.TargetId, out var scope))
        {
            scope.Dispose();
        }

        _scopeFailures.Remove(page.TargetId);
    }

    /// <summary>某个目标的唯一消费者账本；没有作用域时为 <c>null</c>。</summary>
    public NativePasteConsumerLedger? LedgerFor(Guid targetId) =>
        _scopes.TryGetValue(targetId, out var scope) ? scope.Ledger : null;

    /// <summary>某个目标的截图所有者登记表；没有作用域时为 <c>null</c>。</summary>
    public ScreenshotOwnerRegistry? OwnersFor(Guid targetId) =>
        _scopes.TryGetValue(targetId, out var scope) ? scope.Owners : null;

    /// <summary>
    /// 某个目标自己的原生暂存适配器（D17 组合根据此建立附件桥）。
    /// 没有作用域（未挂页或暂存不可用）时为 <c>null</c>——桥会以确定码拒绝传输，绝不静默。
    /// </summary>
    public WindowsAttachmentStagingAdapter? StagingFor(Guid targetId) =>
        _scopes.TryGetValue(targetId, out var scope) ? scope.Staging : null;

    /// <summary>
    /// 记录一次截图所有者握手（D17 从已验证页面的 capabilities 消息调用）。
    /// 没有作用域时返回"未决"，绝不猜。
    /// </summary>
    public ScreenshotOwnerDecision RecordScreenshotHandshake(
        Guid targetId,
        RemotePageEpoch epoch,
        ScreenshotOwnerFacts facts) =>
        OwnersFor(targetId)?.Record(targetId, epoch, facts)
        ?? new ScreenshotOwnerDecision(ScreenshotOwnerMode.Undecided, ScreenshotOwnerHandshake.UndecidedCode, "目标尚无作用域");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnPreviewKeyDown;
            _window.PreviewMouseRightButtonUp -= OnPreviewMouseRightButtonUp;
            _window.DragOver -= OnDragOver;
            _window.Drop -= OnDrop;
            _window = null;
        }

        foreach (var scope in _scopes.Values)
        {
            scope.Dispose();
        }

        _scopes.Clear();
        _attachedPages.Clear();
    }

    /// <summary>粘贴快捷键：Ctrl+V 与 Shift+Insert。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 修饰键取自事件自己的键盘设备：这样"真实设备状态"与"这次事件"永远一致。
        var modifiers = e.KeyboardDevice.Modifiers;
        var isPaste = (e.Key == Key.V && modifiers == ModifierKeys.Control)
            || (e.Key == Key.Insert && modifiers == ModifierKeys.Shift);
        if (!isPaste)
        {
            return;
        }

        e.Handled = BeginClipboardGesture(NativeGestureKind.PasteHotkey);
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // WebView2 的默认上下文菜单被关闭（RemotePageSession），右击不会自动弹出浏览器菜单，
        // 因此这里显式打开本窗口自己的原生菜单；"粘贴"是 ContextMenuPaste 手势。
        // 真实表现（HwndHost 是否会先吃掉右键）属 WindowsPending。
        var page = _host.ActivePage;
        if (page is null || !_attachedPages.Contains(page))
        {
            return;
        }

        _menu ??= CreateNativeMenu();
        _menu.PlacementTarget = page.Web;
        _menu.IsOpen = true;
    }

    /// <summary>窗口自己的原生上下文菜单（唯一菜单：WebView2 默认菜单已关闭）。</summary>
    private ContextMenu CreateNativeMenu()
    {
        var paste = new MenuItem { Header = PasteMenuItemHeader };
        paste.Click += (_, _) => BeginContextMenuPaste();
        return new ContextMenu { Items = { paste } };
    }

    private void BeginContextMenuPaste() => _ = BeginClipboardGesture(NativeGestureKind.ContextMenuPaste);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles && CanGesture(out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        // 拖放由宿主消费（无论成败）：页面不能同时拿到同一次拖放。
        e.Handled = true;
        var evidence = BuildEvidence(NativeGestureKind.DragDrop, dropLandedOnActivePage: true);
        if (evidence is null || !CanGesture(out var scope))
        {
            Publish(Rejected(evidence, StagingUnavailableCode, "拖放时没有可用的原生作用域"));
            return;
        }

        var page = _host.ActivePage!;
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        var plan = scope.Orchestrator.PlanDrop(evidence.Value, _host.ImportContext(page), hasFiles);
        if (plan.Immediate is not null)
        {
            Publish(plan.Immediate);
            return;
        }

        var oleData = e.Data as System.Runtime.InteropServices.ComTypes.IDataObject;
        _ = RunAsync(plan, new DropReadPort(scope.Drops, oleData));
    }

    /// <summary>剪贴板来源的手势：同步计划 → 立即决定是否抑制默认行为 → 异步执行。</summary>
    private bool BeginClipboardGesture(NativeGestureKind kind)
    {
        if (!_host.IsWindowVisible || !_host.IsWindowActive)
        {
            // 窗口不可见/不是前台窗口：不消费事件，浏览器保留默认行为。
            Publish(Rejected(
                null,
                _host.IsWindowVisible ? NativePasteCodes.GestureWindowInactive : NativePasteCodes.GestureWindowHidden,
                "窗口不是可见的前台窗口"));
            return false;
        }

        var evidence = BuildEvidence(kind, dropLandedOnActivePage: false);
        if (evidence is null || !_attachedPages.Contains(_host.ActivePage!))
        {
            // 焦点/标签不成立：不消费事件，浏览器保留默认行为。
            Publish(Rejected(evidence, NativePasteCodes.GestureTabNotActive, "手势不在当前活动页面内"));
            return false;
        }

        if (!CanGesture(out var scope))
        {
            Publish(Rejected(evidence, StagingUnavailableCode, FailureDetail(_host.ActivePage!.TargetId)));
            return false;
        }

        var plan = scope.Orchestrator.Plan(evidence.Value, _host.ImportContext(_host.ActivePage!));
        if (plan.Immediate is not null)
        {
            Publish(plan.Immediate);
            return false;
        }

        _ = RunAsync(plan, scope.Clipboard);

        // 原生通路：必须抑制浏览器默认粘贴，否则同一次动作会被消费两次。
        return plan.SuppressesBrowserDefault;
    }

    private async Task RunAsync(NativePastePlan plan, INativePasteReadPort port)
    {
        var scope = _scopes[plan.Evidence.TargetId];
        NativePasteOutcome outcome;
        try
        {
            outcome = await scope.Orchestrator.ExecuteAsync(plan, port).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            outcome = Rejected(plan.Evidence, NativePasteOrchestrator.ClipboardMalformedCode, "原生读取异常：" + error.Message);
        }

        Publish(outcome);
    }

    private bool CanGesture(out TargetPasteScope scope)
    {
        scope = null!;
        var page = _host.ActivePage;
        return page is not null
            && _attachedPages.Contains(page)
            && _host.IsWindowVisible
            && _host.IsWindowActive
            && _scopes.TryGetValue(page.TargetId, out scope!);
    }

    private NativeGestureEvidence? BuildEvidence(NativeGestureKind kind, bool dropLandedOnActivePage)
    {
        var page = _host.ActivePage;
        if (page is null || !_host.IsWindowVisible)
        {
            return null;
        }

        var focus = new NativeWindowFocusState(
            WindowVisible: _host.IsWindowVisible,
            WindowActive: _host.IsWindowActive,
            FocusInsideActivePage: dropLandedOnActivePage || _host.IsFocusInsideActivePage,
            ActiveTabMatchesTarget: _attachedPages.Contains(page));
        return new NativeGestureEvidence(
            "gesture-" + Guid.NewGuid().ToString("N"),
            kind,
            page.TargetId,
            page.Lifecycle.Epoch,
            _host.FocusToken(page),
            focus,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>为目标建立作用域（暂存适配器 + 编排器）；失败时记住确定原因。</summary>
    private TargetPasteScope? EnsureScope(Guid targetId)
    {
        if (_scopes.TryGetValue(targetId, out var existing))
        {
            return existing;
        }

        if (_stagingFactory is null)
        {
            _scopeFailures[targetId] = "没有配置附件暂存适配器";
            return null;
        }

        try
        {
            var staging = _stagingFactory(targetId);
            if (staging is null)
            {
                _scopeFailures[targetId] = "附件暂存适配器不可用";
                return null;
            }

            staging.Initialize();
            var scope = new TargetPasteScope(targetId, staging, _limits);
            _scopes[targetId] = scope;
            _scopeFailures.Remove(targetId);
            return scope;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or AttachmentStagingException or ArgumentException)
        {
            _scopeFailures[targetId] = "暂存根不可用：" + error.Message;
            return null;
        }
    }

    private string FailureDetail(Guid targetId) =>
        _scopeFailures.TryGetValue(targetId, out var detail) ? detail : "附件暂存不可用";

    private void Publish(NativePasteOutcome outcome) => GestureCompleted?.Invoke(outcome);

    private static NativePasteOutcome Rejected(NativeGestureEvidence? evidence, string code, string detail)
    {
        var classification = NativePasteFailurePolicy.Classify(code);
        return new NativePasteOutcome
        {
            Accepted = false,
            Code = code,
            Detail = detail,
            TargetId = evidence?.TargetId ?? Guid.Empty,
            GestureId = evidence?.GestureId ?? string.Empty,
            GestureKind = evidence?.Kind ?? NativeGestureKind.PasteHotkey,
            Recoverable = classification.Recoverable,
            Retry = classification.Retry,
            KeepsBrowserDefault = true,
        };
    }

    /// <summary>一个目标自己的原生粘贴作用域：独立暂存根、独立账本、独立截图模式。</summary>
    private sealed class TargetPasteScope : IDisposable
    {
        public TargetPasteScope(Guid targetId, WindowsAttachmentStagingAdapter staging, AttachmentLimits limits)
        {
            TargetId = targetId;
            Staging = staging;
            Clipboard = new WindowsClipboardPasteSource(staging, limits);
            Drops = new WindowsDroppedFilesSource(staging, limits);
            Ledger = new NativePasteConsumerLedger();
            Owners = new ScreenshotOwnerRegistry();
            Orchestrator = new NativePasteOrchestrator(
                Clipboard,
                new NativeGestureAuthorizer(),
                Ledger,
                Owners,
                limits);
        }

        public Guid TargetId { get; }

        public WindowsAttachmentStagingAdapter Staging { get; }

        public WindowsClipboardPasteSource Clipboard { get; }

        public WindowsDroppedFilesSource Drops { get; }

        public NativePasteConsumerLedger Ledger { get; }

        public ScreenshotOwnerRegistry Owners { get; }

        public NativePasteOrchestrator Orchestrator { get; }

        public void Dispose() => Staging.Dispose();
    }

    /// <summary>
    /// 拖放读取端口：把 OLE 数据对象的读取包成端口。拖放的载荷是<b>有界</b>的路径列表
    /// （≤ 批内文件数），文件内容复制由 D14 暂存服务在线程外完成，因此这里就在调用线程上读完，
    /// 避免把公寓绑定的 OLE 数据对象跨线程传递。
    /// </summary>
    private sealed class DropReadPort(WindowsDroppedFilesSource source, System.Runtime.InteropServices.ComTypes.IDataObject? oleData)
        : INativePasteReadPort
    {
        public Task<ClipboardReadResult> ReadAsync(
            ClipboardProbe probe,
            ClipboardSourceDecision decision,
            CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(source.RegisterDroppedFiles(oleData, cancellationToken));
            }
            catch (COMException error)
            {
                return Task.FromResult(new ClipboardReadResult
                {
                    Ok = false,
                    Code = WindowsDroppedFilesSource.NoFileDropCode,
                    Detail = "拖放数据对象不可读：" + error.Message,
                    Kind = ClipboardSourceKind.FileList,
                });
            }
        }
    }
}

/// <summary>
/// 键盘焦点判定（真实 WPF + Win32 事实）。
///
/// WebView2 是 HwndHost：焦点在渲染子窗口里时，WPF 的
/// <see cref="Keyboard.FocusedElement"/> 可能是 <c>null</c>，因此这里分层判定：
/// <list type="number">
/// <item><see cref="UIElement.IsKeyboardFocusWithin"/> 为真；</item>
/// <item>否则看 <c>GetFocus()</c> 的根窗口是否就是本窗口（焦点在我们窗口里，而活动页是唯一可见页）。</item>
/// </list>
/// 第二步的真实取值只能实机核对，属 WindowsPending。
/// </summary>
public static partial class WindowFocusProbe
{
    private const uint GaRoot = 2;

    /// <summary>焦点是否在指定 WebView 或至少在本窗口内。</summary>
    public static bool IsFocusInside(WebView2 web, Window window)
    {
        ArgumentNullException.ThrowIfNull(web);
        ArgumentNullException.ThrowIfNull(window);

        if (web.IsKeyboardFocusWithin)
        {
            return true;
        }

        if (Keyboard.FocusedElement is DependencyObject focused
            && (ReferenceEquals(focused, web) || web.IsAncestorOf(focused)))
        {
            return true;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return false;
        }

        var focusedHandle = GetFocus();
        return focusedHandle != nint.Zero && GetAncestor(focusedHandle, GaRoot) == handle;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hWnd, uint gaFlags);
}
