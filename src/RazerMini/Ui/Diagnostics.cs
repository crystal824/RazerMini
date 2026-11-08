using System.Runtime.InteropServices;
using System.Text;
using RazerMini.Core.Devices;
using RazerMini.Core.Hid;
using RazerMini.Core.Logging;
using RazerMini.Core.Settings;
using RazerMini.Platform;

namespace RazerMini.Ui;

/// <summary>
/// 诊断：把"能看到什么、选中了哪个通道、命令发了什么、回了什么"全部摊开。
/// 第一次在真机跑如果读数不对，先看这份报告。
/// </summary>
internal static class Diagnostics
{
    /// <summary>
    /// 诊断报告：接口枚举 + 每台设备的握手/读数实测 + 配置。封版后只保留这一份，
    /// 逆向期那套"手柄深挖 / USB 层 / 蓝牙枚举 / 指定页转储"的调试报告全部删除。
    /// </summary>
    public static string BuildReport(ILog log, AppSettings settings, bool verboseInterfaces = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RazerMini 诊断报告");
        sb.AppendLine($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"系统        : {Environment.OSVersion} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"进程        : {(Environment.Is64BitProcess ? "64 位" : "32 位")}, .NET {Environment.Version}");
        sb.AppendLine($"配置        : {AppPaths.SettingsFile}");
        sb.AppendLine($"电量口径    : {settings.BatteryScale}，读充电状态: {settings.ReadChargingStatus}");
        sb.AppendLine();

        HidEnumeration enumeration = HidEnumerator.EnumerateAll(log);
        List<HidDeviceInfo> all = enumeration.Devices;
        int lockedCount = all.Count(i => !i.Opened);
        sb.AppendLine($"── HID 接口（枚举到路径 {enumeration.PathCount} 个，解析出 {all.Count} 个，其中 {lockedCount} 个打不开）──");

        if (verboseInterfaces)
        {
            ushort[] knownVendors = DeviceCatalog.SupportedVendorIds;
            foreach (HidDeviceInfo info in all.Where(i => knownVendors.Contains(i.VendorId)))
            {
                sb.AppendLine($"  {(info.Opened ? "★" : "✗")} {info}");
                if (!info.Opened)
                {
                    sb.AppendLine($"    打不开：Win32 {info.OpenError}（{ExplainOpenError(info.OpenError)}）");
                }

                sb.AppendLine($"    路径 {info.Path}");
            }

            var others = all.Where(i => !knownVendors.Contains(i.VendorId))
                .GroupBy(i => $"{i.VendorId:X4}:{i.ProductId:X4}")
                .OrderByDescending(g => g.Count())
                .Take(12)
                .Select(g => $"{g.Key}×{g.Count()}");
            sb.AppendLine($"  其它 VID:PID（前 12）: {string.Join(", ", others)}");
        }

        sb.AppendLine();
        sb.AppendLine("── 支持的设备接口（雷蛇/雷柏/漫步者/高斯 四族判据）──");
        List<DeviceCandidate> candidates = DeviceCatalog.RankCandidates(all, log);
        if (candidates.Count == 0)
        {
            sb.AppendLine("  ✗ 没有任何接口同时满足 三族的 VID:PID + usage + 长度 判据。");
            sb.AppendLine("    排查：设备是否插好？上面的接口列表里有没有对应 VID:PID？usage 和长度是否和判据一致？");
            return sb.ToString().TrimEnd();
        }

        foreach (DeviceCandidate candidate in candidates)
        {
            sb.AppendLine($"  · {candidate.Describe()}");
        }

        sb.AppendLine();
        sb.AppendLine("── 手柄（Switch Pro / Xbox）──");

        sb.AppendLine();
        sb.AppendLine("── 候选逐个 × 访问权限逐个 试读 ──");
        sb.AppendLine("  同一台机器可能同时插着多只鼠标；逐个实测谁应答，而不是按枚举顺序猜。");

        List<ConnectedDevice> connected = DeviceConnector.ConnectAll(all, log, out IReadOnlyList<ConnectAttempt> attempts);

        foreach (IGrouping<string, ConnectAttempt> group in attempts.GroupBy(a => $"{a.FamilyName} {a.ModelName} [{a.Id}]"))
        {
            sb.AppendLine($"  {group.Key}");
            foreach (ConnectAttempt attempt in group)
            {
                string mark = attempt.ReadOk ? "✓" : attempt.Opened ? "·" : "✗";
                string detail = !attempt.Opened
                    ? attempt.Detail
                    : attempt.ReadOk
                        ? attempt.Detail
                        : attempt.Registered
                            ? "已在册，电量暂时读不到：" + attempt.Detail
                            : "读电量失败：" + attempt.Detail;
                sb.AppendLine($"    {mark} {attempt.AccessMode,-4}: {detail}");
            }
        }

        if (connected.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("  ✗ 没有设备能读到电量。");
            sb.AppendLine("    排查顺序：");
            sb.AppendLine("    1. 各接口都 ✗ Win32=5 → 尝试「以管理员身份运行」；仍不行多为驱动拦下");
            sb.AppendLine("    2. 打开成功但回 0x05 不支持 → 该 PID 没这条命令（如雷蛇 007E 底座）");
            sb.AppendLine("    3. 雷柏一直拿不到应答 → 鼠标休眠，动一下再跑；或该接口需要读写权限");
            sb.AppendLine("    4. 漫步者读不到状态帧 → 它只在状态变化/心跳时推帧，多等几秒或多跑几次");
            sb.AppendLine("    5. 应答内容不像电量（如雷柏回 charge=0x70 / 百分比 0、雷蛇回 0x04）→ 鼠标在休眠或关机；");
            sb.AppendLine("       接收器在线不代表鼠标在线，动一下鼠标再跑");
            return sb.ToString().TrimEnd();
        }

        foreach (ConnectedDevice entry in connected)
        {
            sb.AppendLine();
            sb.AppendLine($"══ {entry.Candidate.FamilyName} {entry.Candidate.ModelName} [{entry.Candidate.Id}] ══");
            sb.AppendLine($"  通道：{entry.Candidate.Info.Path}");
            sb.AppendLine($"  usage={entry.Candidate.Info.UsagePage:X4}/{entry.Candidate.Info.Usage:X4} " +
                          $"in={entry.Candidate.Info.InputReportLength} out={entry.Candidate.Info.OutputReportLength} " +
                          $"feat={entry.Candidate.Info.FeatureReportLength}，可写={entry.Device.CanWrite}");

            ProbeBattery(sb, entry, settings);
            ProbeDpi(sb, entry);

        }

        foreach (ConnectedDevice entry in connected)
        {
            entry.Device.Dispose();
        }

        return sb.ToString().TrimEnd();
    }

    private static void ProbeBattery(StringBuilder sb, ConnectedDevice entry, AppSettings settings)
    {
        sb.AppendLine();
        sb.AppendLine("  电量：");

        BatterySample? last = null;
        string lastError = string.Empty;

        foreach (BatteryScale scale in new[] { BatteryScale.Raw255, BatteryScale.Direct })
        {
            if (entry.Device.TryReadBattery(scale, out BatterySample sample, out string error, out _))
            {
                last = sample;
                sb.AppendLine($"    {scale,-8}: raw={sample.RawValue} (0x{sample.RawValue:X2}) → {sample.Percent}% " +
                              $"充电={Describe(sample.Charging)} {sample.RawHex}");
            }
            else
            {
                lastError = error;
                sb.AppendLine($"    {scale,-8}: 失败 — {error}");
            }
        }

        // 不再为"采用口径"那一行多读一次设备：口径只影响换算，不是新的一次采样
        if (last is { } chosen)
        {
            sb.AppendLine($"    → 采用 {settings.ParsedBatteryScale} 口径：{chosen.Percent}%");
        }
        else if (lastError.Length > 0)
        {
            sb.AppendLine($"    → 没有可信读数：{lastError}");
            sb.AppendLine("    （无线设备休眠/关机时接收器仍会应答，但内容不是电量：动一下鼠标/键盘再跑一次）");
        }
    }

    private static void ProbeDpi(StringBuilder sb, ConnectedDevice entry)
    {
        sb.AppendLine();
        sb.AppendLine("  DPI：");

        if (entry.Device is EdifierHecate edifier)
        {
            sb.AppendLine($"    读方式：{edifier.ReadMethod}");

            if (edifier.TryReadDpi(out int edX, out int edY, out string edError))
            {
                int stage = edifier.LastDpiStage ?? -1;
                sb.AppendLine($"    当前 {edX}x{edY}（第 {stage + 1}/{EdifierHecate.StageCount} 档，" +
                              $"页 0x05 +0x{EdifierHecate.DpiOffsetForStage(stage):X2}，" +
                              $"raw={edifier.LastDpiRaw} → (raw+1)×{EdifierHecate.DpiStep}）");
                sb.AppendLine("    与官方 HECATE Connect 上显示的档位/数值对一下，不一致就把 App 的数发我");
            }
            else
            {
                sb.AppendLine($"    读取失败 — {edError}");
            }

            sb.AppendLine();
            sb.AppendLine("    三个配置页原始字节（2026-10-26 抓包确认的协议）：");
            DumpEdifierPages(sb, edifier);
            return;
        }

        if (!entry.Device.SupportsDpi)
        {
            sb.AppendLine("    该型号未提供 DPI 读写");
            return;
        }

        if (!entry.Device.TryReadDpi(out int x, out int y, out string readError))
        {
            sb.AppendLine($"    读取失败 — {readError}");
            if (entry.Device is RapooVt9Pro failedRapoo)
            {
                sb.AppendLine();
                DumpRapooTables(sb, failedRapoo);
            }

            return;
        }

        sb.AppendLine($"    当前 {x}x{y}（范围 {entry.Device.MaxDpi} 上限）");

        if (entry.Device is RapooVt9Pro rapoo)
        {
            DumpRapooTables(sb, rapoo);

            // 真写一次（原样写回），否则"通路可用"只是空话：值相同时 TryWriteDpi 会提前返回
            if (rapoo.TryVerifyWritePath(out string verifyError))
            {
                sb.AppendLine("    ✓ 写入通路实测通过：把读到的字节原样写回，读回逐字节一致（配置未改变）");
            }
            else
            {
                sb.AppendLine($"    ✗ 写入通路实测失败：{verifyError}");
            }

            return;
        }

        if (entry.Device.TryWriteDpi(x, y, out string writeError))
        {
            sb.AppendLine("    ✓ 写入通路可用（写回相同值，未改变配置）");
        }
        else
        {
            sb.AppendLine($"    ✗ 写入失败：{writeError}");
        }
    }

    private static string OrMissing(string hex) => hex.Length == 0 ? "（没读到）" : hex;

    /// <summary>
    /// 采几条原始 input report（去重，只取前 16 字节）。协议不明的设备（比如走蓝牙 HID 的 Xbox 手柄）
    /// 靠这几行原始字节来定位电量字段 —— 比反复猜快得多。
    /// </summary>


    /// <summary>
    /// 手柄诊断：Xbox 走 XInput（不在 HID 世界里，必须单独列），
    /// Switch Pro 走蓝牙 HID（把接口、原始报告、0x30 模式是否切换成功全摊开）。
    /// 这两块是新功能，第一次在真机上跑时全靠这里定位问题。
    /// </summary>

    /// <summary>
    /// 摊开雷柏三块 EEPROM 原文。**读失败时也要打**：三块全 0 就是"鼠标休眠/未连接"的直接证据，
    /// 没有这几行根本分不清"DPI 真的是 0"和"设备根本没应答"（2026-10-27 那份 diag 就是这样）。
    /// </summary>
    private static void DumpRapooTables(StringBuilder sb, RapooVt9Pro rapoo)
    {
        sb.AppendLine($"    X 表 0x{RapooVt9Pro.AddrDpiX:X3}: {OrMissing(rapoo.LastXTableHex)}");
        sb.AppendLine($"    Y 表 0x{RapooVt9Pro.AddrDpiY:X3}: {OrMissing(rapoo.LastYTableHex)}");
        sb.AppendLine($"    档位 0x{RapooVt9Pro.AddrDpiCur:X3}: {OrMissing(rapoo.LastStageHex)}");
        sb.AppendLine($"    解析：{DescribeRapooTable(rapoo.LastXTableHex, rapoo.LastStageHex)}");
    }

    /// <summary>把 16 字节 X 表按 7 档 u16 小端摊开，方便一眼看出每档多少 DPI。</summary>
    private static string DescribeRapooTable(string hex, string stageHex)
    {
        if (hex.Length == 0) return "（无数据）";

        byte[] data;
        try
        {
            data = Convert.FromHexString(hex.Replace(" ", string.Empty));
        }
        catch (FormatException)
        {
            return "（hex 解析失败）";
        }

        if (data.Length < 14) return $"（只有 {data.Length} 字节，不够 7 档）";

        // 全 0 = 鼠标休眠/未连接（接收器 ACK 了一个空块），绝不能当成"DPI 是 0"
        if (data.All(b => b == 0)) return "（全 0：鼠标休眠/未与接收器连接，不是真实的 DPI 表）";

        var parts = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            int value = data[i * 2] | (data[i * 2 + 1] << 8);
            parts.Add($"{i + 1}档={value}");
        }

        string tail = data.Length >= 16 ? $"；[14]=0x{data[14]:X2} [15]=0x{data[15]:X2}" : string.Empty;

        // 两次真机读数里 [14] 都等于 0x898[0]（当前档位下标）→ 疑似固件留的同步副本，不参与判据
        string stageNote = string.Empty;
        if (data.Length >= 16 && stageHex.Length >= 2)
        {
            try
            {
                byte index = Convert.FromHexString(stageHex.Replace(" ", string.Empty))[0];
                stageNote = $"\n    当前档位下标 {index}（0x898[0]）；[14]=0x{data[14]:X2}" +
                            (data[14] == index ? " 与下标相同（疑似同步副本，判据里忽略它）" : " 与下标不同（也忽略它）");
            }
            catch (FormatException)
            {
                // 诊断信息不值得因为格式问题中断整个报告
            }
        }

        return string.Join(" ", parts) + tail + stageNote;
    }

    private static void DumpEdifierPages(StringBuilder sb, EdifierHecate edifier)
    {
        (byte Page, int Length, string Name)[] pages =
        {
            (EdifierHecate.PageDevice, EdifierHecate.DevicePageLength, "设备信息"),
            (EdifierHecate.PageSettings, EdifierHecate.SettingsPageLength, "设置/灯光"),
            (EdifierHecate.PageProfile, EdifierHecate.ProfilePageLength, "档位/按键"),
        };

        // 先读设置页：当前档位下标记在它的偏移 12 上。
        // 以前用 0x28 去"读"——那是设置命令，数据填 0 等于每跑一次 diag 就把鼠标切回第 1 档。
        byte[]? settings = null;
        if (edifier.TryReadPage(EdifierHecate.PageSettings, EdifierHecate.SettingsPageLength,
                out byte[] settingsData, out string settingsError))
        {
            settings = settingsData;
            if (settingsData.Length > EdifierHecate.SettingsStageIndexOffset)
            {
                sb.AppendLine($"    当前档位：下标 {settingsData[EdifierHecate.SettingsStageIndexOffset]}" +
                              $"（页 0x05 偏移 {EdifierHecate.SettingsStageIndexOffset}，App 切档写的就是这一格）");
            }
        }
        else
        {
            sb.AppendLine($"    设置页读取失败 — {settingsError}");
        }

        foreach ((byte page, int length, string name) in pages)
        {
            byte[]? data = page == EdifierHecate.PageSettings ? settings : null;
            if (data is null)
            {
                if (!edifier.TryReadPage(page, length, out byte[] fresh, out string pageError))
                {
                    sb.AppendLine($"    页 0x{page:X2}（{name}，{length} 字节）读取失败 — {pageError}");
                    continue;
                }

                data = fresh;
            }

            sb.AppendLine($"    页 0x{page:X2}（{name}，{length} 字节）：");
            for (int offset = 0; offset < data.Length; offset += 24)
            {
                int take = Math.Min(24, data.Length - offset);
                sb.AppendLine($"      +{offset:X2}: " +
                              string.Join(' ', data.Skip(offset).Take(take).Select(b => b.ToString("X2"))));
            }

            if (page == EdifierHecate.PageSettings)
            {
                sb.AppendLine("      7 档 DPI 表（每条 9 字节，自 0x10 起，DPI=(raw+1)×50）：" +
                              string.Join(" ", EdifierHecate.DescribeStageTable(data)));
            }

        }
    }

    /// <summary>
    /// 单独读一个指定页并打印（`--probe-page` 用）。只读，不写任何东西。
    /// 页号来自抓包（官方 App 打开的页），所以不需要靠猜。
    /// </summary>

    private static string Describe(bool? charging) => charging switch
    {
        true => "充电中",
        false => "放电中",
        null => "未知",
    };

    /// <summary>把 CreateFile 的 Win32 错误码翻译成人能用的排查提示。</summary>
    private static string ExplainOpenError(int error) => error switch
    {
        2 => "系统找不到设备（已拔出）",
        5 => "拒绝访问（权限不足，或被驱动拦下）",
        32 => "共享冲突——设备被其它进程独占（最常见：Razer Synapse 后台服务）",
        1168 => "找不到元素（设备已移除）",
        _ => "见 Win32 错误码表",
    };
}
