namespace RazerMini.Core.Devices;

/// <summary>
/// 一次"读电量"失败属于哪一类。**取代"从错误文案里 grep 关键字"**。
///
/// 以前 <c>DeviceConnector</c> 靠 <c>error.Contains("不支持")</c> 决定换不换候选接口、
/// <c>TrayApp</c> 靠 <c>Contains("1167")</c> 判断句柄是否已死 —— 改一句提示文案就会静默失效，
/// 而且这种耦合在代码里看不出来。现在每个驱动自己给出语义，调用方只 <c>switch</c>。
///
/// 枚举值的**顺序有意义**（见 <see cref="ReadOutcomes.Escalate"/>）：越大越"该采取动作"。
/// </summary>
public enum ReadOutcome
{
    /// <summary>读到了可信的电量。</summary>
    Ok = 0,

    /// <summary>
    /// 应答结构合法但内容不可信（如雷柏休眠时回 <c>charge=0x70/percent=0</c>、块全 0）。
    /// 与"没应答"一样是**设备暂时不在线**的典型表现，不应当成数据，也不该剔除设备。
    /// </summary>
    Implausible = 1,

    /// <summary>设备在线（收发器/句柄都在）但没有应答 —— 无线设备休眠/关机。</summary>
    NoResponse = 2,

    /// <summary>其它失败：设备明确报错、协议拒绝、未知状态码。</summary>
    Failure = 3,

    /// <summary>该设备/固件**没有这条命令**（雷蛇 007E 充电底座回 0x05）→ 换下一个候选接口。</summary>
    Unsupported = 4,

    /// <summary>句柄权限不够（只读/零权限读不到主动查询类设备）→ 换下一种访问方式。</summary>
    NotWritable = 5,

    /// <summary>句柄失效 / 设备已拔出（Win32 6 / 1167 / 1168）→ 可以剔除并重开。</summary>
    HandleDead = 6,
}

public static class ReadOutcomes
{
    /// <summary>合并多次尝试的结论：取"最能决定动作"的那个（越大越强）。</summary>
    public static ReadOutcome Escalate(ReadOutcome current, ReadOutcome other) =>
        (int)other > (int)current ? other : current;

    /// <summary>HID 写/读失败的 Win32 码 → 语义（各驱动的映射保持一处）。</summary>
    public static ReadOutcome FromWin32(int code) => code switch
    {
        5 => ReadOutcome.NotWritable,
        6 or 1167 or 1168 => ReadOutcome.HandleDead,
        _ => ReadOutcome.Failure,
    };

    /// <summary>设备在线、只是暂时读不到（休眠/关机）：界面上显示"上次读数"，不报错、不剔除。</summary>
    public static bool IsTransient(this ReadOutcome outcome) =>
        outcome is ReadOutcome.NoResponse or ReadOutcome.Implausible;

    /// <summary>日志里的人话（英文枚举名 + 一句中文）。</summary>
    public static string Describe(this ReadOutcome outcome) => outcome switch
    {
        ReadOutcome.Ok => "成功",
        ReadOutcome.Implausible => "应答不可信（设备多半休眠）",
        ReadOutcome.NoResponse => "设备无应答（休眠/关机）",
        ReadOutcome.Failure => "设备报错",
        ReadOutcome.Unsupported => "该设备不支持这条命令",
        ReadOutcome.NotWritable => "句柄权限不足",
        ReadOutcome.HandleDead => "句柄失效/设备已拔出",
        _ => outcome.ToString(),
    };
}
