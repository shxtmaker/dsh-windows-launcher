using System.Diagnostics.CodeAnalysis;

namespace DshLauncher.Core.Attachments;

/// <summary>
/// 注入的字节源（D11）：协调器只经这个接口取原始字节，因此不接触任何 Windows API，
/// 也能在 Linux 上由假源精确驱动。契约：
/// <list type="bullet">
/// <item><see cref="Read"/> 允许<b>短读</b>：返回值可以小于请求长度，协调器负责累计；</item>
/// <item>返回 0 表示 <b>EOF</b>（流已结束），不是错误；</item>
/// <item>返回负数或大于请求长度是源故障；抛出 <see cref="IOException"/> 等异常同样是源故障，
/// 两者都转化为稳定的本地码 <see cref="AttachmentCoordinatorCodes.SourceError"/>；</item>
/// <item>协调器在传输完成、取消、身份失效或自身释放时都会释放源，测试用假源逐路径断言。</item>
/// </list>
/// </summary>
public interface IAttachmentByteSource : IDisposable
{
    /// <summary>file-begin.byteLength：声明的原始字节数（0..<see cref="AttachmentProtocol.MaxFileBytes"/>）。</summary>
    int ByteLength { get; }

    /// <summary>读取至多 <c>destination.Length</c> 字节；短读合法，0 表示 EOF。</summary>
    int Read(Span<byte> destination);
}

/// <summary>
/// 注入的消息通道（D11）：<see cref="Send"/> 发出一条完整 wire JSON 帧后立即返回，
/// 不等待对端；<see cref="TryReceive"/> 非阻塞取一条入站帧（没有则返回 false）。
/// 通道自身不阻塞、不睡眠，协调器因此不需要后台线程或定时器。
/// </summary>
public interface IAttachmentChannel
{
    /// <summary>发送一条 wire 帧（UTF-8 JSON 文本）。</summary>
    void Send(string wireJson);

    /// <summary>取一条入站帧；没有待处理消息时返回 false，绝不阻塞。</summary>
    bool TryReceive([NotNullWhen(true)] out string? wireJson);
}

/// <summary>
/// 注入的时钟（D11）：Ack/import-result/空闲三类超时与去重缓存的生命周期全部只读它。
/// 生产实现是 <see cref="SystemAttachmentClock"/>；测试用可控时钟直接推进毫秒，
/// 不需要 sleep，也不受机器负载影响。
/// </summary>
public interface IAttachmentClock
{
    long NowMs { get; }
}

/// <summary>生产时钟：整个协调器里唯一读取系统时间的位置。</summary>
public sealed class SystemAttachmentClock : IAttachmentClock
{
    /// <summary>无状态单例。</summary>
    public static SystemAttachmentClock Instance { get; } = new();

    private SystemAttachmentClock()
    {
    }

    public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>全局并发闸门的判定结果。</summary>
public enum AttachmentGateOutcome
{
    /// <summary>已持有全局槽位，可以开始传输。</summary>
    Active,

    /// <summary>已进入有界等待队列；本次未获槽位，调用方必须保持原状（不读字节）。</summary>
    Queued,

    /// <summary>等待队列已满：本次不排队（队列绝不无界增长），调用方保持 pending。</summary>
    Refused,
}

/// <summary>
/// 全局并发闸门（D11）：同时传输的目标数不超过 <see cref="MaxConcurrent"/>
/// （构造时即受冻结的 <see cref="AttachmentProtocol.MaxConcurrentTargets"/> 约束），
/// 等待队列容量固定为 <see cref="QueueCapacity"/>。因此
/// <see cref="WaitingCount"/> 永远 ≤ <see cref="QueueCapacity"/>，而
/// <see cref="ActiveCount"/> 永远 ≤ <see cref="MaxConcurrent"/>——两者都有硬上界。
/// 闸门只在目标真正开始传输时被占用，释放时按 FIFO 提升队首。
/// </summary>
public sealed class AttachmentConcurrencyGate
{
    /// <summary>默认等待队列容量（固定常量，可配置但有上界）。</summary>
    public const int DefaultQueueCapacity = 32;

    /// <summary>允许配置的最大等待队列容量，防止"有界"被配置成无界。</summary>
    public const int MaxQueueCapacity = 1024;

    private readonly List<string> _active = [];
    private readonly List<string> _waiting = [];

    public AttachmentConcurrencyGate(
        int maxConcurrent = AttachmentProtocol.MaxConcurrentTargets,
        int queueCapacity = DefaultQueueCapacity)
    {
        if (maxConcurrent < 1 || maxConcurrent > AttachmentProtocol.MaxConcurrentTargets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrent),
                maxConcurrent,
                $"并发上限必须在 1..{AttachmentProtocol.MaxConcurrentTargets} 之间");
        }

        if (queueCapacity < 0 || queueCapacity > MaxQueueCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueCapacity),
                queueCapacity,
                $"等待队列容量必须在 0..{MaxQueueCapacity} 之间");
        }

        MaxConcurrent = maxConcurrent;
        QueueCapacity = queueCapacity;
    }

    /// <summary>全局同时传输的目标数上限。</summary>
    public int MaxConcurrent { get; }

    /// <summary>等待队列容量（硬上界）。</summary>
    public int QueueCapacity { get; }

    /// <summary>当前持有槽位的键数，≤ <see cref="MaxConcurrent"/>。</summary>
    public int ActiveCount => _active.Count;

    /// <summary>当前在队键数，≤ <see cref="QueueCapacity"/>。</summary>
    public int WaitingCount => _waiting.Count;

    /// <summary>因队列已满而拒绝入队的次数。</summary>
    public int RefusedCount { get; private set; }

    /// <summary>当前持有槽位的键（只读快照）。</summary>
    public IReadOnlyList<string> ActiveKeys => [.. _active];

    /// <summary>当前排队中的键（只读快照）。</summary>
    public IReadOnlyList<string> WaitingKeys => [.. _waiting];

    /// <summary>申请槽位：已持有返回 <see cref="AttachmentGateOutcome.Active"/>；已在队列返回 Queued；
    /// 否则有空位即激活，队列未满即入队，队列已满即拒绝（不入队）。</summary>
    public AttachmentGateOutcome TryAcquire(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_active.Contains(key)) return AttachmentGateOutcome.Active;
        if (_waiting.Contains(key)) return AttachmentGateOutcome.Queued;
        if (_active.Count < MaxConcurrent)
        {
            _active.Add(key);
            return AttachmentGateOutcome.Active;
        }

        if (_waiting.Count < QueueCapacity)
        {
            _waiting.Add(key);
            return AttachmentGateOutcome.Queued;
        }

        RefusedCount += 1;
        return AttachmentGateOutcome.Refused;
    }

    /// <summary>是否已持有槽位。</summary>
    public bool IsActive(string key) => _active.Contains(key);

    /// <summary>释放槽位；若释放的是活动槽位且队列非空，则按 FIFO 提升队首为活动。
    /// 取消排队中的键同样只用这一个方法（从队列移除）。</summary>
    public void Release(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_active.Remove(key))
        {
            if (_waiting.Count > 0)
            {
                _active.Add(_waiting[0]);
                _waiting.RemoveAt(0);
            }

            return;
        }

        _waiting.Remove(key);
    }
}

/// <summary>
/// 协调器的<b>本地</b>判定码。线协议（D10）只承载冻结的
/// <see cref="AttachmentProtocol.ErrorCodes"/> 与 <see cref="AttachmentProtocol.WireErrorCodes"/>；
/// 下面这些码只出现在本地逐文件记录与 trace 里，<b>绝不</b>写进任何报文，
/// 因而不会污染冻结字段集。与线协议语义重合的失败（如 size-mismatch、hash-mismatch）
/// 直接复用冻结码，不另造同义码。
/// </summary>
public static class AttachmentCoordinatorCodes
{
    /// <summary>字节源读取失败（抛异常或返回非法长度）。</summary>
    public const string SourceError = "source-error";

    /// <summary>已发送的块在注入时钟下超时未获 ack。</summary>
    public const string AckTimeout = "ack-timeout";

    /// <summary>file-end 之后在注入时钟下超时未获 import-result。</summary>
    public const string ImportTimeout = "import-timeout";

    /// <summary>批次空闲超时（含全局并发槽位长期不可得的情况）。</summary>
    public const string IdleTimeout = "idle-timeout";

    /// <summary>全局并发闸门等待队列已满，本次仍未获槽位（文件保持 pending，不读字节）。</summary>
    public const string Starved = "queue-starved";

    /// <summary>协调器已释放。</summary>
    public const string Disposed = "disposed";
}
