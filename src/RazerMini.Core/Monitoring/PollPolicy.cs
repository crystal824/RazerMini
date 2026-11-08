namespace RazerMini.Core.Monitoring;

public enum LinkState
{
    /// <summary>还没探测过。</summary>
    Unknown = 0,
    Ok = 1,

    /// <summary>设备在但读失败（连续失败未到阈值）。</summary>
    Degraded = 2,

    /// <summary>设备不在（拔掉 / 接收器没插）。</summary>
    Missing = 3,
}

/// <summary>
/// 轮询节奏状态机：连着时慢慢轮（省电、省 USB 流量），断了时快速重扫（插回来自动恢复），
/// 连续失败到阈值就要求重开句柄（无线设备睡醒后旧句柄会失效）。
/// 纯逻辑，Linux 上可断言。
/// </summary>
public sealed class PollPolicy
{
    private readonly int _failuresBeforeReopen;

    public PollPolicy(
        TimeSpan connectedInterval,
        TimeSpan disconnectedInterval,
        TimeSpan retryInterval,
        int failuresBeforeReopen = 3)
    {
        ConnectedInterval = connectedInterval;
        DisconnectedInterval = disconnectedInterval;
        RetryInterval = retryInterval;
        _failuresBeforeReopen = Math.Max(1, failuresBeforeReopen);
    }

    public TimeSpan ConnectedInterval { get; }
    public TimeSpan DisconnectedInterval { get; }
    public TimeSpan RetryInterval { get; }

    public LinkState State { get; private set; } = LinkState.Unknown;
    public int ConsecutiveFailures { get; private set; }
    public int TotalSuccesses { get; private set; }
    public int TotalFailures { get; private set; }

    /// <summary>连续失败达到阈值 → 调用方应释放并重开句柄。</summary>
    public bool ShouldReopen => ConsecutiveFailures >= _failuresBeforeReopen;

    public TimeSpan NextDelay => State switch
    {
        LinkState.Ok => ConnectedInterval,
        LinkState.Missing => DisconnectedInterval,
        LinkState.Degraded => RetryInterval,
        _ => TimeSpan.FromSeconds(1),
    };

    /// <summary>返回 true 表示状态发生了变化（调用方据此决定要不要打日志/弹提示）。</summary>
    public bool OnSuccess()
    {
        bool changed = State != LinkState.Ok;
        State = LinkState.Ok;
        ConsecutiveFailures = 0;
        TotalSuccesses++;
        return changed;
    }

    /// <param name="deviceMissing">true = 设备根本没出现；false = 设备在但读取失败。</param>
    public bool OnFailure(bool deviceMissing)
    {
        var newState = deviceMissing ? LinkState.Missing : LinkState.Degraded;
        bool changed = State != newState;
        State = newState;
        ConsecutiveFailures++;
        TotalFailures++;
        return changed;
    }

    public void Reset()
    {
        State = LinkState.Unknown;
        ConsecutiveFailures = 0;
    }

    public string Describe() =>
        $"state={State} 连续失败={ConsecutiveFailures} 累计成功={TotalSuccesses} 累计失败={TotalFailures} " +
        $"下次间隔={NextDelay.TotalSeconds:0.#}s";
}
