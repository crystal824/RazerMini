using System.Text;

namespace RazerMini.Core.Devices;

/// <summary>
/// 雷蛇 91 字节 feature report 的帧构造与解析（Windows 口径：缓冲区含 report ID）。
///
/// 字节布局，与 openrazer 的 <c>struct razer_report</c>（90 字节）+ 1 字节 report ID 对应：
/// <code>
///   buf[0]      report ID 0x00                 （仅 Windows 缓冲区有，struct 里没有）
///   buf[1]      status                         struct[0]
///   buf[2]      transaction_id                 struct[1]
///   buf[3..4]   remaining_packets（大端）       struct[2..3]
///   buf[5]      protocol_type                  struct[4]
///   buf[6]      data_size                      struct[5]
///   buf[7]      command_class                  struct[6]
///   buf[8]      command_id                     struct[7]
///   buf[9..88]  arguments[0..79]               struct[8..87]
///   buf[89]     crc = XOR(struct[2..87])       struct[88]
///   buf[90]     reserved = 0                   struct[89]
/// </code>
/// crc 范围要以 struct 下标为准：openrazer <c>razer_calculate_crc</c> 是 <c>for (i = 2; i &lt; 88; i++)</c>，
/// struct 下标 +1 就是 Windows 缓冲区的 <c>buf[3..88]</c>。
/// </summary>
public static class RazerReport
{
    public const int Length = 91;
    public const int ArgumentCount = 80;
    public const int CrcOffset = 89;

    public const byte ReportId = 0x00;

    public const byte StatusSuccess = 0x00;
    public const byte StatusBusy = 0x01;
    public const byte StatusSuccessAlt = 0x02;
    public const byte StatusFailure = 0x03;
    public const byte StatusNoResponse = 0x04;
    public const byte StatusNotSupported = 0x05;

    /// <summary>构造一帧请求。status 固定 0、protocol 固定 0、remaining_packets 固定 0。</summary>
    public static byte[] CreateFrame(byte transactionId, byte commandClass, byte commandId, byte dataSize,
        ReadOnlySpan<byte> arguments = default)
    {
        if (arguments.Length > ArgumentCount)
        {
            throw new ArgumentException($"arguments 最多 {ArgumentCount} 字节，实际 {arguments.Length}", nameof(arguments));
        }

        var frame = new byte[Length];
        frame[0] = ReportId;
        frame[1] = 0x00;                       // status（请求永远 0）
        frame[2] = transactionId;
        frame[3] = 0x00;                       // remaining_packets 高字节
        frame[4] = 0x00;                       // remaining_packets 低字节
        frame[5] = 0x00;                       // protocol_type
        frame[6] = dataSize;
        frame[7] = commandClass;
        frame[8] = commandId;
        arguments.CopyTo(frame.AsSpan(9));
        frame[CrcOffset] = ComputeCrc(frame);
        frame[90] = 0x00;                      // reserved
        return frame;
    }

    /// <summary>crc = XOR(buf[3..88])，等价于 openrazer 的 XOR(struct[2..87])。</summary>
    public static byte ComputeCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Length)
        {
            throw new ArgumentException($"帧长度必须为 {Length}，实际 {frame.Length}", nameof(frame));
        }

        byte crc = 0;
        for (int i = 3; i <= 88; i++)
        {
            crc ^= frame[i];
        }
        return crc;
    }

    public static RazerResponse Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Length)
        {
            throw new ArgumentException($"帧长度必须为 {Length}，实际 {frame.Length}", nameof(frame));
        }

        var args = new byte[ArgumentCount];
        frame.Slice(9, ArgumentCount).CopyTo(args);
        return new RazerResponse(
            status: frame[1],
            transactionId: frame[2],
            commandClass: frame[7],
            commandId: frame[8],
            arguments: args,
            crc: frame[CrcOffset]);
    }

    public static string StatusText(byte status) => status switch
    {
        StatusSuccess => "成功(0x00)",
        StatusBusy => "忙(0x01)",
        StatusSuccessAlt => "成功(0x02)",
        StatusFailure => "失败(0x03)",
        StatusNoResponse => "设备无应答(0x04)",
        StatusNotSupported => "不支持(0x05)",
        _ => $"未知状态(0x{status:X2})",
    };

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0 && i % 16 == 0) sb.Append('\n').Append("  ");
            sb.Append(data[i].ToString("X2")).Append(' ');
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>设备回的一帧。arguments 固定 80 字节，未使用的为 0。</summary>
public readonly struct RazerResponse
{
    public RazerResponse(byte status, byte transactionId, byte commandClass, byte commandId, byte[] arguments, byte crc)
    {
        Status = status;
        TransactionId = transactionId;
        CommandClass = commandClass;
        CommandId = commandId;
        Arguments = arguments;
        Crc = crc;
    }

    public byte Status { get; }
    public byte TransactionId { get; }
    public byte CommandClass { get; }
    public byte CommandId { get; }
    public byte[] Arguments { get; }
    public byte Crc { get; }

    /// <summary>两种"成功"状态码都要认：0x00 与 0x02 在不同型号/固件上都会出现。</summary>
    public bool IsSuccess => Status is RazerReport.StatusSuccess or RazerReport.StatusSuccessAlt;

    public byte Arg(int index) => index >= 0 && index < Arguments.Length ? Arguments[index] : (byte)0;

    public ushort ArgUInt16BigEndian(int index) => (ushort)((Arg(index) << 8) | Arg(index + 1));

    public string StatusText => RazerReport.StatusText(Status);

    public override string ToString() =>
        $"status={StatusText} txn=0x{TransactionId:X2} class=0x{CommandClass:X2} id=0x{CommandId:X2} " +
        $"args={RazerReport.ToHex(Arguments.AsSpan(0, 8))}";
}
