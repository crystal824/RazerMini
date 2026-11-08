namespace RazerMini.Core.Devices;

/// <summary>
/// 漫步者的**配置页 IO**：按 24 字节分块读、整页写，外面用 `0x01` / `0x02` 夹住（与官方 App 一致）。
/// </summary>
public sealed partial class EdifierHecate
{
    /// <summary>读页遇到"整页全 0"时的重试次数与间隔（鼠标刚唤醒时会先回空帧）。</summary>
    private const int PageReadAttempts = 2;

    private const int PageRetryGapMs = 60;

    /// <summary>
    /// 按 24 字节分块读一个配置页，与官方 App 的行为一致：
    /// 页 0x05 = 84 字节（0x18×3 + 0x0C）、页 0x07 = 126 字节（0x18×5 + 0x06）、页 0x03 = 34 字节（0x18 + 0x0A）。
    /// </summary>
    public bool TryReadPage(byte page, int length, out byte[] data, out string error)
    {
        data = new byte[Math.Max(length, 0)];
        error = string.Empty;
        if (length <= 0) return true;

        for (int attempt = 1; attempt <= PageReadAttempts; attempt++)
        {
            if (TryReadPageOnce(page, length, out data, out error) && !IsAllZero(data))
            {
                return true;
            }

            if (string.IsNullOrEmpty(error))
            {
                // 整页全 0：设备只回了帧头没填数据。真机上鼠标刚唤醒时十有八九是这个，
                // 直接当成"页面不对"会让上层报一条看不懂的错（用户 2026-10-26 就撞上了）。
                error = $"页 0x{page:X2} 读回整页是空的（设备只回了帧头，多半刚唤醒）";
            }

            _log.Debug($"页 0x{page:X2} 第 {attempt}/{PageReadAttempts} 次没读到有效数据：{error}");
            if (attempt < PageReadAttempts)
            {
                Thread.Sleep(PageRetryGapMs);
            }
        }

        return false;
    }

    /// <summary>读一次指定页（不做"全 0 重试"）。</summary>
    private bool TryReadPageOnce(byte page, int length, out byte[] data, out string error)
    {
        data = new byte[length];
        error = string.Empty;

        // 照抄官方 App：每次读写配置都先用 0x01 开一次配置会话，读完用 0x02 收尾。
        // 不这么做的话，长时间空闲后设备不再认旧会话，读页只回"帧头 + 全 0 数据" →
        // 上层看到 DPI 字段 = 0（用户 2026-10-26 在托盘里改 DPI 就是这样失败的）。
        TryBeginConfigBurst();

        int offset = 0;
        while (offset < length)
        {
            int chunk = Math.Min(PageChunk, length - offset);
            if (!TryExchange(page, (byte)chunk, (byte)offset, ReadOnlySpan<byte>.Empty,
                    out byte[] reply, out string chunkError, out _))
            {
                error = $"页 0x{page:X2} 偏移 0x{offset:X2} 读失败：{chunkError}";
                TryEndConfigBurst();
                return false;
            }

            ReadOnlySpan<byte> span = reply;
            if (!TryLocatePayload(span, out int p) || span.Length < p + DataOffset + chunk)
            {
                error = $"页 0x{page:X2} 偏移 0x{offset:X2} 应答太短（{reply.Length} 字节）";
                TryEndConfigBurst();
                return false;
            }

            span.Slice(p + DataOffset, chunk).CopyTo(data.AsSpan(offset));
            offset += chunk;
        }

        TryEndConfigBurst();
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b != 0) return false;
        }

        return true;
    }

    // ⚠️ 这里**故意没有**"读当前档位"的方法。
    //
    // 曾经写过一个 `TryReadStage()`：发 `0x28` 长度 1、数据字节填 0，把应答里的 [8] 当"当前档位"。
    // 2026-10-26 对照抓包才看清 —— `0x28` 是**设档位**（App 每次发完它就去写页 0x05 偏移 12），
    // 所以那个"读"实际是**把鼠标切换到第 1 档**，而且读回的还是我们自己发出去的回显字节，
    // 等于既改坏了状态、又给了个假读数。
    //
    // 当前档位请从页 `0x05` 偏移 <see cref="SettingsStageIndexOffset"/> 读（App 就是这么记录的）。
}
