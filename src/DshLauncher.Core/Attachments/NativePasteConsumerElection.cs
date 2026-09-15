namespace DshLauncher.Core.Attachments;

/// <summary>一次手势的消费者。</summary>
public enum NativePasteConsumer
{
    /// <summary>还没有消费者。</summary>
    None,

    /// <summary>宿主原生 paste（CF_HDROP / 位图截图）。</summary>
    NativePaste,

    /// <summary>页面桥（浏览器真实 paste）。</summary>
    Bridge,
}

/// <summary>选举/占用的判定结果。</summary>
public readonly record struct NativePasteConsumerDecision(bool Accepted, string Code, string Detail);

/// <summary>
/// 一次手势的消费者账目：谁被选、谁真的消费了、各自导入了几次、是否被原生侧放弃。
/// <see cref="ExactlyOneConsumer"/> 是"同一个动作只有一个消费者"的可断言形式。
/// </summary>
public readonly record struct NativePasteGestureAccounting(
    string GestureId,
    ClipboardImportRoute Route,
    NativePasteConsumer Elected,
    NativePasteConsumer Claimed,
    int NativeImports,
    int BridgeImports,
    bool NativeAbandoned,
    string? LastCode)
{
    /// <summary>本动作实际发生的导入次数总和。</summary>
    public int TotalImports => NativeImports + BridgeImports;

    /// <summary>没有任何消费者，或恰好一个消费者完成了一次导入。</summary>
    public bool ExactlyOneConsumer => TotalImports == 0 || (Claimed != NativePasteConsumer.None && TotalImports == 1);

    /// <summary>原生侧已放弃（超时/失败），此动作不得再由桥重新导入。</summary>
    public bool BridgeMustNotImport => NativeAbandoned;
}

/// <summary>
/// D16 唯一消费者账本（平台中立状态机）。规则：
/// <list type="bullet">
/// <item>一次手势在<b>开始时</b>按当时的截图所有者模式选举消费者：文件列表恒为原生，
/// 位图按模式 native-paste 或 bridge；模式未定（<see cref="ScreenshotOwnerMode.Undecided"/>）或无需附件时不给选举；</item>
/// <item>被选中的消费者只能 <see cref="Claim"/> 一次：第二次（无论谁）都得
/// <c>consumer-already-elected</c>，因此"同一动作导入两次"在结构上不可能；</item>
/// <item>原生侧超时/失败必须显式 <see cref="Abandon"/>：此后桥的占用请求得
/// <c>consumer-bridge-after-native-timeout</c>，<b>不</b>会补一次桥导入；</item>
/// <item>模式切换只影响<b>后续</b>手势：选举发生时就固定了 route，之后改模式不改本动作；</item>
/// <item>账目上界固定，不无界堆积。</item>
/// </list>
/// 本类型不含任何 P/Invoke、不接触剪贴板与页面通道。
/// </summary>
public sealed class NativePasteConsumerLedger
{
    /// <summary>同时跟踪的手势数上界。</summary>
    public const int MaxTrackedGestures = 64;

    /// <summary>选举成功（原生）。</summary>
    public const string ElectedNativeCode = "consumer-elected-native-paste";

    /// <summary>选举成功（桥）。</summary>
    public const string ElectedBridgeCode = "consumer-elected-bridge";

    /// <summary>该手势不应有附件消费者（纯文本/拒绝）。</summary>
    public const string NotElectedCode = "consumer-not-elected";

    /// <summary>该手势从未在本账本选举过。</summary>
    public const string UnknownGestureCode = "consumer-unknown-gesture";

    /// <summary>消费者占用成功（本动作至多一次）。</summary>
    public const string ClaimedCode = "consumer-claimed";

    /// <summary>已有消费者占用过：拒绝第二次导入。</summary>
    public const string AlreadyElectedCode = "consumer-already-elected";

    /// <summary>原生侧已放弃该手势。</summary>
    public const string NativeAbandonedCode = "consumer-native-abandoned";

    /// <summary>原生侧超时/失败后，桥不得补导入。</summary>
    public const string BridgeAfterNativeTimeoutCode = "consumer-bridge-after-native-timeout";

    /// <summary>账本已满。</summary>
    public const string RegistryFullCode = "consumer-registry-full";

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    private sealed class Entry
    {
        public ClipboardImportRoute Route { get; init; }

        public NativePasteConsumer Elected { get; init; }

        public NativePasteConsumer Claimed { get; set; }

        public int NativeImports { get; set; }

        public int BridgeImports { get; set; }

        public bool NativeAbandoned { get; set; }

        public string? LastCode { get; set; }
    }

    /// <summary>被拒绝的重复导入请求次数（测试与 UI 可据此断言"没有双导入"）。</summary>
    public int RefusedDuplicateClaims { get; private set; }

    /// <summary>被拒绝的"原生超时后补桥导入"次数。</summary>
    public int RefusedBridgeAfterNativeTimeout { get; private set; }

    /// <summary>当前跟踪的手势数。</summary>
    public int Count => _entries.Count;

    /// <summary>一次手势开始时按路由选举唯一消费者。</summary>
    public NativePasteConsumerDecision Elect(string gestureId, ClipboardImportRoute route)
    {
        if (string.IsNullOrWhiteSpace(gestureId))
        {
            return new NativePasteConsumerDecision(false, UnknownGestureCode, "手势 id 为空");
        }

        if (route is ClipboardImportRoute.NativePaste)
        {
            return Record(gestureId, route, NativePasteConsumer.NativePaste);
        }

        if (route is ClipboardImportRoute.Bridge)
        {
            return Record(gestureId, route, NativePasteConsumer.Bridge);
        }

        // 纯文本与拒绝：不给任何消费者（页面保留原行为）。
        return new NativePasteConsumerDecision(
            false,
            NotElectedCode,
            $"路由 {route} 不产生附件消费者");
    }

    /// <summary>占用手势的消费者。同一手势至多一次；原生放弃后桥不得占用。</summary>
    public NativePasteConsumerDecision Claim(string gestureId, NativePasteConsumer consumer)
    {
        if (consumer == NativePasteConsumer.None)
        {
            return new NativePasteConsumerDecision(false, NotElectedCode, "消费者不能是 None");
        }

        if (string.IsNullOrWhiteSpace(gestureId) || !_entries.TryGetValue(gestureId, out var entry))
        {
            return new NativePasteConsumerDecision(false, UnknownGestureCode, "该手势不在本账本里");
        }

        if (entry.Claimed != NativePasteConsumer.None)
        {
            RefusedDuplicateClaims++;
            entry.LastCode = AlreadyElectedCode;
            return new NativePasteConsumerDecision(
                false,
                AlreadyElectedCode,
                $"该手势已由 {entry.Claimed} 消费（同一动作只有一个消费者）");
        }

        if (entry.NativeAbandoned)
        {
            if (consumer == NativePasteConsumer.Bridge)
            {
                RefusedBridgeAfterNativeTimeout++;
                entry.LastCode = BridgeAfterNativeTimeoutCode;
                return new NativePasteConsumerDecision(
                    false,
                    BridgeAfterNativeTimeoutCode,
                    "原生 paste 已超时/失败：不得再由桥补导入同一动作");
            }

            entry.LastCode = NativeAbandonedCode;
            return new NativePasteConsumerDecision(
                false,
                NativeAbandonedCode,
                "原生 paste 已放弃该手势");
        }

        if (consumer != entry.Elected)
        {
            entry.LastCode = NotElectedCode;
            return new NativePasteConsumerDecision(
                false,
                NotElectedCode,
                $"该手势选举的消费者是 {entry.Elected}，不是 {consumer}");
        }

        entry.Claimed = consumer;
        if (consumer == NativePasteConsumer.NativePaste)
        {
            entry.NativeImports++;
        }
        else
        {
            entry.BridgeImports++;
        }

        entry.LastCode = ClaimedCode;
        return new NativePasteConsumerDecision(true, ClaimedCode, $"{consumer} 已占用该手势");
    }

    /// <summary>
    /// 原生侧放弃一次手势（剪贴板 busy/超时/损坏/来源不支持等）。
    /// 只有已选举原生且尚未占用的手势可以放弃；账本里还没有该手势时会先立一块<b>墓碑</b>
    /// （原生接管过但没完成），因此放弃之后同一手势的桥导入一律被拒绝，
    /// 用户重试即产生<b>新的</b>手势与新的账目。
    /// </summary>
    public NativePasteConsumerDecision Abandon(string gestureId, string code, string detail)
    {
        if (string.IsNullOrWhiteSpace(gestureId))
        {
            return new NativePasteConsumerDecision(false, UnknownGestureCode, "手势 id 为空");
        }

        if (!_entries.TryGetValue(gestureId, out var entry))
        {
            Record(gestureId, ClipboardImportRoute.NativePaste, NativePasteConsumer.NativePaste);
            entry = _entries[gestureId];
        }

        if (entry.Claimed != NativePasteConsumer.None)
        {
            entry.LastCode = AlreadyElectedCode;
            return new NativePasteConsumerDecision(
                false,
                AlreadyElectedCode,
                "该手势已经被消费过，不能再放弃");
        }

        if (entry.Elected != NativePasteConsumer.NativePaste)
        {
            entry.LastCode = NotElectedCode;
            return new NativePasteConsumerDecision(false, NotElectedCode, "该手势不是原生 paste 手势");
        }

        entry.NativeAbandoned = true;
        entry.LastCode = string.IsNullOrEmpty(code) ? NativeAbandonedCode : code;
        return new NativePasteConsumerDecision(true, entry.LastCode, detail);
    }

    /// <summary>读取账目；手势不在账本里时为 <c>null</c>。</summary>
    public NativePasteGestureAccounting? Accounting(string gestureId)
    {
        if (string.IsNullOrWhiteSpace(gestureId) || !_entries.TryGetValue(gestureId, out var entry))
        {
            return null;
        }

        return new NativePasteGestureAccounting(
            gestureId,
            entry.Route,
            entry.Elected,
            entry.Claimed,
            entry.NativeImports,
            entry.BridgeImports,
            entry.NativeAbandoned,
            entry.LastCode);
    }

    /// <summary>丢弃一个手势的账目（例如批次已关闭且用户已看到结果）。</summary>
    public bool Forget(string gestureId) => !string.IsNullOrEmpty(gestureId) && _entries.Remove(gestureId);

    private NativePasteConsumerDecision Record(string gestureId, ClipboardImportRoute route, NativePasteConsumer elected)
    {
        if (!_entries.ContainsKey(gestureId))
        {
            if (_entries.Count >= MaxTrackedGestures)
            {
                _entries.Remove(_order.Dequeue());
            }

            _order.Enqueue(gestureId);
        }

        _entries[gestureId] = new Entry
        {
            Route = route,
            Elected = elected,
            LastCode = elected == NativePasteConsumer.NativePaste ? ElectedNativeCode : ElectedBridgeCode,
        };

        return new NativePasteConsumerDecision(
            true,
            elected == NativePasteConsumer.NativePaste ? ElectedNativeCode : ElectedBridgeCode,
            $"已选举 {elected} 为唯一消费者");
    }
}
