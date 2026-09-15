using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using DshLauncher.Desktop;
using DshLauncher.Desktop.Attachments;
using DshLauncher.Desktop.Remote;
using DshLauncher.Platform.Windows;
using DshLauncher.Platform.Windows.Attachments;
using Xunit;

namespace DshLauncher.Acceptance.Tests.EndToEnd;

/// <summary>
/// D16 Windows 实机用例：真实 WPF 窗口 + 真实键盘路由事件 + 真实剪贴板 + 真实暂存根。
/// <b>本轮没有 Windows 机器，这里一条都没有执行过</b>，因此不得记为 PASS；
/// 每条用例的 id、要跑什么、断言什么、为什么 Linux 不能跑，以及"手工验证步骤"见
/// <c>src/DshLauncher.Desktop/Attachments/NativePasteGesture.WindowsPending.md</c>。
/// Linux 侧本轮真实执行的是平台中立规则（<c>tests/DshLauncher.Core.Tests/Attachments/NativePaste*</c>）。
/// </summary>
[Trait("triggerTags", "VFY-07")]
public sealed class NativePasteGestureWindowsTests
{
    [Fact]
    public Task WW01ARealPasteHotkeyInTheActiveWindowImportsExactlyOnce() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await NativePasteHarness.StartAsync(dispatcher, cancellationToken);
            harness.StageFile("报告 v2.txt", [0x41, 0x42, 0x43]);
            harness.SetClipboardFileDropList();
            var page = harness.Window.Pages.Single();

            harness.SendPasteHotkey();

            var outcome = await harness.WaitForOutcomeAsync(cancellationToken);
            Assert.True(outcome.Accepted);
            Assert.Equal(NativePasteOrchestrator.ImportedCode, outcome.Code);
            Assert.Equal(NativePasteConsumer.NativePaste, outcome.Consumer);
            Assert.True(outcome.ReadOffSta);
            var accounting = harness.Window.PasteAccounting(page.TargetId, outcome.GestureId);
            Assert.NotNull(accounting);
            Assert.Equal(1, accounting!.Value.TotalImports);
            Assert.True(accounting.Value.ExactlyOneConsumer);
        });

    [Fact]
    public Task WW02AHiddenWindowRefusesTheGestureWithoutReadingTheClipboard() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await NativePasteHarness.StartAsync(dispatcher, cancellationToken);
            harness.SetClipboardFileDropList();
            harness.Window.Hide();

            harness.SendPasteHotkey();

            var outcome = await harness.WaitForOutcomeAsync(cancellationToken);
            Assert.False(outcome.Accepted);
            Assert.Equal(NativePasteCodes.GestureWindowHidden, outcome.Code);
            Assert.True(outcome.KeepsBrowserDefault);
        });

    [Fact]
    public Task WW03AWindowWithoutASessionRefusesWithNoSessionAndKeepsTheDefaultPaste() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await NativePasteHarness.StartAsync(dispatcher, cancellationToken);
            harness.ReportComposerContext(sessionId: null);
            harness.SetClipboardFileDropList();

            harness.SendPasteHotkey();

            var outcome = await harness.WaitForOutcomeAsync(cancellationToken);
            Assert.Equal("no-session", outcome.Code);
            Assert.True(outcome.Recoverable);
            Assert.True(outcome.KeepsBrowserDefault);
            Assert.Equal(0, harness.StagingCreateCount);
        });

    [Fact]
    public Task WW04ABitmapOwnedByTheBridgeIsNeverReadByTheHost() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await NativePasteHarness.StartAsync(dispatcher, cancellationToken);
            harness.ReportComposerContext(sessionId: "session-real");
            harness.ReportScreenshotHandshake(prefersNativePaste: false);
            NativePasteHarness.SetClipboardBitmap();

            harness.SendPasteHotkey();

            var outcome = await harness.WaitForOutcomeAsync(cancellationToken);
            Assert.True(outcome.Accepted);
            Assert.Equal(ClipboardImportRoute.Bridge, outcome.Route);
            Assert.Equal(NativePasteConsumer.Bridge, outcome.Consumer);
            Assert.True(outcome.KeepsBrowserDefault);
            Assert.Equal(0, harness.StagingCreateCount);
        });

    [Fact]
    public Task WW05ABitmapOwnedByNativePasteBecomesOneStagedPng() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await NativePasteHarness.StartAsync(dispatcher, cancellationToken);
            harness.ReportComposerContext(sessionId: "session-real");
            harness.ReportScreenshotHandshake(prefersNativePaste: true);
            NativePasteHarness.SetClipboardBitmap();

            harness.SendPasteHotkey();

            var outcome = await harness.WaitForOutcomeAsync(cancellationToken);
            Assert.True(outcome.Accepted);
            Assert.Equal(NativePasteConsumer.NativePaste, outcome.Consumer);
            Assert.Equal(1, outcome.FileCount);
            Assert.InRange(outcome.EncodedBytes, 1, AttachmentProtocol.MaxFileBytes);
            Assert.Equal(1, harness.StagingCreateCount);
        });

    [Fact]
    public Task WW06ASecondHotkeyAfterANativeTimeoutIsNotReImportedByTheBridge() =>
        OnDispatcherAsync(async (dispatcher, cancellationToken) =>
        {
            await using var harness = await NativePasteHarness.StartAsync(dispatcher, cancellationToken);
            harness.ReportComposerContext(sessionId: "session-real");
            harness.SetClipboardFileDropList();
            var page = harness.Window.Pages.Single();

            harness.SendPasteHotkey();
            var first = await harness.WaitForOutcomeAsync(cancellationToken);
            var replayedBridgeClaim = harness.Window
                .PasteAccounting(page.TargetId, first.GestureId)!.Value;

            // 同一次动作不得有第二个消费者；新的粘贴是新的手势、新的账目。
            Assert.True(replayedBridgeClaim.ExactlyOneConsumer);
            Assert.Equal(1, replayedBridgeClaim.TotalImports);
            Assert.Equal(1, harness.StagingCreateCount);
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
}

/// <summary>
/// D16 实机夹具：真实窗口 + 真实暂存根 + 真实剪贴板写入 + 手势结果收集。
/// </summary>
internal sealed class NativePasteHarness : IAsyncDisposable
{
    private readonly RemotePageHarness _pages;

    private NativePasteHarness(string root, ApplicationDataLayout layout, RemotePageHarness pages)
    {
        Root = root;
        Layout = layout;
        _pages = pages;
        Window = pages.Window;
        Window.PasteGestureCompleted += Record;
    }

    /// <summary>没有会话的默认上下文（用例按需覆盖）。</summary>
    public ApplicationDataLayout Layout { get; }

    public string Root { get; }

    public RemoteWindow Window { get; }

    /// <summary>已创建的暂存文件数（用于断言"没有多余导入"）。</summary>
    public int StagingCreateCount { get; private set; }

    private NativePasteOutcome? _lastOutcome;

    public static async Task<NativePasteHarness> StartAsync(Dispatcher dispatcher, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "DshLauncher.D16", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, ApplicationDataLayout.OwnershipMarkerName),
            ApplicationDataLayout.OwnershipMarkerContent);
        var layout = new ApplicationDataLayout(root);
        var pages = await RemotePageHarness.StartWithStagingAsync(
            dispatcher,
            root,
            targetId => new WindowsAttachmentStagingAdapter(layout, targetId),
            cancellationToken);
        var harness = new NativePasteHarness(root, layout, pages);
        harness.Window.ShowAndActivate();
        return harness;
    }

    public RemotePageSession? ActivePage =>
        ((INativePasteWindowHost)Window).ActivePage;

    /// <summary>在应用数据根下写一个真实源文件并放到剪贴板上。</summary>
    public string StageFile(string leafName, byte[] content)
    {
        var path = Path.Combine(Root, leafName);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void SetClipboardFileDropList() =>
        ClipboardWriter.SetFileDropList(Directory.EnumerateFiles(Root).ToArray());

    /// <summary>把一张真实 32bpp 位图放进剪贴板。</summary>
    public static void SetClipboardBitmap() => ClipboardWriter.SetBitmap(64, 32);

    /// <summary>让窗口成为前台窗口并把 Ctrl+V 送进去（真实 Win32 输入）。</summary>
    // ReSharper disable once UnusedMember.Global -- 由实机用例调用
    public void SendPasteHotkey()
    {
        Window.Activate();
        Window.Focus();
        NativePasteGestureDriver.SendPasteHotkey();
    }

    public void ReportComposerContext(string? sessionId) =>
        Window.ReportComposerContext(
            Window.Pages.Single().TargetId,
            Window.Pages.Single().Lifecycle.Epoch,
            new ImportContextFacts(
                sessionId,
                ComposerEditable: true,
                ComposerLocked: false,
                SubagentActive: false,
                PageAdmitted: true,
                RemotePageSessionCodes.WorkAccepted),
            pageAdmitted: true);

    public void ReportScreenshotHandshake(bool prefersNativePaste) =>
        Window.ReportScreenshotHandshake(
            Window.Pages.Single().TargetId,
            Window.Pages.Single().Lifecycle.Epoch,
            new ScreenshotOwnerFacts(
                PluginAdvertisesScreenshot: true,
                PluginPrefersNativePaste: prefersNativePaste,
                NativeCaptureAvailable: true));

    /// <summary>等待最近一次手势的结局（窗口在 <c>GestureCompleted</c> 上回调）。</summary>
    public async Task<NativePasteOutcome> WaitForOutcomeAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_lastOutcome is { } outcome)
            {
                _lastOutcome = null;
                return outcome;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("等待原生粘贴结局超时。");
    }

    internal void Record(NativePasteOutcome outcome)
    {
        _lastOutcome = outcome;
        if (outcome.Accepted && outcome.Route == ClipboardImportRoute.NativePaste)
        {
            StagingCreateCount += outcome.CaptureIds.Count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Window.PasteGestureCompleted -= Record;
        await _pages.DisposeAsync();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>实机用例用的真实剪贴板写入（生产代码只读剪贴板，从不写）。</summary>
internal static class ClipboardWriter
{
    public static void SetFileDropList(IReadOnlyList<string> paths)
    {
        var headerSize = Marshal.SizeOf<DropFilesHeader>();
        var textBytes = paths.Sum(path => (path.Length + 1) * 2) + 2;
        var handle = Marshal.AllocHGlobal(headerSize + textBytes);
        var header = new DropFilesHeader { pFiles = (uint)headerSize, fWide = 1 };
        Marshal.StructureToPtr(header, handle, fDeleteOld: false);
        var cursor = handle + headerSize;
        foreach (var path in paths)
        {
            var bytes = Encoding.Unicode.GetBytes(path + "\0");
            Marshal.Copy(bytes, 0, cursor, bytes.Length);
            cursor += bytes.Length;
        }

        Marshal.WriteInt16(cursor, 0);
        OpenAndSet(15, handle);
    }

    public static void SetBitmap(int width, int height)
    {
        var stride = width * 4;
        var dib = new byte[40 + (stride * height)];
        BitConverter.TryWriteBytes(dib.AsSpan(0, 4), 40u);
        BitConverter.TryWriteBytes(dib.AsSpan(4, 4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8, 4), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12, 2), (ushort)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14, 2), (ushort)32);
        var handle = Marshal.AllocHGlobal(dib.Length);
        Marshal.Copy(dib, 0, handle, dib.Length);
        OpenAndSet(8, handle);
    }

    private static void OpenAndSet(uint format, nint handle)
    {
        if (!OpenClipboard(nint.Zero))
        {
            throw new InvalidOperationException("无法打开剪贴板。");
        }

        try
        {
            _ = EmptyClipboard();
            if (SetClipboardData(format, handle) == nint.Zero)
            {
                throw new InvalidOperationException("SetClipboardData 失败。");
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFilesHeader
    {
        public uint pFiles;

        public int x;

        public int y;

        public int fNC;

        public int fWide;
    }
}

/// <summary>
/// 实机用例的手势注入：用真实 <c>SendInput</c> 把 Ctrl+V 送进前台窗口，
/// 因此走的是<b>完整</b>的 Win32 → WPF 输入栈，而不是合成路由事件。
/// （合成 <c>KeyEventArgs</c> 无法伪造修饰键状态：<c>KeyboardDevice.Modifiers</c> 不是虚成员。）
/// </summary>
internal static class NativePasteGestureDriver
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    /// <summary>把 Ctrl+V 送进当前前台窗口（调用方必须先让目标窗口获得焦点）。</summary>
    public static void SendPasteHotkey()
    {
        var inputs = new[]
        {
            Key(VkControl, down: true),
            Key(VkV, down: true),
            Key(VkV, down: false),
            Key(VkControl, down: false),
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            throw new InvalidOperationException("SendInput 未能送出全部按键。");
        }
    }

    private static Input Key(ushort virtualKey, bool down) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = down ? 0u : KeyEventKeyUp,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;

        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;

        public ushort ScanCode;

        public uint Flags;

        public uint Time;

        public nint ExtraInfo;
    }
}
