using System.Text;
using RazerMini.Core.Devices;
using RazerMini.Core.Hid;
using RazerMini.Core.Logging;
using RazerMini.Core.Monitoring;
using RazerMini.Core.Overlay;
using RazerMini.Core.Rendering;
using RazerMini.Core.Settings;

namespace RazerMini.Verify;

/// <summary>
/// 协议断言程序：在 Linux 上直接跑（HID 用假通道），把"帧字节、CRC、解析、重试、选择、轮询、渲染"
/// 全部真跑一遍。Windows 上才有的部分（P/Invoke、图标）不在这里验证。
/// </summary>
internal static class Verify
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static int Main(string[] args)
    {
        // 辅助模式：把托盘图标导出成裸 ARGB 文件，方便在 Linux 上肉眼检查渲染效果
        if (args.Length >= 2 && args[0] == "--dump-icons")
        {
            DumpIcons(args[1]);
            return 0;
        }

        Console.WriteLine("RazerMini 协议断言");
        Console.WriteLine(new string('=', 60));

        Section("帧构造", TestFrames);
        Section("应答解析", TestParsing);
        Section("电量换算", TestBatteryScaling);
        Section("DPI", TestDpi);
        Section("重试与错误分支", TestRetryAndErrors);
        Section("通道选择", TestDeviceSelector);
        Section("雷柏 VT9 Pro", TestRapoo);
        Section("漫步者 G3M Pro", TestEdifier);
        Section("高斯键盘", TestGanss);
        Section("Switch Pro 手柄", TestSwitchPro);
        Section("Xbox 手柄电量", TestXboxBattery);
        Section("三族排序", TestDeviceCatalog);
        Section("轮询策略", TestPollPolicy);
        Section("桌面浮窗", TestOverlay);
        Section("托盘图元", TestTrayImage);
        Section("配置读写", TestSettings);

        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"通过 {_passed} 条，失败 {Failures.Count} 条");
        foreach (string failure in Failures) Console.WriteLine("  ✗ " + failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void DumpIcons(string directory)
    {
        Directory.CreateDirectory(directory);
        var cases = new (string Name, TrayIconMode Mode, int Size, TrayVisualState State)[]
        {
            ("percent-74", TrayIconMode.Percent, 16, new TrayVisualState(true, 74, false)),
            ("percent-100", TrayIconMode.Percent, 16, new TrayVisualState(true, 100, false)),
            ("percent-8", TrayIconMode.Percent, 16, new TrayVisualState(true, 8, false)),
            ("percent-charging", TrayIconMode.Percent, 16, new TrayVisualState(true, 42, true)),
            ("percent-off", TrayIconMode.Percent, 16, TrayVisualState.Disconnected),
            ("battery-74", TrayIconMode.Battery, 16, new TrayVisualState(true, 74, false)),
            ("battery-15", TrayIconMode.Battery, 16, new TrayVisualState(true, 15, false)),
            ("battery-charging", TrayIconMode.Battery, 16, new TrayVisualState(true, 62, true)),
            ("battery-off", TrayIconMode.Battery, 16, TrayVisualState.Disconnected),
            ("percent-74-32px", TrayIconMode.Percent, 32, new TrayVisualState(true, 74, false)),
            ("percent-off-32px", TrayIconMode.Percent, 32, TrayVisualState.Disconnected),
        };

        foreach ((string name, TrayIconMode mode, int size, TrayVisualState state) in cases)
        {
            TrayImage image = TrayImageFactory.Render(mode, size, state);
            string path = Path.Combine(directory, $"{name}_{image.Width}x{image.Height}.argb");
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(image.Width);
            writer.Write(image.Height);
            foreach (int pixel in image.Pixels) writer.Write(pixel);
            Console.WriteLine($"{path}");
        }
    }

    private static void Section(string name, Action action)
    {
        Console.WriteLine();
        Console.WriteLine($"── {name} ──");
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Fail($"{name} 抛异常：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static void Check(bool condition, string what)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  ✓ {what}");
        }
        else
        {
            Fail(what);
        }
    }

    private static void CheckEqual<T>(T actual, T expected, string what)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  ✓ {what}");
        }
        else
        {
            Fail($"{what}（期望 {expected}，实际 {actual}）");
        }
    }

    private static void Fail(string what)
    {
        Failures.Add(what);
        Console.WriteLine($"  ✗ {what}");
    }

    // ────────────────────────── 帧构造 ──────────────────────────

    private static void TestFrames()
    {
        byte[] battery = RazerCommands.GetBatteryLevel(0xFF);

        CheckEqual(battery.Length, 91, "帧长度 91 字节（含 report ID）");
        CheckEqual(battery[0], (byte)0x00, "[0] report ID = 0x00");
        CheckEqual(battery[1], (byte)0x00, "[1] 请求 status = 0x00");
        CheckEqual(battery[2], (byte)0xFF, "[2] 事务号 = 0xFF（Viper Ultimate）");
        CheckEqual(battery[3], (byte)0x00, "[3] remaining_packets 高字节 = 0");
        CheckEqual(battery[4], (byte)0x00, "[4] remaining_packets 低字节 = 0");
        CheckEqual(battery[5], (byte)0x00, "[5] protocol_type = 0");
        CheckEqual(battery[6], (byte)0x02, "[6] data_size = 0x02（电量命令）");
        CheckEqual(battery[7], (byte)0x07, "[7] command_class = 0x07");
        CheckEqual(battery[8], (byte)0x80, "[8] command_id = 0x80");
        CheckEqual(battery[9], (byte)0x00, "[9] args[0] = 0");
        // CRC = XOR(struct[2..87]) = XOR(buf[3..88])，即 data_size/class/id/args，**不含事务号**
        CheckEqual(battery[89], (byte)0x85, "[89] crc = 0x85（XOR [3..88] 手算值：0x02^0x07^0x80）");
        CheckEqual(battery[90], (byte)0x00, "[90] reserved = 0");

        byte manual = (byte)(0x02 ^ 0x07 ^ 0x80);   // data_size ^ class ^ id
        CheckEqual(battery[89], manual, "crc 与手算的 0x02^0x07^0x80 一致");

        // 反面断言：如果把事务号也算进去会得到 0x7A，必须不是它
        byte ifIncludingTxn = (byte)(0xFF ^ 0x02 ^ 0x07 ^ 0x80);
        Check(battery[89] != ifIncludingTxn,
            $"事务号不参与 CRC（含事务号会得 0x{ifIncludingTxn:X2}，实际 0x{battery[89]:X2}）");

        // 手工重算整个 crc 范围，确认是 [3..88] 而不是 [2..87]
        byte crc2 = 0;
        for (int i = 3; i <= 88; i++) crc2 ^= battery[i];
        CheckEqual(battery[89], crc2, "crc 覆盖范围确为 buf[3..88]");

        byte[] charging = RazerCommands.GetChargingStatus(0xFF);
        CheckEqual(charging[8], (byte)0x84, "充电查询 command_id = 0x84");
        CheckEqual(charging[6], (byte)0x02, "充电查询 data_size = 0x02");

        byte[] getDpi = RazerCommands.GetDpi(0xFF);
        CheckEqual(getDpi[7], (byte)0x04, "读 DPI command_class = 0x04");
        CheckEqual(getDpi[8], (byte)0x85, "读 DPI command_id = 0x85");
        CheckEqual(getDpi[6], (byte)0x07, "读 DPI data_size = 0x07");
        CheckEqual(getDpi[9], (byte)0x00, "读 DPI args[0] = NOSTORE(0x00)");
    }

    // ────────────────────────── 应答解析 ──────────────────────────

    private static void TestParsing()
    {
        byte[] frame = BuildResponse(RazerReport.StatusSuccess, 0xFF, 0x07, 0x80,
            (0, 0x00), (1, 189), (2, 0x00));

        RazerResponse parsed = RazerReport.Parse(frame);
        Check(parsed.IsSuccess, "status=0x00 判为成功");
        CheckEqual(parsed.TransactionId, (byte)0xFF, "解析出事务号");
        CheckEqual(parsed.CommandClass, (byte)0x07, "解析出 command_class");
        CheckEqual(parsed.CommandId, (byte)0x80, "解析出 command_id");
        CheckEqual(parsed.Arg(1), (byte)189, "解析出 args[1] = 189");
        CheckEqual(parsed.Arguments.Length, 80, "args 固定 80 字节");

        byte[] alt = BuildResponse(RazerReport.StatusSuccessAlt, 0xFF, 0x07, 0x80, (1, 100));
        Check(RazerReport.Parse(alt).IsSuccess, "status=0x02 也判为成功（两种成功码都认）");

        CheckEqual(RazerReport.StatusText(RazerReport.StatusNoResponse), "设备无应答(0x04)", "状态文案：无应答");
        CheckEqual(RazerReport.ToHex(new byte[] { 0xDE, 0xAD }), "DE AD", "十六进制输出格式");
    }

    // ────────────────────────── 电量 ──────────────────────────

    private static void TestBatteryScaling()
    {
        // raw 189 → (189*100+127)/255 = 74
        var transport = new FakeTransport(91,
            BuildResponse(0x00, 0xFF, 0x07, 0x80, (1, 189)),
            BuildResponse(0x00, 0xFF, 0x07, 0x84, (1, 0x01)));
        using var mouse = new RazerMouse(transport, Model(), NullLog.Instance);

        Check(mouse.TryReadBattery(BatteryScale.Raw255, out BatterySample sample, out string error),
            $"Raw255 口径读电量成功（{error}）");
        CheckEqual(sample.Percent, 74, "raw 189 → 74%");
        CheckEqual(sample.RawValue, (byte)189, "原始值保留");
        CheckEqual(sample.Charging, (bool?)true, "充电状态解析为 true");
        CheckEqual(mouse.Trace.Count, 2, "两次命令都留了轨迹（电量 + 充电）");

        var direct = new FakeTransport(91, BuildResponse(0x00, 0xFF, 0x07, 0x80, (1, 75)),
            BuildResponse(0x00, 0xFF, 0x07, 0x84, (1, 0x00)));
        using var mouse2 = new RazerMouse(direct, Model(), NullLog.Instance);
        Check(mouse2.TryReadBattery(BatteryScale.Direct, out BatterySample sample2, out _), "Direct 口径读电量成功");
        CheckEqual(sample2.Percent, 75, "Direct 口径 raw 75 → 75%");
        CheckEqual(sample2.Charging, (bool?)false, "放电状态解析为 false");

        var chargingOff = new FakeTransport(91, BuildResponse(0x00, 0xFF, 0x07, 0x80, (1, 255)));
        using var mouse3 = new RazerMouse(chargingOff, Model(), NullLog.Instance) { ReadChargingStatus = false };
        Check(mouse3.TryReadBattery(BatteryScale.Raw255, out BatterySample full, out _), "关掉充电查询也能读电量");
        CheckEqual(full.Percent, 100, "raw 255 → 100%");
        CheckEqual(full.Charging, (bool?)null, "未查充电状态时为未知");
        Check(full.IsFull == false, "充电未知时不算已充满");
        CheckEqual(chargingOff.GetCount, 1, "只发了一条命令（没有多余查询）");
    }

    // ────────────────────────── DPI ──────────────────────────

    private static void TestDpi()
    {
        byte[] set = RazerCommands.SetDpi(0xFF, 1600, 1600);
        CheckEqual(set[7], (byte)0x04, "写 DPI command_class = 0x04");
        CheckEqual(set[8], (byte)0x05, "写 DPI command_id = 0x05");
        CheckEqual(set[6], (byte)0x07, "写 DPI data_size = 0x07");
        CheckEqual(set[9], (byte)0x01, "args[0] = VARSTORE(0x01)");
        CheckEqual(set[10], (byte)0x06, "1600 高字节 = 0x06（大端）");
        CheckEqual(set[11], (byte)0x40, "1600 低字节 = 0x40");
        CheckEqual(set[12], (byte)0x06, "Y 高字节 = 0x06");
        CheckEqual(set[13], (byte)0x40, "Y 低字节 = 0x40");
        CheckEqual(set[14], (byte)0x00, "args[5] = 0");
        CheckEqual(set[15], (byte)0x00, "args[6] = 0");
        CheckEqual(set[89], (byte)0x07, "写 DPI crc = 0x07（0x07^0x04^0x05^0x01^0x06^0x40^0x06^0x40）");

        // 读 DPI：应答 args[1..4] = 06 40 06 40 → 1600x1600
        var readTransport = new FakeTransport(91,
            BuildResponse(0x00, 0xFF, 0x04, 0x85, (0, 0x00), (1, 0x06), (2, 0x40), (3, 0x06), (4, 0x40)));
        using var mouse = new RazerMouse(readTransport, Model(), NullLog.Instance);
        Check(mouse.TryReadDpi(out int x, out int y, out string error), $"读 DPI 成功（{error}）");
        CheckEqual(x, 1600, "解析 X = 1600（大端 args[1..2]）");
        CheckEqual(y, 1600, "解析 Y = 1600（大端 args[3..4]）");

        // 写 DPI 成功后设备回 status=0
        var writeTransport = new FakeTransport(91, BuildResponse(0x00, 0xFF, 0x04, 0x05));
        using var mouse2 = new RazerMouse(writeTransport, Model(), NullLog.Instance);
        Check(mouse2.TryWriteDpi(800, 800, out string writeError), $"写 DPI 成功（{writeError}）");
        CheckEqual(writeTransport.LastRequest![10], (byte)0x03, "800 = 0x0320 → 高字节 0x03");
        CheckEqual(writeTransport.LastRequest![11], (byte)0x20, "800 = 0x0320 → 低字节 0x20");

        // 超上限要夹住（Viper Ultimate 20000）
        var clampTransport = new FakeTransport(91, BuildResponse(0x00, 0xFF, 0x04, 0x05));
        using var mouse3 = new RazerMouse(clampTransport, Model(), NullLog.Instance);
        Check(mouse3.TryWriteDpi(50000, 50000, out _), "超上限写入不报错（夹到上限）");
        ushort written = (ushort)((clampTransport.LastRequest![10] << 8) | clampTransport.LastRequest![11]);
        CheckEqual(written, (ushort)20000, "50000 被夹到 20000");

        // DPI 应答为 0 时视为异常（防止把空帧当成功）
        var zeroTransport = new FakeTransport(91, BuildResponse(0x00, 0xFF, 0x04, 0x85));
        using var mouse4 = new RazerMouse(zeroTransport, Model(), NullLog.Instance);
        Check(!mouse4.TryReadDpi(out _, out _, out string zeroError), "全 0 的 DPI 应答判为失败");
        Check(zeroError.Contains("异常", StringComparison.Ordinal), "失败原因写明应答异常");
    }

    // ────────────────────────── 重试 / 错误分支 ──────────────────────────

    private static void TestRetryAndErrors()
    {
        // 第一次"忙"，第二次成功
        var busyThenOk = new FakeTransport(91,
            BuildResponse(0x01, 0xFF, 0x07, 0x80),
            BuildResponse(0x00, 0xFF, 0x07, 0x80, (1, 200)),
            BuildResponse(0x00, 0xFF, 0x07, 0x84, (1, 0x00)));
        var log = new ListLog();
        using var mouse = new RazerMouse(busyThenOk, Model(), log);
        Check(mouse.TryReadBattery(BatteryScale.Raw255, out BatterySample sample, out _), "忙碌后重试成功");
        // 电量命令发了 2 次（busy + 成功），充电命令再发 1 次 → 共 3 次 SET
        CheckEqual(busyThenOk.SetCount, 3, "电量命令重试过一次（2 次 + 充电 1 次 = 3 次 SET）");
        Check(log.Contains("设备忙"), "日志里记下了'设备忙'的原因");
        CheckEqual(sample.Percent, 78, "raw 200 → 78%");

        // 应答错配（上一条命令的残留），第二次对上
        var stale = new FakeTransport(91,
            BuildResponse(0x00, 0xFF, 0x04, 0x85, (1, 0x06), (2, 0x40)),
            BuildResponse(0x00, 0xFF, 0x07, 0x80, (1, 150)),
            BuildResponse(0x00, 0xFF, 0x07, 0x84, (1, 0x00)));
        using var mouse2 = new RazerMouse(stale, Model(), log);
        Check(mouse2.TryReadBattery(BatteryScale.Raw255, out BatterySample sample2, out _), "残留应答后重试成功");
        CheckEqual(sample2.RawValue, (byte)150, "取到的是正确那条应答（raw 150）");
        Check(log.Contains("应答错配"), "日志记下了错配原因");

        // 设备无应答：必须失败且原因可读
        var noResponse = new FakeTransport(91, BuildResponse(0x04, 0xFF, 0x07, 0x80));
        var log2 = new ListLog();
        using var mouse3 = new RazerMouse(noResponse, Model(), log2);
        Check(!mouse3.TryReadBattery(BatteryScale.Raw255, out _, out string error), "无应答时读取失败");
        Check(error.Contains("无应答", StringComparison.Ordinal), "失败原因写的是'无应答'");
        Check(log2.Contains("无应答"), "日志里有'无应答'记录（不允许静默）");

        // Win32 层面就失败（句柄失效）
        var brokenTransport = new FakeTransport(91) { FailGetWith = 6 };
        var log3 = new ListLog();
        using var mouse4 = new RazerMouse(brokenTransport, Model(), log3);
        Check(!mouse4.TryReadBattery(BatteryScale.Raw255, out _, out string error2), "句柄失效时读取失败");
        Check(error2.Contains("Win32", StringComparison.Ordinal), "失败原因带 Win32 错误码");
        Check(log3.Contains("1167") == false && log3.Contains("GET_FEATURE"), "日志记下 GET_FEATURE 失败");

        // 通道只有 64 字节时构造就应拒绝
        bool threw = false;
        try
        {
            using var bad = new RazerMouse(new FakeTransport(64), Model(), NullLog.Instance);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "feature 长度不足 91 时拒绝构造（避免往错通道写帧）");

        // 一直 busy：按 openrazer 口径兜底接受，但日志要留痕
        var alwaysBusy = new FakeTransport(91,
            BuildResponse(0x01, 0xFF, 0x07, 0x80, (1, 189)),
            BuildResponse(0x01, 0xFF, 0x07, 0x80, (1, 189)),
            BuildResponse(0x01, 0xFF, 0x07, 0x80, (1, 189)),
            BuildResponse(0x01, 0xFF, 0x07, 0x80, (1, 189)),
            BuildResponse(0x01, 0xFF, 0x07, 0x80, (1, 189)));
        var busyLog = new ListLog();
        using var mouseBusy = new RazerMouse(alwaysBusy, Model(), busyLog) { ReadChargingStatus = false };
        Check(mouseBusy.TryReadBattery(BatteryScale.Raw255, out BatterySample busySample, out _),
            "连续 busy 时按 openrazer 口径仍算成功");
        CheckEqual(busySample.RawValue, (byte)189, "busy 兜底时仍能取到数据（raw 189）");
        Check(busyLog.Contains("busy"), "busy 兜底有日志（不静默）");
        CheckEqual(alwaysBusy.SetCount, 5, "重试 5 次（与 openrazer 一致）才兜底");

        // 零权限/只读句柄：仍应尝试写（HID feature IOCTL 是 FILE_ANY_ACCESS，真机实测能成）
        var readOnly = new FakeTransport(91,
            BuildResponse(0x00, 0xFF, 0x07, 0x80, (1, 189)),
            BuildResponse(0x00, 0xFF, 0x07, 0x84, (1, 0x00)),
            BuildResponse(0x00, 0xFF, 0x04, 0x05)) { ReadOnly = true };
        using var mouseRo = new RazerMouse(readOnly, Model(), NullLog.Instance);
        Check(mouseRo.TryReadBattery(BatteryScale.Raw255, out BatterySample roSample, out _), "只读句柄仍能读电量");
        CheckEqual(roSample.Percent, 74, "只读句柄电量值正确");
        Check(!mouseRo.CanWrite, "设备对象报告句柄不可写");
        Check(mouseRo.TryWriteDpi(1600, 1600, out string roError), $"只读句柄仍尝试写 DPI 并成功（{roError}）");
        CheckEqual(readOnly.SetCount, 3, "只读句柄也会发 SET_FEATURE（电量+充电+写 DPI）");

        // SET_FEATURE 真被拒时（Win32 5），必须报出可读原因
        var noWrite = new FakeTransport(91) { FailSetWith = 5 };
        using var mouseNoWrite = new RazerMouse(noWrite, Model(), NullLog.Instance) { ReadChargingStatus = false };
        Check(!mouseNoWrite.TryWriteDpi(1600, 1600, out string writeError), "SET_FEATURE 被拒时写 DPI 失败");
        Check(writeError.Contains("Win32", StringComparison.Ordinal), "失败原因带 Win32 错误码");
        Check(writeError.Contains("5", StringComparison.Ordinal), "失败原因里能看到错误码 5");
    }

    // ────────────────────────── 通道选择 ──────────────────────────

    private static void TestDeviceSelector()
    {
        var good = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_1532&pid_007b&mi_02#8&1&0000",
            VendorId = 0x1532,
            ProductId = 0x007B,
            UsagePage = 0x0001,
            Usage = 0x0002,
            FeatureReportLength = 91,
            InputReportLength = 8,
            OutputReportLength = 8,
        };
        var noise = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_1532&pid_007b&mi_00#8&1&0001",
            VendorId = 0x1532,
            ProductId = 0x007B,
            UsagePage = 0x0001,
            Usage = 0x0006,
            FeatureReportLength = 0,
            InputReportLength = 8,
            OutputReportLength = 0,
        };

        DeviceSelection selection = DeviceSelector.Select(new[] { noise, good }, NullLog.Instance);
        Check(selection.Found, "在噪声接口中挑到协议通道");
        CheckEqual(selection.Interface?.FeatureReportLength, (ushort)91, "选中的是 91 字节通道");
        CheckEqual(selection.Model?.ProductId, (ushort)0x007B, "匹配到 007B 型号");

        var dockFallback = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_1532&pid_007e&col01",
            VendorId = 0x1532,
            ProductId = 0x007E,
            UsagePage = 0xFF00,
            Usage = 0x0002,
            FeatureReportLength = 91,
        };
        DeviceSelection fallback = DeviceSelector.Select(new[] { dockFallback }, NullLog.Instance);
        Check(fallback.Found, "usage 不标准时按 91 字节长度兜底（底座 007E 场景）");
        Check(fallback.Reason.Contains("91 字节", StringComparison.Ordinal), "原因写明选中依据是 91 字节通道");

        var tooShort = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_1532&pid_007b",
            VendorId = 0x1532,
            ProductId = 0x007B,
            UsagePage = 0x0001,
            Usage = 0x0002,
            FeatureReportLength = 64,
        };
        var log = new ListLog();
        DeviceSelection rejected = DeviceSelector.Select(new[] { tooShort }, log);
        Check(!rejected.Found, "feature 只有 64 字节时不认（避免写坏设备）");
        Check(rejected.Reason.Contains("91"), "拒绝原因提到 91 字节要求");
        Check(log.Contains("没有 91 字节"), "日志记下了拒绝原因");

        DeviceSelection empty = DeviceSelector.Select(Array.Empty<HidDeviceInfo>(), log);
        Check(!empty.Found, "空枚举返回未找到");
        Check(empty.Reason.Contains("没有枚举到"), "空枚举原因可读");

        var other = new HidDeviceInfo { VendorId = 0x046D, ProductId = 0xC52B, FeatureReportLength = 91 };
        DeviceSelection notRazer = DeviceSelector.Select(new[] { other }, NullLog.Instance);
        Check(!notRazer.Found, "非雷蛇设备不会被误认");
        Check(notRazer.Reason.Contains("1532") == false && notRazer.Reason.Contains("没有匹配"), "原因说明是没匹配到型号");

        // 同时插着 007B 接收器和 007E 底座时，必须先试 007B：
        // openrazer 的 charge_level 分表里没有 007E，真机实测它对电量命令回 0x05 不支持
        var dock = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_1532&pid_007e&mi_00#9&c5df9cf&0&0000",
            VendorId = 0x1532,
            ProductId = 0x007E,
            UsagePage = 0x0001,
            Usage = 0x0002,
            FeatureReportLength = 91,
            InputReportLength = 9,
        };
        var dongle = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_1532&pid_007b&mi_00#9&340b2271&0&0000",
            VendorId = 0x1532,
            ProductId = 0x007B,
            UsagePage = 0x0001,
            Usage = 0x0002,
            FeatureReportLength = 91,
            InputReportLength = 9,
        };

        var ranked = DeviceSelector.RankCandidates(new[] { dock, dongle }, NullLog.Instance);
        CheckEqual(ranked.Count, 2, "两个候选都被列出（不是只挑一个）");
        CheckEqual(ranked[0].Model.ProductId, (ushort)0x007B, "007B 接收器排在 007E 底座前面");
        CheckEqual(ranked[1].Model.ProductId, (ushort)0x007E, "007E 排在后备位");

        DeviceSelection both = DeviceSelector.Select(new[] { dock, dongle }, NullLog.Instance);
        CheckEqual(both.Model?.ProductId, (ushort)0x007B, "Select 在两台都在时选 007B（真机踩过的坑）");
    }

    // ────────────────────────── 雷柏 VT9 Pro ──────────────────────────

    private static void TestRapoo()
    {
        byte[] frame = RapooVt9Pro.BuildFrame(0xA5);
        CheckEqual(frame.Length, 32, "帧长 32（Windows 口径，含 report ID）");
        CheckEqual(frame[0], (byte)0xBA, "[0] report ID = 0xBA");
        CheckEqual(frame[1], (byte)0xA5, "[1] conn = 0xA5（2.4G 接收器）");
        CheckEqual(frame[2], (byte)0xAA, "[2] cmd = 0xAA（电量）");
        CheckEqual(frame[3], (byte)0x00, "[3] len = 0");
        CheckEqual(frame[4], (byte)0x00, "[4] addr 低字节 = 0");
        CheckEqual(RapooVt9Pro.BuildFrame(0xFF)[1], (byte)0xFF, "有线 conn = 0xFF");

        // 应答解析：含 report ID / 被驱动摘掉 report ID 两种偏移
        var withId = new byte[32];
        withId[0] = 0xBA; withId[1] = 0x01; withId[2] = 0x02; withId[3] = 78;
        Check(RapooVt9Pro.TryParseResponse(withId, out byte st, out byte chg, out int pct), "含 ID 时能解析");
        CheckEqual(st, (byte)0x01, "status = 0x01");
        CheckEqual(chg, (byte)0x02, "charge = 2（充电）");
        CheckEqual(pct, 78, "电量 78%");

        var noId = new byte[32];
        noId[0] = 0x01; noId[1] = 0x01; noId[2] = 55;
        Check(RapooVt9Pro.TryParseResponse(noId, out _, out _, out int pct2), "不含 ID 时也能解析");
        CheckEqual(pct2, 55, "无 ID 偏移下电量 55%");

        var busy = new byte[32];
        busy[0] = 0xBA; busy[1] = 0x02;
        var ack = new byte[32];
        ack[0] = 0xBA; ack[1] = 0x01; ack[2] = 0x02; ack[3] = 78;

        var t = new FakeTransport(0)
        {
            Vid = 0x24AE, Pid = 0x1205, UsagePage = 0xFF00, Usage = 0x000E, InLength = 32, OutLength = 32,
        };
        t.QueueInput(busy, ack);
        using (var rap = new RapooVt9Pro(t, NullLog.Instance))
        {
            bool ok = rap.TryReadBattery(BatteryScale.Direct, out BatterySample sample, out string error);
            Check(ok, $"读到电量（{error}）");
            CheckEqual(sample.Percent, 78, "电量 78%");
            CheckEqual(sample.Charging, (bool?)true, "charge=2 判为充电中");
            CheckEqual(t.WriteCount, 1, "只发了一帧");
            CheckEqual(t.LastOutput![0], (byte)0xBA, "写出的首字节是 report ID");
            CheckEqual(t.LastOutput![1], (byte)0xA5, "先试 2.4G conn=0xA5");
            CheckEqual(t.LastOutput!.Length, 32, "写出的长度等于 OutputReportByteLength");
        }

        // 陈旧应答（charge=0，说明是 EEPROM 块应答）必须被识别并重试
        var staleAck = new byte[32];
        staleAck[0] = 0xBA; staleAck[1] = 0x01; staleAck[2] = 0x00;
        var t2 = new FakeTransport(0)
        {
            Vid = 0x24AE, Pid = 0x1205, UsagePage = 0xFF00, Usage = 0x000E, InLength = 32, OutLength = 32,
        };
        t2.QueueInput(busy, staleAck, busy, ack);
        using (var rap2 = new RapooVt9Pro(t2, NullLog.Instance))
        {
            Check(rap2.TryReadBattery(BatteryScale.Direct, out BatterySample s2, out _), "陈旧应答后重试成功");
            CheckEqual(s2.Percent, 78, "最终拿到 78%");
            CheckEqual(t2.WriteCount, 2, "发了两帧（第一帧拿到的是陈旧应答）");
        }

        // 读不了 input report 时快速失败，不要榨干超时
        var t3 = new FakeTransport(0)
        {
            Vid = 0x24AE, Pid = 0x1205, UsagePage = 0xFF00, Usage = 0x000E, InLength = 32, OutLength = 32,
            FailInput = true,
        };
        using (var rap3 = new RapooVt9Pro(t3, NullLog.Instance))
        {
            Check(!rap3.TryReadBattery(BatteryScale.Direct, out _, out string error3), "读不到 input report 时失败");
            Check(error3.Contains("input report", StringComparison.Ordinal), "失败原因写清是 input report 读不到");
        }

        // 句柄不可写 → 直接失败并带 Win32=5
        var t4 = new FakeTransport(0)
        {
            Vid = 0x24AE, Pid = 0x1205, UsagePage = 0xFF00, Usage = 0x000E, InLength = 32, OutLength = 32,
            ReadOnly = true,
        };
        using (var rap4 = new RapooVt9Pro(t4, NullLog.Instance))
        {
            Check(!rap4.TryReadBattery(BatteryScale.Direct, out _, out string error4, out ReadOutcome o4),
                "句柄不可写时失败");
            Check(error4.Contains("Win32", StringComparison.Ordinal), "失败原因带 Win32 错误码");
            CheckEqual(o4, ReadOutcome.NotWritable, "失败语义 = NotWritable（不再靠文案判断）");
        }

        Check(RapooVt9Pro.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x24AE, ProductId = 0x1205, UsagePage = 0xFF00, Usage = 0x000E,
            InputReportLength = 32, OutputReportLength = 32,
        }), "接口判据：VID:PID + usage + 32/32 三者满足");
        Check(!RapooVt9Pro.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x24AE, ProductId = 0x1205, UsagePage = 0xFF00, Usage = 0x000E,
            InputReportLength = 10, OutputReportLength = 10,
        }), "长度不符时不认");

        // ── v1.14.0：真机 2026-10-27 形态 —— charge=0x70、percent=0 的"假 0%" ──
        // 接收器在线、鼠标休眠时，input 端口的"最后一条报告"会是这种事件/残留帧。
        // 旧代码只挡了 charge==0，于是它被当成"真 0%"显示在托盘上（用户看到雷柏一直 0%）。
        var fakeZero = new byte[32];
        fakeZero[0] = 0xBA; fakeZero[1] = 0x01; fakeZero[2] = 0x70; fakeZero[3] = 0x00;
        Check(RapooVt9Pro.TryParseResponse(fakeZero, out byte fzStatus, out byte fzCharge, out int fzPct),
            "假 0% 帧结构上能解析");
        CheckEqual(fzPct, 0, "结构解析如实给出 0（不夹取、不改写原始字节）");
        Check(!RapooVt9Pro.IsPlausibleBattery(fzStatus, fzCharge, fzPct),
            "charge=0x70 判为不可信（不能当电量）");
        Check(RapooVt9Pro.DescribeImplausible(fzStatus, fzCharge, fzPct).Contains("0x70", StringComparison.Ordinal),
            "不可信原因里带着实际收到的 charge 字节");
        Check(RapooVt9Pro.IsPlausibleBattery(0x01, 0x01, 0), "合法的 0%（放电、百分比 0）仍然是有效读数");
        Check(!RapooVt9Pro.IsPlausibleBattery(0x01, 0x02, 101), "百分比 101 判为不可信（不做夹取）");
        Check(!RapooVt9Pro.IsPlausibleBattery(0x01, 0x00, 80), "charge=0（EEPROM 块应答特征）判为不可信");

        var pctOverflow = new byte[32];
        pctOverflow[0] = 0xBA; pctOverflow[1] = 0x01; pctOverflow[2] = 0x01; pctOverflow[3] = 0xFF;
        RapooVt9Pro.TryParseResponse(pctOverflow, out _, out _, out int overflowPct);
        CheckEqual(overflowPct, 255, "0xFF 不再被夹成 100（以前那等于替设备编数据）");

        // 先拿到假 0%，重试后拿到真读数 → 必须用真读数
        var t5 = new FakeTransport(0)
        {
            Vid = 0x24AE, Pid = 0x1205, UsagePage = 0xFF00, Usage = 0x000E, InLength = 32, OutLength = 32,
        };
        t5.QueueInput(busy, fakeZero, busy, ack);
        using (var rap5 = new RapooVt9Pro(t5, NullLog.Instance))
        {
            Check(rap5.TryReadBattery(BatteryScale.Direct, out BatterySample s5, out string e5),
                $"假 0% 之后重试拿到真读数（{e5}）");
            CheckEqual(s5.Percent, 78, "最终是 78%，不是 0%");
        }

        // 一直只有假 0% → 必须报失败（宁可没有数字，也不显示 0%）
        var t6 = new FakeTransport(0)
        {
            Vid = 0x24AE, Pid = 0x1205, UsagePage = 0xFF00, Usage = 0x000E, InLength = 32, OutLength = 32,
        };
        for (int i = 0; i < 4; i++) t6.QueueInput(busy, fakeZero);
        using (var rap6 = new RapooVt9Pro(t6, NullLog.Instance))
        {
            Check(!rap6.TryReadBattery(BatteryScale.Direct, out BatterySample s6, out string e6),
                "只有假 0% 时读电量必须失败（不能把 0% 交给界面）");
            CheckEqual(s6.Percent, 0, "失败时不产生任何 sample");
            Check(e6.Contains("不可信", StringComparison.Ordinal), $"失败原因写明应答不可信：{e6}");
            Check(e6.Contains("休眠", StringComparison.Ordinal), $"失败原因给出鼠标休眠的处置提示：{e6}");
            // v1.15：设备只会复读同一帧 ⇒ 两次（一次探、一次确认）就收工，不再白试 4 次
            CheckEqual(t6.WriteCount, 2, "同一帧复读两次即判定休眠，本轮收工（省掉 2 次白等）");
        }

        // 但如果每帧都不一样（真的在回不同数据），仍要按 4 次上限重试
        var t7 = new FakeTransport(0)
        {
            Vid = 0x24AE, Pid = 0x1205, UsagePage = 0xFF00, Usage = 0x000E, InLength = 32, OutLength = 32,
        };
        for (int i = 0; i < 4; i++)
        {
            var varying = (byte[])fakeZero.Clone();
            varying[2] = (byte)(0x70 + i);      // charge 每次不同 → 不触发"复读"早退
            t7.QueueInput(busy, varying);
        }
        using (var rap7 = new RapooVt9Pro(t7, NullLog.Instance))
        {
            Check(!rap7.TryReadBattery(BatteryScale.Direct, out _, out _, out ReadOutcome o7),
                "每次都不同的不可信应答仍判失败");
            CheckEqual(o7, ReadOutcome.Implausible, "失败语义 = Implausible（设备多半休眠）");
            CheckEqual(t7.WriteCount, 4, "帧不相同就按 4 次重试上限问到底");
        }

        TestRapooDpi();
    }

    // ────────────────────────── 雷柏 DPI（EEPROM 块） ──────────────────────────

    private static void TestRapooDpi()
    {
        CheckEqual(RapooVt9Pro.NormalizeDpi(1600), 1600, "1600 原样通过");
        CheckEqual(RapooVt9Pro.NormalizeDpi(1623), 1600, "1623 规整到 50 步进");
        CheckEqual(RapooVt9Pro.NormalizeDpi(10), 50, "低于下限夹到 50");
        CheckEqual(RapooVt9Pro.NormalizeDpi(99000), 26000, "超过上限夹到 26000");

        // 读块帧：[2]=0xA4 [3]=长度 [4..7]=地址 LE32
        byte[] readFrame = RapooVt9Pro.BuildFrame(0xA5, RapooVt9Pro.CmdReadBlock, 16, RapooVt9Pro.AddrDpiX);
        CheckEqual(readFrame[0], (byte)0xBA, "读块帧首字节是 report ID");
        CheckEqual(readFrame[2], (byte)0xA4, "读块 cmd = 0xA4");
        CheckEqual(readFrame[3], (byte)16, "读块长度 = 16");
        CheckEqual(readFrame[4], (byte)0x88, "地址 0x888 低字节 = 0x88");
        CheckEqual(readFrame[5], (byte)0x08, "地址 0x888 高字节 = 0x08");

        byte[] writeFrame = RapooVt9Pro.BuildFrame(0xA5, RapooVt9Pro.CmdWriteBlock, 2, RapooVt9Pro.AddrDpiX,
            new byte[] { 0x40, 0x06 });
        CheckEqual(writeFrame[2], (byte)0xA5, "写块 cmd = 0xA5");
        CheckEqual(writeFrame[3], (byte)2, "写块长度 = 数据长度");
        CheckEqual(writeFrame[8], (byte)0x40, "数据从第 8 字节开始");

        // 块应答解析：数据从第 5 字节开始（含 report ID 时）
        var response = new byte[32];
        response[0] = 0xBA;
        response[1] = 0x01;
        for (int i = 0; i < 16; i++) response[5 + i] = (byte)(i + 1);
        Check(RapooVt9Pro.TryParseBlockData(response, 16, out byte[] block), "块应答能解析");
        CheckEqual(block.Length, 16, "块长度 16");
        CheckEqual(block[0], (byte)1, "首字节 = 1");
        CheckEqual(block[15], (byte)16, "末字节 = 16");

        var badStatus = new byte[32];
        badStatus[0] = 0xBA;
        badStatus[1] = 0x02;
        Check(!RapooVt9Pro.TryParseBlockData(badStatus, 16, out _), "status 非 0x01 时不算块数据");

        // 安全闸：结构校验
        var goodTable = new byte[16];
        int[] stages = { 400, 800, 1600, 3200, 6400, 12000, 26000 };
        for (int i = 0; i < 7; i++)
        {
            goodTable[i * 2] = (byte)(stages[i] & 0xFF);
            goodTable[i * 2 + 1] = (byte)(stages[i] >> 8);
        }
        goodTable[14] = 7;
        Check(RapooVt9Pro.LooksLikeDpiTable(goodTable, out _), "合法的 7 档 DPI 表通过校验");

        var zeroStages = (byte[])goodTable.Clone();
        zeroStages[14] = 0;
        Check(RapooVt9Pro.LooksLikeDpiTable(zeroStages, activeStage: 3, out _),
            "[14]=0 仍通过（真机就是这个值，旧判据把它误判成地址猜错 → DPI 写不进去）");

        // 2026-10-26 真机形态的回归：只有当前档有值、[14]=0。这正是"调整 DPI 失败"现场的翻版
        var realDevice = new byte[16];
        realDevice[0] = 0x80; realDevice[1] = 0x0C;   // 当前档 3200
        Check(RapooVt9Pro.LooksLikeDpiTable(realDevice, activeStage: 0, out string realReason),
            $"真机形态（[14]=0、当前档 3200）通过校验（{realReason}）");

        // [14] 完全不可依赖：真机上先后读到 0 和 2，档位列表两次都完整 → 任何取值都不能影响判据
        var secondRealShape = new byte[16];
        new byte[] { 0x40, 0x06, 0x80, 0x0C, 0x20, 0x03, 0xB0, 0x04,
                     0x00, 0x19, 0x90, 0x65, 0x90, 0x01, 0x02, 0x00 }.CopyTo(secondRealShape, 0);
        Check(RapooVt9Pro.LooksLikeDpiTable(secondRealShape, activeStage: 2, out string secondReason),
            $"第二次真机形态（[14]=2、当前第 3 档 800）通过校验（{secondReason}）");

        foreach (byte countByte in new byte[] { 0x00, 0x02, 0x07, 0x0B, 0xFF })
        {
            var probeTable = (byte[])goodTable.Clone();
            probeTable[14] = countByte;
            Check(RapooVt9Pro.LooksLikeDpiTable(probeTable, activeStage: 3, out _),
                $"[14]=0x{countByte:X2} 不影响判据（这个字节不可依赖）");
        }

        var emptyActive = (byte[])goodTable.Clone();
        emptyActive[4] = 0; emptyActive[5] = 0;       // 当前档（下标 2）清零
        Check(!RapooVt9Pro.LooksLikeDpiTable(emptyActive, activeStage: 2, out string reasonActive),
            "当前档位值不像 DPI 时拒绝");
        Check(reasonActive.Contains("当前档位"), "拒绝原因指向当前档位");

        var wildValue = (byte[])goodTable.Clone();
        wildValue[0] = 0xFF; wildValue[1] = 0xFF;   // 65535 DPI
        Check(!RapooVt9Pro.LooksLikeDpiTable(wildValue, activeStage: 0, out string reason2), "某档超出传感器范围时拒绝");
        Check(reason2.Contains("超出"), "拒绝原因说明取值越界");

        // 完整流程：用协议模拟器跑一遍 读 → 写 → 校验
        var mouse = new FakeRapooMouse();
        mouse.SetDpiTable(new[] { 400, 800, 1600, 3200, 6400, 12000, 26000 }, activeStage: 1);
        using (var rap = new RapooVt9Pro(mouse, NullLog.Instance))
        {
            Check(rap.TryReadDpi(out int x, out int y, out string readError), $"读到 DPI（{readError}）");
            CheckEqual(x, 800, "当前档位（第 2 档）= 800");
            CheckEqual(y, 800, "Y 与 X 一致");

            Check(rap.TryWriteDpi(1600, 1600, out string writeError), $"写 DPI 成功（{writeError}）");
            CheckEqual(mouse.GetDpi(1), 1600, "EEPROM 里第 2 档已改为 1600");
            Check(mouse.XTable[0] == 0x90 && mouse.YTable[0] == 0x90, "第 1 档保持 400 未被动过");

            Check(rap.TryReadDpi(out int x2, out int y2, out _), "写完再读");
            CheckEqual(x2, 1600, "读回 1600");
            CheckEqual(y2, 1600, "Y 读回 1600");
        }

        // 写入被设备忽略 → 必须报"校验不一致"并尝试恢复原值
        var mute = new FakeRapooMouse { IgnoreWrites = true };
        mute.SetDpiTable(new[] { 400, 800, 1600, 3200, 6400, 12000, 26000 }, activeStage: 1);
        using (var rapMute = new RapooVt9Pro(mute, NullLog.Instance))
        {
            Check(!rapMute.TryWriteDpi(3200, 3200, out string mismatchError), "写入被忽略时报失败");
            Check(mismatchError.Contains("不一致", StringComparison.Ordinal), "失败原因写明读回不一致");
            CheckEqual(mute.GetDpi(1), 800, "设备侧仍是原值 800（没被写坏）");
        }

        // 读回来的内容不像 DPI 表 → 拒绝写入，绝不碰 EEPROM
        var garbage = new FakeRapooMouse();
        for (int i = 0; i < 16; i++) garbage.XTable[i] = 0xEE;
        garbage.XTable[14] = 0xEE;
        for (int i = 0; i < 16; i++) garbage.YTable[i] = 0xEE;
        garbage.YTable[14] = 0xEE;
        using (var rapGarbage = new RapooVt9Pro(garbage, NullLog.Instance))
        {
            Check(!rapGarbage.TryWriteDpi(1600, 1600, out string gateError), "内容不像 DPI 表时拒绝写入");
            Check(gateError.Contains("拒绝写入", StringComparison.Ordinal), "拒绝原因写明是安全闸拦下");
            CheckEqual(garbage.BlockWriteCount, 0, "一帧 EEPROM 写请求都没发出去");
        }

        // ── 真机形态回归（2026-10-26）──
        // 用户机上 [14]=0、当前档 3200，旧判据直接拒写。这里用同样的形态跑通整条写入链路。
        var realShape = new FakeRapooMouse { StageCountByte = 0 };
        realShape.SetDpiTable(new[] { 3200 }, activeStage: 0);
        using (var rapReal = new RapooVt9Pro(realShape, NullLog.Instance))
        {
            Check(rapReal.TryReadDpi(out int realX, out int realY, out string realReadError),
                $"真机形态读到 DPI（{realReadError}）");
            CheckEqual(realX, 3200, "真机形态当前档 = 3200");
            CheckEqual(realY, 3200, "Y 也是 3200");

            Check(rapReal.TryWriteDpi(1600, 1600, out string realWriteError),
                $"真机形态（[14]=0）写 DPI 不再被安全闸误拦（{realWriteError}）");
            CheckEqual(realShape.GetDpi(0), 1600, "EEPROM 当前档已改为 1600");
            Check(rapReal.LastXTableHex.Contains("40 06", StringComparison.Ordinal),
                $"诊断拿得到 X 表原文（{rapReal.LastXTableHex}）");
        }

        // 写通路探测：原样写回 + 读回比对（配置必须没被改动）
        var probe = new FakeRapooMouse { StageCountByte = 0 };
        probe.SetDpiTable(new[] { 400, 800, 1600, 3200, 6400, 12000, 26000 }, activeStage: 3);
        using (var rapProbe = new RapooVt9Pro(probe, NullLog.Instance))
        {
            Check(rapProbe.TryVerifyWritePath(out string probeError), $"写通路探测通过（{probeError}）");
            CheckEqual(probe.GetDpi(3), 3200, "探测后当前档仍是 3200（没被改）");
            Check(probe.BlockWriteCount >= 2, $"真的发了块写帧（{probe.BlockWriteCount} 帧）");
        }

        // 内容不像 DPI 表时，探测也不能写
        var probeGarbage = new FakeRapooMouse();
        for (int i = 0; i < 16; i++) probeGarbage.XTable[i] = 0xFF;
        for (int i = 0; i < 16; i++) probeGarbage.YTable[i] = 0xFF;
        using (var rapProbeBad = new RapooVt9Pro(probeGarbage, NullLog.Instance))
        {
            Check(!rapProbeBad.TryVerifyWritePath(out string probeBadError), "不像 DPI 表时探测拒绝写入");
            Check(probeBadError.Contains("不像 DPI 表", StringComparison.Ordinal), "拒绝原因说清是结构校验");
            CheckEqual(probeGarbage.BlockWriteCount, 0, "一帧都没写");
        }

        // ── v1.14.0：鼠标休眠时接收器 ACK 一个"全 0 块"（真机 2026-10-27 形态）──
        // 旧代码把全 0 当合法数据 → 日志出现"雷柏 DPI：档位 1/0 = 0x0"，看着像 DPI 真是 0。
        var sleeping = new FakeRapooMouse();     // 三块默认全 0
        using (var rapSleep = new RapooVt9Pro(sleeping, NullLog.Instance))
        {
            Check(!rapSleep.TryReadDpi(out _, out _, out string sleepError), "全 0 的块不算读到了 DPI");
            Check(sleepError.Contains("全 0", StringComparison.Ordinal), $"失败原因写明块内容全 0：{sleepError}");
            Check(sleepError.Contains("休眠", StringComparison.Ordinal), $"失败原因提示鼠标休眠：{sleepError}");
            Check(rapSleep.LastXTableHex.Replace(" ", string.Empty).All(c => c == '0'),
                $"诊断仍能拿到原始块（16 个 00）：{rapSleep.LastXTableHex}");
            Check(sleeping.ReadBlockCount >= 3, $"全 0 块要重试（读了 {sleeping.ReadBlockCount} 次）");
            CheckEqual(sleeping.BlockWriteCount, 0, "读不到表时绝不写 EEPROM");
        }

        // 前两次块读为空、之后有值（鼠标刚好醒过来）→ 必须重试成功，不能报失败
        var wakeUp = new FakeRapooMouse { EmptyBlockReadsFirst = 2 };
        wakeUp.SetDpiTable(new[] { 400, 800, 1600, 3200, 6400, 12000, 26000 }, activeStage: 2);
        using (var rapWake = new RapooVt9Pro(wakeUp, NullLog.Instance))
        {
            Check(rapWake.TryReadDpi(out int wakeX, out int wakeY, out string wakeError),
                $"先空后来有值时应重试成功（{wakeError}）");
            CheckEqual(wakeX, 1600, "第 3 档 = 1600");
            CheckEqual(wakeY, 1600, "Y 同步 1600");
        }
    }

    // ────────────────────────── 任天堂 Switch Pro ──────────────────────────

    private static void TestSwitchPro()
    {
        // 子命令帧：0x01 + 序号 + 8 字节中性震动 + 子命令 + 数据（hid-nintendo 的 layout）
        byte[] frame = NintendoSwitchPro.BuildSetFullReportModeFrame(packetNumber: 5);
        CheckEqual(frame.Length, NintendoSwitchPro.ReportLength, "默认帧长 = 49（蓝牙 output report 长度）");
        CheckEqual(frame[0], (byte)0x01, "[0] = 0x01（rumble + subcommand）");
        CheckEqual(frame[1], (byte)0x05, "[1] = 报文序号");
        CheckEqual(frame[2], (byte)0x00, "震动段首字节 = 0x00");
        CheckEqual(frame[3], (byte)0x01, "震动段第二字节 = 0x01（中性）");
        CheckEqual(frame[10], NintendoSwitchPro.SubcmdSetInputReportMode, "[10] = 子命令 0x03（设报告模式）");
        CheckEqual(frame[11], NintendoSwitchPro.InputReportModeFull, "[11] = 0x30（标准全量模式）");

        byte[] wide = NintendoSwitchPro.BuildSetFullReportModeFrame(packetNumber: 0, reportLength: 64);
        CheckEqual(wide.Length, 64, "给了通道长度就按通道长度出帧");

        // 电量位域：bit5-7 档位、bit4 充电、bit0 USB 供电
        var full = new byte[49];
        full[0] = 0x30;
        full[2] = 0x80;                       // 档位 4（满）、不充电
        Check(NintendoSwitchPro.TryParseBattery(full, out int lvFull, out bool chgFull, out bool hostFull),
            "0x30 满电报告能解析");
        CheckEqual(lvFull, 4, "档位 = 4（满）");
        CheckEqual(NintendoSwitchPro.LevelToPercent(lvFull), 100, "档位 4 → 100%");
        CheckEqual(chgFull, false, "bit4=0 不算充电");
        CheckEqual(hostFull, false, "bit0=0 不是 USB 供电");

        var charging = new byte[49];
        charging[0] = 0x30;
        charging[2] = (byte)((2 << 5) | 0x10);   // 档位 2 + 充电位
        NintendoSwitchPro.TryParseBattery(charging, out int lv2, out bool chg2, out _);
        CheckEqual(lv2, 2, "档位 = 2（中）");
        CheckEqual(NintendoSwitchPro.LevelToPercent(lv2), 50, "档位 2 → 50%");
        CheckEqual(chg2, true, "bit4=1 判为充电中");

        var hostPowered = new byte[49];
        hostPowered[0] = 0x30;
        hostPowered[2] = (byte)((3 << 5) | 0x01);   // 档位 3 + USB 供电
        NintendoSwitchPro.TryParseBattery(hostPowered, out int lv3, out bool chg3, out bool host3);
        CheckEqual(lv3, 3, "档位 = 3（高）");
        CheckEqual(host3, true, "bit0=1 判为 USB 供电");
        CheckEqual(chg3, false, "只有 bit0 时不冒充充电中");

        // ⚠️ 组装层（BuildSample）不得用 USB 供电位兜底成"充电中"：
        // 2026-11-02 真机帧 0x81（bit0=1、bit4=0、档位满）实际没在充电，旧逻辑显示成"已充满·充电中"被用户指出。
        CheckEqual(NintendoSwitchPro.ResolveCharging(chargingBit: false, hostPowered: true, level: 4),
            (bool?)false, "bit0=1 + 满电但 bit4=0 → 不算充电（真机 0x81 实测）");
        CheckEqual(NintendoSwitchPro.ResolveCharging(true, true, 4), (bool?)true, "bit4=1 → 充电中");
        CheckEqual(NintendoSwitchPro.ResolveCharging(false, false, 2), (bool?)false, "bit4=0 → 未充电");

        // 档位制设备必须把"档位"显示出来，否则 100% 会被当成精确电量（2026-11-02 用户质疑）
        var levelSample = new BatterySample(100, 0x81, false, DateTimeOffset.Now, BatteryScale.Direct, "x")
        {
            LevelText = "满 5/5 档",
        };
        CheckEqual(levelSample.Describe(), "100% · 满 5/5 档 · 放电中", "档位制显示带档位（不冒充精确百分比）");
        var plainSample = new BatterySample(87, 0xDD, null, DateTimeOffset.Now, BatteryScale.Raw255, "x");
        CheckEqual(plainSample.Describe(), "87%", "精确百分比设备照旧只显示百分比");

        var invalid = new byte[49];
        invalid[0] = 0x30;
        invalid[2] = (byte)(5 << 5);          // 档位 5：非法
        Check(!NintendoSwitchPro.TryParseBattery(invalid, out _, out _, out _),
            "非法档位（>4）判失败，不硬解释成满电");

        var simple = new byte[49];
        simple[0] = NintendoSwitchPro.ReportInputSimpleHid;
        Check(!NintendoSwitchPro.TryParseBattery(simple, out _, out _, out _),
            "0x3F 简化报告没有电量字段");

        var subcmdReply = new byte[49];
        subcmdReply[0] = NintendoSwitchPro.ReportInputSubcmdReply;
        subcmdReply[2] = 0x82;                // 这里是 ack，不是电量
        Check(!NintendoSwitchPro.TryParseBattery(subcmdReply, out _, out _, out _),
            "0x21 子命令应答不按 0x30 解析（[2] 是 ack）");

        // ① 设备已经在推 0x30 → 不该发子命令
        var t = SwitchProTransport();
        t.QueueInput(PadReport(0x3F), PadReport(0x30, 0x80));
        using (var pad = new NintendoSwitchPro(t, NullLog.Instance))
        {
            Check(pad.TryReadBattery(BatteryScale.Direct, out BatterySample sample, out string error),
                $"已经在推 0x30 时直接读到电量（{error}）");
            CheckEqual(sample.Percent, 100, "读到 100%");
            CheckEqual(t.WriteCount, 0, "已经在推 0x30，不需要发子命令");
            CheckEqual(pad.ModeRequested, false, "ModeRequested 仍是 false");
            CheckEqual(pad.LastReportId, (byte)0x30, "诊断里记下最近报告 ID");
        }

        // ② 只推 0x3F → 必须发一次子命令，然后仍读不到就报失败（语义 = 无应答）
        var t2 = SwitchProTransport();
        t2.QueueInput(PadReport(0x3F), PadReport(0x3F));
        using (var pad2 = new NintendoSwitchPro(t2, NullLog.Instance))
        {
            Check(!pad2.TryReadBattery(BatteryScale.Direct, out _, out string error2, out ReadOutcome outcome2),
                "只有 0x3F 时读电量失败");
            CheckEqual(outcome2, ReadOutcome.NoResponse, "语义 = NoResponse（手柄休眠/未响应）");
            CheckEqual(t2.WriteCount, 1, "只发了一次子命令（不刷屏）");
            CheckEqual(t2.LastOutput![0], (byte)0x01, "写出的是 0x01 帧");
            CheckEqual(t2.LastOutput![10], NintendoSwitchPro.SubcmdSetInputReportMode, "子命令 = 0x03");
            CheckEqual(pad2.ModeRequested, true, "诊断里能看到已请求切模式");
            Check(error2.Contains("0x30", StringComparison.Ordinal), $"失败原因提到 0x30 模式：{error2}");
        }

        // ③ 句柄不可写 → NotWritable（此时连子命令都发不出去）
        var t3 = new FakeTransport(0)
        {
            Vid = 0x057E, Pid = 0x2009, UsagePage = 0x0001, Usage = 0x0005,
            InLength = 362, OutLength = NintendoSwitchPro.ReportLength, ReadOnly = true,
            PathValue = @"\\?\hid#bthenum#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        };
        t3.QueueInput(PadReport(0x3F));
        using (var pad3 = new NintendoSwitchPro(t3, NullLog.Instance))
        {
            Check(!pad3.TryReadBattery(BatteryScale.Direct, out _, out string error3, out ReadOutcome outcome3),
                "只读句柄读不到电量");
            CheckEqual(outcome3, ReadOutcome.NotWritable, "语义 = NotWritable");
            Check(error3.Contains("Win32", StringComparison.Ordinal), $"失败原因带 Win32 码：{error3}");
        }

        // ④ 接口判据：必须锁死 0001/0005 + 长度够
        Check(NintendoSwitchPro.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x057E, ProductId = 0x2009, UsagePage = 0x0001, Usage = 0x0005,
            InputReportLength = 362, OutputReportLength = 49,
        }), "蓝牙下 in=362 也认（按 usage 判，不按长度判）");
        Check(!NintendoSwitchPro.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x057E, ProductId = 0x2009, UsagePage = 0x0001, Usage = 0x0006,
            InputReportLength = 49, OutputReportLength = 49,
        }), "键盘集合（0001/0006）不认");
        Check(!NintendoSwitchPro.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x057E, ProductId = 0x2009, UsagePage = 0x0001, Usage = 0x0005,
            InputReportLength = 8, OutputReportLength = 49,
        }), "输入报告太短不认");
        Check(NintendoSwitchPro.IsBluetoothPath(@"\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002057e_pid&2009"),
            "蓝牙路径识别（bth GUID）");
        Check(NintendoSwitchPro.IsBluetoothPath(@"\?\hid#bthenum#7&1234&0&0000#{...}"), "蓝牙路径识别（bthenum）");
        Check(!NintendoSwitchPro.IsBluetoothPath(@"\?\hid#vid_057e&pid_2009#7&abc&0&0000"), "非蓝牙路径不误判");
    }

    /// <summary>构造一条 Switch Pro 输入报告（默认 49 字节）。</summary>
    private static byte[] PadReport(byte reportId, byte batteryByte = 0x00)
    {
        var report = new byte[NintendoSwitchPro.ReportLength];
        report[0] = reportId;
        report[2] = batteryByte;
        return report;
    }

    private static FakeTransport SwitchProTransport() => new FakeTransport(0)
    {
        Vid = 0x057E, Pid = 0x2009, UsagePage = 0x0001, Usage = 0x0005,
        InLength = 362, OutLength = NintendoSwitchPro.ReportLength,
        PathValue = @"\?\hid#bthenum#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
    };

    // ────────────────────────── Xbox 手柄 ──────────────────────────

    private static void TestXboxBattery()
    {
        CheckEqual(XboxBattery.ToPercent(XboxBatteryLevel.Full), 100, "满 → 100%");
        CheckEqual(XboxBattery.ToPercent(XboxBatteryLevel.Medium), 60, "中 → 60%");
        CheckEqual(XboxBattery.ToPercent(XboxBatteryLevel.Low), 25, "低 → 25%");
        CheckEqual(XboxBattery.ToPercent(XboxBatteryLevel.Empty), 5, "空 → 5%（不是 0，避免被误当成没数据）");

        Check(XboxBattery.HasBattery(XboxBatteryType.Alkaline), "碱性电池有电量");
        Check(XboxBattery.HasBattery(XboxBatteryType.NiMh), "镍氢充电包有电量");
        Check(!XboxBattery.HasBattery(XboxBatteryType.Wired), "有线供电没有电池（不该进托盘）");
        Check(!XboxBattery.HasBattery(XboxBatteryType.Disconnected), "未连接没有电量");

        Check(XboxBattery.DescribeType(XboxBatteryType.Alkaline).Contains("碱性"), "类型文案：碱性");
        Check(XboxBattery.DescribeType(XboxBatteryType.NiMh).Contains("镍氢"), "类型文案：镍氢");
        CheckEqual(XboxBattery.DescribeLevel(XboxBatteryLevel.Low), "低", "档位文案：低");

        // 4 档 → 百分比必须单调递增（改桶值时别破坏顺序）
        int previous = -1;
        foreach (XboxBatteryLevel level in Enum.GetValues<XboxBatteryLevel>())
        {
            int percent = XboxBattery.ToPercent(level);
            Check(percent > previous, $"{level} 的百分比高于前一档（{percent} > {previous}）");
            previous = percent;
        }
    }

    // ────────────────────────── 漫步者 G3M Pro ──────────────────────────

    private static void TestEdifier()
    {
        var heartbeat = new byte[64];
        heartbeat[0] = 0x04; heartbeat[1] = 0xAA; heartbeat[4] = 0xFF;

        var status = new byte[64];
        status[0] = 0x04; status[1] = 0x20; status[2] = 0x00; status[3] = 0x1A;
        status[8] = 67; status[9] = 0x00;

        Check(EdifierHecate.TryParseStatusFrame(status, out int pct, out bool charging), "解析状态帧");
        CheckEqual(pct, 67, "电量 67%");
        CheckEqual(charging, false, "charge=0 判为放电");
        Check(!EdifierHecate.TryParseStatusFrame(heartbeat, out _, out _), "心跳帧不当状态帧");

        // 驱动摘掉 report ID 的情况
        Check(EdifierHecate.TryParseStatusFrame(status.AsSpan(1), out int pct2, out _), "不含 report ID 也能解析");
        CheckEqual(pct2, 67, "偏移兼容后仍是 67%");

        var chargingFrame = (byte[])status.Clone();
        chargingFrame[9] = 0x01;
        EdifierHecate.TryParseStatusFrame(chargingFrame, out _, out bool chg2);
        CheckEqual(chg2, true, "charge=1 判为充电中");

        var fullFrame = (byte[])status.Clone();
        fullFrame[8] = 100;
        fullFrame[9] = 0x00;
        EdifierHecate.TryParseStatusFrame(fullFrame, out _, out bool chg3);
        CheckEqual(chg3, true, "100% 兜底判满（满电也可能报 0x01）");

        // 只读句柄 → 主动查询发不出去，必须自动退回被动状态帧
        var t = new FakeTransport(0)
        {
            Vid = 0x320F, Pid = 0x706E, UsagePage = 0xFF1C, Usage = 0x0092,
            InLength = 64, OutLength = 64, ReadOnly = true,
        };
        t.QueueInput(heartbeat, status);
        using (var ed = new EdifierHecate(t, NullLog.Instance))
        {
            Check(ed.TryReadBattery(BatteryScale.Direct, out BatterySample sample, out string error, out ReadOutcome edOutcome),
                $"跳过心跳帧后读到电量（{error}）");
            CheckEqual(edOutcome, ReadOutcome.Ok, "只读句柄靠被动状态帧也能给出 Ok");
            CheckEqual(sample.Percent, 67, "电量 67%");
            Check(ed.SupportsDpi, "漫步者提供 DPI（字段在页 0x05 偏移 0x34）");
            Check(!ed.TryReadDpi(out _, out _, out string roDpiError), $"只读句柄读不到 DPI（{roDpiError}）");
            Check(!ed.TryWriteDpi(800, 800, out string roWriteError), $"只读句柄写 DPI 必失败（{roWriteError}）");
        }

        // ── 主动协议（2026-10-26 抓包确认）──
        byte[] batteryRequest = EdifierHecate.BuildRequest(EdifierHecate.CmdBattery, 0x06, 0x00);
        CheckEqual(batteryRequest.Length, 64, "请求固定 64 字节");
        CheckEqual(batteryRequest[0], (byte)0x04, "report ID = 0x04");
        CheckEqual(batteryRequest[1], (byte)0x20, "[1] = 校验和（0x1A+0x06 = 0x20，与抓包一致）");
        CheckEqual(batteryRequest[3], (byte)0x1A, "电量命令 0x1A");
        CheckEqual(batteryRequest[4], (byte)0x06, "电量子命令 0x06");

        // 校验和必须能复现抓包里每一条帧的 [1]（这是 v1.4.0 一条应答都收不到的真因：
        // 当时 [1] 是随便编的序号，设备直接静默丢帧）
        (byte[] Frame, string Why)[] capturedFrames =
        {
            (new byte[] { 0x04, 0x1B, 0x00, 0x03, 0x18 }, "读设备信息页 [1]=0x1B"),
            (new byte[] { 0x04, 0x25, 0x00, 0x03, 0x0A, 0x18 }, "读设备信息页偏移 0x18 [1]=0x25"),
            (new byte[] { 0x04, 0x37, 0x00, 0x07, 0x18, 0x18 }, "读档位页偏移 0x18 [1]=0x37"),
            (new byte[] { 0x04, 0x59, 0x00, 0x05, 0x0C, 0x48 }, "读设置页末块 [1]=0x59"),
            (new byte[] { 0x04, 0x20, 0x00, 0x1A, 0x06 }, "查电量 [1]=0x20"),
            (new byte[] { 0x04, 0xAA, 0x00, 0xAA }, "心跳/握手 [1]=0xAA"),
            (new byte[] { 0x04, 0x2D, 0x00, 0x28, 0x01, 0x00, 0x00, 0x00, 0x04 }, "设档位 4 [1]=0x2D"),
            (new byte[] { 0x04, 0x29, 0x00, 0x28, 0x01, 0x00, 0x00, 0x00, 0x00 }, "设档位 0 [1]=0x29"),
            (new byte[]
            {
                0x04, 0x79, 0x02, 0x06, 0x18, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x04, 0x02, 0x00, 0x01, 0xFF, 0x00,
                0x00, 0x00, 0x00, 0x03, 0x04, 0x00, 0x01, 0x00,
                0x07, 0x00, 0x40, 0x06, 0xFF, 0x00, 0x00, 0x01,
            }, "写设置页整块 [1]=0x79（含 24 字节数据）"),
        };

        foreach ((byte[] frame, string why) in capturedFrames)
        {
            CheckEqual(EdifierHecate.Checksum(frame), frame[1], $"校验和复现抓包：{why}");
        }

        // 会话初始化序列必须与官方 App 的抓包逐帧对上（含校验和字节）
        string[] expectedSession =
        {
            "04 AA 00 AA", "04 01 00 01", "04 1B 00 03 18", "04 25 00 03 0A 18",
            "04 1F 00 07 18", "04 37 00 07 18 18", "04 4F 00 07 18 30", "04 67 00 07 18 48",
            "04 7F 00 07 18 60", "04 85 00 07 06 78", "04 02 00 02", "04 06 00 05 01",
            "04 1D 00 05 18", "04 35 00 05 18 18", "04 4D 00 05 18 30", "04 59 00 05 0C 48",
        };
        CheckEqual(EdifierHecate.SessionSequence.Length, expectedSession.Length,
            "会话序列帧数与抓包一致（16 帧）");

        for (int i = 0; i < expectedSession.Length; i++)
        {
            (byte command, byte length, byte offset) = EdifierHecate.SessionSequence[i];
            byte[] built = EdifierHecate.BuildRequest(command, length, offset);
            string[] expectedBytes = expectedSession[i].Split(' ');
            string got = string.Join(' ', built.Take(expectedBytes.Length).Select(b => b.ToString("X2")));
            CheckEqual(got, expectedSession[i], $"会话第 {i + 1} 帧复现抓包");
        }

        // 抓包原文回放：04 20 00 1a 06 00 00 00 5b → 91%
        var captured = new byte[64];
        captured[0] = 0x04; captured[1] = 0x20; captured[3] = 0x1A; captured[4] = 0x06; captured[8] = 0x5B;
        Check(EdifierHecate.TryParseBatteryReply(captured, 0x20, out int capturedPct, out bool capturedCharging),
            "解析抓包里的电量应答");
        CheckEqual(capturedPct, 91, "抓包 0x5B → 91%");
        CheckEqual(capturedCharging, false, "抓包那一帧是放电中");
        Check(!EdifierHecate.TryParseBatteryReply(captured, 0x21, out _, out _), "序号对不上时不认（防陈旧应答）");

        var hecate = new FakeHecateMouse { BatteryPercent = 91, Stage = 5 };
        using (var ed2 = new EdifierHecate(hecate, NullLog.Instance))
        {
            Check(ed2.TryReadBattery(BatteryScale.Direct, out BatterySample activeSample, out string activeError),
                $"主动查询读到电量（{activeError}）");
            CheckEqual(activeSample.Percent, 91, "主动查询 91%");
            Check(hecate.OutputCount >= 1, "确实发了 output report");
            Check(hecate.InterruptReadCount >= 1, "报文走的是 interrupt ReadFile（真机上只有这条能取到）");
            CheckEqual(ed2.ReadMethod, "interrupt ReadFile（overlapped）", "诊断能报出读方式");
            CheckEqual(hecate.HeartbeatCount, 1, "开局先发一次握手（0xAA），且只发一次");

            CheckEqual(hecate.SetStageCount, 0, "全程没发过 0x28（那是设置命令，发它会改掉鼠标档位）");
            CheckEqual(hecate.HeartbeatCount, 1, "会话只跑一遍就够（末帧有应答）");

            hecate.ResetChunks();   // 会话初始化自己也读过页，先清掉
            hecate.Commands.Clear();
            Check(ed2.TryReadPage(EdifierHecate.PageSettings, EdifierHecate.SettingsPageLength,
                    out byte[] page, out string pageError),
                $"读到设置页 84 字节（{pageError}）");
            CheckEqual(page.Length, 84, "设置页长度 84");
            CheckEqual(page[12], (byte)5, "页内 [12] = 当前档位（抓包里就是这一格）");
            CheckEqual(hecate.LastChunks, "24@0,24@18,24@30,12@48",
                "分块读法：0x18/0x18/0x18/0x0C 四块（与官方 App 一致）");
            CheckEqual(string.Join(',', hecate.Commands.Select(c => c.ToString("X2"))),
                "01,05,05,05,05,02",
                "读页前后用 0x01/0x02 夹住（官方 App 的做法，缺了会读到空页）");

            // 设备回空帧（没进配置会话）时必须自动重试，而不是把字段 0 当成 DPI
            hecate.ZeroSettingsPageReplies = 4;   // 正好一次读页的四块
            Check(ed2.TryReadDpi(out int retryX, out _, out string retryError),
                $"空回显会自动重试直到读到数据（{retryError}）");
            CheckEqual(retryX, 1600, "重试后拿到 1600");
            CheckEqual(hecate.ZeroSettingsPageReplies, 0, "四块空回显都被消耗掉");

            hecate.ZeroSettingsPageReplies = 999;
            Check(!ed2.TryReadDpi(out _, out _, out string emptyError), "一直回空帧时读 DPI 失败");
            hecate.ZeroSettingsPageReplies = 0;

            // ── DPI：7 档记录表（每条 9 字节，自 0x10 起），DPI = (raw + 1) × 50 ──
            CheckEqual(EdifierHecate.DpiTableOffset, 0x10, "档位表起点 0x10");
            CheckEqual(EdifierHecate.DpiRecordSize, 9, "每条记录 9 字节");
            CheckEqual(EdifierHecate.DpiOffsetForStage(0), 0x10, "第 1 档在 0x10");
            CheckEqual(EdifierHecate.DpiOffsetForStage(4), 0x34, "第 5 档在 0x34");
            CheckEqual(EdifierHecate.DpiOffsetForStage(6), 0x46, "第 7 档在 0x46");
            CheckEqual(EdifierHecate.RawToDpi(7), 400, "(7+1)×50 = 400");
            CheckEqual(EdifierHecate.RawToDpi(25), 1300, "(25+1)×50 = 1300");
            CheckEqual(EdifierHecate.DpiToRaw(2500), 49, "2500 → raw 49");

            // 真机页面必须解析成官方 App 显示的七档（用户 2026-10-26 提供）
            hecate.ResetChunks();
            Check(ed2.TryReadPage(EdifierHecate.PageSettings, EdifierHecate.SettingsPageLength,
                    out byte[] realPage, out string realPageError),
                $"读真机页面（{realPageError}）");
            CheckEqual(string.Join(" ", EdifierHecate.DescribeStageTable(realPage)),
                "1档=400 2档=800 3档=1000 4档=1200 5档=1300 6档=1600 7档=2500",
                "七档全部对上官方 App 显示值");

            Check(ed2.TryReadDpi(out int dpiReadX, out int dpiReadY, out string dpiReadError),
                $"读到漫步者 DPI（{dpiReadError}）");
            CheckEqual(ed2.LastDpiStage, 5, "当前档位下标 5（第 6 档）");
            CheckEqual(ed2.LastDpiRaw, 31, "raw 31");
            CheckEqual(dpiReadX, 1600, "(31+1)×50 = 1600");
            CheckEqual(dpiReadY, 1600, "X/Y 同值");

            hecate.WriteChunks.Clear();
            Check(ed2.TryWriteDpi(800, 800, out string dpiWriteError), $"写 DPI 800（{dpiWriteError}）");
            CheckEqual(string.Join(',', hecate.WriteChunks), "24@0,24@24,24@48,12@72",
                "整页四块回写（与官方 App 一致，缺块会被设备忽略）");
            Check(ed2.TryReadDpi(out int dpiAfter, out _, out _), "写后能读回");
            CheckEqual(dpiAfter, 800, "读回 800");
            Check(ed2.TryWriteDpi(1600, 1600, out _), "把第 6 档写回 1600");

            // 写完设备把当前档位挪回第 1 档：校验必须按"写入的那个偏移"而不是"当前档位"，
            // 否则会误判失败、再把刚写成功的值覆盖回去（2026-10-26 真机 400→1600 就是这样失败的）
            hecate.MoveStageAfterWrite = true;
            Check(ed2.TryWriteDpi(1000, 1000, out string movedError),
                $"设备挪档后仍按偏移校验、判定成功（{movedError}）");
            CheckEqual(ed2.LastDpiStage, 0, "上报的当前档位是设备改后的 0");
            CheckEqual(hecate.Pages[EdifierHecate.PageSettings][EdifierHecate.DpiOffsetForStage(5)], (byte)19,
                "第 6 档那格确实是 raw 19（1000），没被恢复原值覆盖");
            hecate.MoveStageAfterWrite = false;

            // 换到第 1 档：字段在 0x10，应该写第一块（偏移 0）
            hecate.Pages[EdifierHecate.PageSettings][EdifierHecate.SettingsStageIndexOffset] = 0;
            hecate.WriteChunks.Clear();
            Check(ed2.TryWriteDpi(2500, 2500, out string firstStageError),
                $"把第 1 档改成 2500（{firstStageError}）");
            CheckEqual(hecate.Pages[EdifierHecate.PageSettings][EdifierHecate.DpiOffsetForStage(0)], (byte)49,
                "第 1 档那格被写成 raw 49（2500）");
            Check(ed2.TryReadDpi(out int firstStageDpi, out _, out _), "读回第 1 档");
            CheckEqual(firstStageDpi, 2500, "第 1 档 = 2500");
            hecate.Pages[EdifierHecate.PageSettings][EdifierHecate.SettingsStageIndexOffset] = 5;

            CheckEqual(EdifierHecate.NormalizeDpi(1234), 1250, "规整到 50 步进");
            CheckEqual(EdifierHecate.NormalizeDpi(1), 50, "下限 50");
            CheckEqual(EdifierHecate.NormalizeDpi(99999), 26000, "上限 26000");
        }

        // CAN 不通时退回被动帧
        var roOnly = new FakeHecateMouse { BatteryPercent = 42, Writable = false };
        roOnly.QueueInput(heartbeat, status);
        using (var ed3 = new EdifierHecate(roOnly, NullLog.Instance))
        {
            Check(ed3.TryReadBattery(BatteryScale.Direct, out BatterySample fallback, out string fallbackError),
                $"只读句柄退回被动帧（{fallbackError}）");
            CheckEqual(fallback.Percent, 67, "被动帧仍是 67%");
            CheckEqual(roOnly.OutputCount, 0, "只读句柄一帧都没写出去");
        }

        Check(EdifierHecate.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x320F, ProductId = 0x706E, UsagePage = 0xFF1C, Usage = 0x0092,
            InputReportLength = 64, OutputReportLength = 64,
        }), "接口判据：VID:PID + 0xFF1C/0x0092 + 64/64");
        Check(!EdifierHecate.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x320F, ProductId = 0x706E, UsagePage = 0xFF1C, Usage = 0x0092,
            InputReportLength = 33, OutputReportLength = 33,
        }), "同接收器的 33B 双向集合不认（技能里记的诱饵接口）");
        Check(EdifierHecate.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x320F, ProductId = 0x706E, UsagePage = 0xFF1C, Usage = 0x0092,
            InputReportLength = 65, OutputReportLength = 65,
        }), "65 字节变体仍认（不同固件可能多一字节）");
    }

    // ────────────────────────── 高斯键盘（只读电量） ──────────────────────────
    private static void TestGanss()
    {
        // 接口判据
        Check(GanssKeyboard.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x05AC, ProductId = 0x024F, UsagePage = 0xFF60, Usage = 0x0061,
            InputReportLength = 33, OutputReportLength = 33,
        }), "接口判据：05AC:024F + 0xFF60/0x0061 + 33/33");

        Check(!GanssKeyboard.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x05AC, ProductId = 0x024F, UsagePage = 0xFF60, Usage = 0x0061,
            InputReportLength = 65, OutputReportLength = 65,
        }), "同 usage 的 65/65 集合不认（技能里的老坑）");

        Check(!GanssKeyboard.MatchesInterface(new HidDeviceInfo
        {
            VendorId = 0x05AC, ProductId = 0x024F, UsagePage = 0x0001, Usage = 0x0006,
            InputReportLength = 33, OutputReportLength = 33,
        }), "键盘引导协议集合（usage 0001/0006）不认");

        // 查询帧
        byte[] query = GanssKeyboard.BuildQuery();
        CheckEqual(query.Length, 33, "查询帧 33 字节（含 report ID）");
        CheckEqual(query[0], (byte)0x00, "首字节是 report ID 0");
        CheckEqual(query[1], (byte)0x20, "[1] = 0x20");
        CheckEqual(query[2], (byte)0x01, "[2] = 0x01");
        Check(query.Skip(3).All(b => b == 0), "其余 30 字节全 0");

        // 应答解析：带/不带 report ID 前缀都要认
        Check(GanssKeyboard.TryParseReply(new byte[] { 0x00, 0x20, 0x01, 0x00, 0x2C }, out int parsedWithId),
            "解析应答（带 report ID 前缀）");
        CheckEqual(parsedWithId, 44, "电量 44%（0x2C）");
        Check(GanssKeyboard.TryParseReply(new byte[] { 0x20, 0x01, 0x00, 0x64 }, out int parsedNoId),
            "解析应答（不带前缀）");
        CheckEqual(parsedNoId, 100, "电量 100%");
        Check(!GanssKeyboard.TryParseReply(new byte[] { 0x04, 0xAA, 0x00, 0xAA }, out _),
            "别的帧不当电量应答");
        Check(!GanssKeyboard.TryParseReply(new byte[] { 0x20, 0x01, 0x00, 0xFF }, out _),
            "电量字节 > 100 时不认（继续往后找）");

        static FakeTransport Keyboard(bool readOnly = false) => new(0)
        {
            Vid = 0x05AC, Pid = 0x024F, UsagePage = 0xFF60, Usage = 0x0061,
            InLength = 33, OutLength = 33, ReadOnly = readOnly,
        };

        // 正常读
        FakeTransport t = Keyboard();
        t.QueueInput(new byte[] { 0x00, 0x20, 0x01, 0x00, 0x2C });
        using (var kb = new GanssKeyboard(t, NullLog.Instance))
        {
            Check(kb.SupportsBattery, "支持电量");
            Check(!kb.SupportsDpi, "不支持 DPI");
            CheckEqual(kb.MaxDpi, 0, "MaxDpi 为 0");
            Check(kb.TryReadBattery(BatteryScale.Raw255, out BatterySample sample, out string error),
                $"读到电量（{error}）");
            CheckEqual(sample.Percent, 44, "44%");
            CheckEqual(sample.RawValue, (byte)44, "raw 就是百分比");
            CheckEqual(sample.Scale, BatteryScale.Direct, "口径记为 Direct（设备直接报百分比）");
            CheckEqual(sample.Charging, (bool?)null, "充电状态未知（协议里没有）");
            CheckEqual(t.LastOutput is null ? 0 : t.LastOutput.Length, 33, "写出去的是 33 字节");
            CheckEqual(t.LastOutput is null ? -1 : t.LastOutput[1], 0x20, "写出去 [1]=0x20");
            CheckEqual(t.LastOutput is null ? -1 : t.LastOutput[2], 0x01, "写出去 [2]=0x01");
            Check(!kb.TryReadDpi(out _, out _, out _), "读 DPI 直接失败");
            Check(!kb.TryWriteDpi(800, 800, out _), "写 DPI 直接失败");
            CheckEqual(kb.ReadMethod, "HidD_GetInputReport 轮询", "非 interrupt 通道时读方式写清");
        }

        // 无效帧跳过 → 同一窗口内继续等到有效帧
        FakeTransport t2 = Keyboard();
        t2.QueueInput(new byte[] { 0x00, 0xAA, 0x00, 0xAA, 0x00 },
                      new byte[] { 0x00, 0x20, 0x01, 0x00, 0x37 });
        using (var kb2 = new GanssKeyboard(t2, NullLog.Instance))
        {
            Check(kb2.TryReadBattery(BatteryScale.Raw255, out BatterySample sample2, out string error2),
                $"跳过无效帧后读到电量（{error2}）");
            CheckEqual(sample2.Percent, 55, "55%（0x37）");
        }

        // 只读句柄：立刻失败，且说明原因
        FakeTransport ro = Keyboard(readOnly: true);
        using (var kb3 = new GanssKeyboard(ro, NullLog.Instance))
        {
            Check(!kb3.TryReadBattery(BatteryScale.Raw255, out _, out string roError, out ReadOutcome roOutcome),
                "只读句柄读不到电量");
            Check(roError.Contains("不可写"), $"失败原因说清是不可写：{roError}");
            CheckEqual(roOutcome, ReadOutcome.NotWritable, "语义 = NotWritable");
            CheckEqual(ro.WriteCount, 0, "一帧都没写出去");
        }

        // 整个应答窗口都没报文：重试 3 次后失败（键盘成功率约 50%，错误里要写清重试情况）
        // v1.14.0：真机日志里出现过"连续两次整窗无应答、隔一轮又能读到"，所以次数从 2 提到 3。
        FakeTransport silent = Keyboard();
        using (var kb4 = new GanssKeyboard(silent, NullLog.Instance))
        {
            Check(!kb4.TryReadBattery(BatteryScale.Raw255, out _, out string silentError, out ReadOutcome silentOutcome),
                "没应答时读取失败");
            CheckEqual(silentOutcome, ReadOutcome.NoResponse, "整窗没报文 = NoResponse");
            CheckEqual(silent.WriteCount, 3, "一共试了 3 次（键盘休眠窗口常连丢两轮）");
            Check(silentError.Contains("试了 3 次"), $"失败原因写清重试次数：{silentError}");
        }

        // 目录排序：33/33 的高斯接口要出现在候选里
        List<DeviceCandidate> ranked = DeviceCatalog.RankCandidates(new[]
        {
            new HidDeviceInfo
            {
                Path = @"\\?\kb", VendorId = 0x05AC, ProductId = 0x024F,
                UsagePage = 0xFF60, Usage = 0x0061,
                InputReportLength = 33, OutputReportLength = 33,
            },
        }, NullLog.Instance);
        CheckEqual(ranked.Count, 1, "高斯接口产生 1 个候选");
        CheckEqual(ranked[0].Family, DeviceFamily.Ganss, "族是高斯");
        CheckEqual(ranked[0].FamilyName, "高斯", "族名是高斯");

        // 白名单必须包含高斯的 VID —— 之前漏了 05AC，设备类写好了但枚举根本没带上它
        Check(DeviceCatalog.SupportedVendorIds.Contains(GanssKeyboard.VendorId),
            "VID 白名单包含 05AC（否则托盘枚举不到键盘）");
    }

    private static void TestDeviceCatalog()
    {
        var razer = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_1532&pid_007b&mi_00#x",
            VendorId = 0x1532, ProductId = 0x007B, UsagePage = 0x0001, Usage = 0x0002,
            FeatureReportLength = 91, InputReportLength = 9,
        };
        var rapoo = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_24ae&pid_1205&mi_01#x",
            VendorId = 0x24AE, ProductId = 0x1205, UsagePage = 0xFF00, Usage = 0x000E,
            InputReportLength = 32, OutputReportLength = 32,
        };
        var edifier = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_320f&pid_706e&mi_01#x",
            VendorId = 0x320F, ProductId = 0x706E, UsagePage = 0xFF1C, Usage = 0x0092,
            InputReportLength = 64, OutputReportLength = 64,
        };
        var decoy = new HidDeviceInfo
        {
            Path = @"\\?\hid#vid_320f&pid_706e&mi_01&col02#x",
            VendorId = 0x320F, ProductId = 0x706E, UsagePage = 0xFF1C, Usage = 0x0092,
            InputReportLength = 33, OutputReportLength = 33,
        };

        var ranked = DeviceCatalog.RankCandidates(new[] { edifier, rapoo, decoy, razer }, NullLog.Instance);
        CheckEqual(ranked.Count, 3, "三族各命中一个，长度不符的被排除");
        CheckEqual(ranked[0].Family, DeviceFamily.Razer, "雷蛇排第一");
        CheckEqual(ranked[1].Family, DeviceFamily.Rapoo, "雷柏排第二");
        CheckEqual(ranked[2].Family, DeviceFamily.Edifier, "漫步者排第三");
        Check(ranked[0].Describe().Contains("91"), "描述里带通道长度，便于看日志");

        var empty = DeviceCatalog.RankCandidates(Array.Empty<HidDeviceInfo>(), NullLog.Instance);
        CheckEqual(empty.Count, 0, "没有接口时候选为空");

        // ── 手柄（v1.16 新加）──
        var switchPad = new HidDeviceInfo
        {
            Path = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002057e_pid&2009&col01#x",
            VendorId = 0x057E, ProductId = 0x2009, UsagePage = 0x0001, Usage = 0x0005,
            InputReportLength = 362, OutputReportLength = 49,
        };
        var switchKeyboard = new HidDeviceInfo
        {
            Path = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002057e_pid&2009&col02#x",
            VendorId = 0x057E, ProductId = 0x2009, UsagePage = 0x0001, Usage = 0x0006,
            InputReportLength = 49, OutputReportLength = 49,
        };

        List<DeviceCandidate> withPad = DeviceCatalog.RankCandidates(
            new[] { switchKeyboard, switchPad }, NullLog.Instance);
        CheckEqual(withPad.Count, 1, "Switch Pro 只认手柄集合（0001/0005）");
        CheckEqual(withPad[0].Family, DeviceFamily.Nintendo, "族 = 任天堂");
        CheckEqual(withPad[0].ModelName, "Switch Pro 手柄", "型号名");
        Check(withPad[0].FamilyName == "任天堂", "族名 = 任天堂（托盘行显示用）");

        // VID 白名单必须包含 0x057E，否则"驱动写好了但枚举根本不带它"（高斯那次就踩过）
        Check(DeviceCatalog.SupportedVendorIds.Contains((ushort)0x057E), "枚举白名单含任天堂 057E");

        // ── 接口路径解析：USB 与蓝牙是两种形态（2026-11-02 真机：只认 vid_ 把 Switch Pro 整族漏枚举）──
        const string usbRazer = @"\\?\hid#vid_1532&pid_007b&mi_02#7&2f0e4b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        CheckEqual(HidPath.VendorId(usbRazer), (ushort)0x1532, "USB 路径 vid_1532 解析出 1532");
        CheckEqual(HidPath.ProductId(usbRazer), (ushort)0x007B, "USB 路径 pid_007b 解析出 007B");

        const string btSwitch = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002057e&pid&2009#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        CheckEqual(HidPath.VendorId(btSwitch), (ushort)0x057E, "蓝牙路径 vid&0002057e 取低 4 位得 057E");
        CheckEqual(HidPath.ProductId(btSwitch), (ushort)0x2009, "蓝牙路径 pid&2009 解析出 2009");
        Check(HidPath.MatchesVendor(btSwitch, 0x057E), "蓝牙 057E 路径能过白名单（否则 Switch Pro 枚举不到）");
        Check(!HidPath.MatchesVendor(btSwitch, 0x045E), "蓝牙 057E 路径不被误判成 045E");
        Check(HidPath.MatchesVendor(usbRazer, 0x1532), "USB 路径照旧能过白名单");

        const string btHogp = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_vid&0002057e&pid&2009#a&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        CheckEqual(HidPath.VendorId(btHogp), (ushort)0x057E, "HOGP 形态（00001812）同样解析出 057E");

        CheckEqual(HidPath.VendorId(@"\\?\hid#no-vid-here#1"), (ushort)0, "不含 VID 的路径返回 0（不误匹配白名单）");

        // XInput 手柄是合成的候选：没有 VID:PID，靠 IdOverride
        var xboxInfo = new HidDeviceInfo { Path = "xinput://slot0", VendorId = 0x045E, Opened = true };
        var xboxCandidate = new DeviceCandidate(DeviceFamily.Xbox, xboxInfo, "Xbox 手柄（槽 1）", 0,
            IdOverride: "xinput:0");
        CheckEqual(xboxCandidate.Id, "xinput:0", "XInput 候选的 Id 用覆盖值，不显示 045E:0000");
        CheckEqual(xboxCandidate.FamilyName, "Xbox", "族名 = Xbox");
    }

    // ────────────────────────── 轮询策略 ──────────────────────────

    private static void TestPollPolicy()
    {
        var policy = new PollPolicy(
            connectedInterval: TimeSpan.FromSeconds(60),
            disconnectedInterval: TimeSpan.FromSeconds(5),
            retryInterval: TimeSpan.FromSeconds(10),
            failuresBeforeReopen: 3);

        CheckEqual(policy.State, LinkState.Unknown, "初始状态 Unknown");

        Check(policy.OnSuccess(), "首次成功算状态变化");
        CheckEqual(policy.State, LinkState.Ok, "成功后 Ok");
        CheckEqual(policy.NextDelay.TotalSeconds, 60d, "连着时 60s 一轮");
        Check(!policy.OnSuccess(), "再次成功不算状态变化（不重复打日志）");

        Check(policy.OnFailure(deviceMissing: true), "设备消失算状态变化");
        CheckEqual(policy.State, LinkState.Missing, "状态转 Missing");
        CheckEqual(policy.NextDelay.TotalSeconds, 5d, "断开后 5s 重扫");
        Check(!policy.ShouldReopen, "首次失败还不重开句柄");

        Check(!policy.OnFailure(deviceMissing: true), "连续缺失不再算变化");
        CheckEqual(policy.ConsecutiveFailures, 2, "连续失败累计 2");
        policy.OnFailure(deviceMissing: true);
        Check(policy.ShouldReopen, "连续 3 次失败要求重开句柄");

        policy.OnSuccess();
        Check(!policy.ShouldReopen, "成功后失败计数清零");
        CheckEqual(policy.ConsecutiveFailures, 0, "失败计数归零");

        policy.OnFailure(deviceMissing: false);
        CheckEqual(policy.State, LinkState.Degraded, "设备在但读失败 → Degraded");
        CheckEqual(policy.NextDelay.TotalSeconds, 10d, "Degraded 用 10s 重试");
        Check(policy.Describe().Contains("Degraded"), "Describe 可读");
    }

    // ────────────────────────── 托盘图元 ──────────────────────────

    /// <summary>
    /// 桌面浮窗的纯几何/文案断言：行布局、命中区域、拖拽缩放、位置持久化、状态语义。
    /// 绘制本身（GDI+）只能在 Windows 上看，但"行放哪儿、鼠标落到哪个边、位置怎么存"全是纯算术，
    /// 必须在这里跑死 —— 出错的代价是用户抓不住窗口，或者一开交互就跳回左上角。
    /// </summary>
    private static void TestOverlay()
    {
        static bool Overlaps(RectI a, RectI b) =>
            a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

        // ── 行布局 ──
        var client = new RectI(0, 0, 300, 160);
        OverlayFrame one = OverlayLayout.Compute(client, 1, 1.0);
        CheckEqual(1, one.RowCount, "浮窗·1 台设备 = 1 行");
        Check(one.Rows[0].Width >= one.Content.Width - 1, "浮窗·单行铺满可用宽度");
        Check(one.Rows[0].Bottom <= client.Bottom && one.Rows[0].Right <= client.Right, "浮窗·行不越界");
        Check(!one.ShowHeader && one.Header.Height == 0, "浮窗·没有标题行（用户要求去掉）");
        CheckEqual(one.Rows[0].Y, one.Content.Y, "浮窗·第一行贴内容区顶部");

        OverlayFrame four = OverlayLayout.Compute(new RectI(0, 0, 320, 300), 4, 1.0);
        CheckEqual(4, four.RowCount, "浮窗·4 台设备 4 行");
        for (int i = 0; i < four.Rows.Count; i++)
        {
            Check(four.Rows[i].X >= four.Content.X && four.Rows[i].Right <= four.Content.Right &&
                  four.Rows[i].Y >= four.Content.Y && four.Rows[i].Bottom <= four.Content.Bottom,
                $"浮窗·第 {i} 行在内容区内");
            Check(four.Rows[i].Height >= 20, $"浮窗·第 {i} 行高度可用");
            for (int j = i + 1; j < four.Rows.Count; j++)
            {
                Check(!Overlaps(four.Rows[i], four.Rows[j]), $"浮窗·第 {i}/{j} 行不重叠");
            }
        }

        CheckEqual(0, OverlayLayout.Compute(client, 0, 1.0).RowCount, "浮窗·0 台设备没有行");

        OverlayFrame tiny = OverlayLayout.Compute(new RectI(0, 0, 60, 40), 3, 1.0);
        CheckEqual(3, tiny.RowCount, "浮窗·极小窗口仍给每台设备留位");
        Check(tiny.Rows.All(r => r.Height >= 1 && r.Width >= 1), "浮窗·极小窗口行不为 0 尺寸");

        // ── 尺寸 ──
        (int Width, int Height) min1 = OverlayLayout.MinSize(1, 1.0);
        (int Width, int Height) min4 = OverlayLayout.MinSize(4, 1.0);
        (int Width, int Height) minHi = OverlayLayout.MinSize(1, 2.0);
        Check(min4.Height > min1.Height, "浮窗·设备多时最小高度更大");
        Check(min1.Width >= 220, "浮窗·最小宽度够放环形+名字");
        Check(minHi.Width > min1.Width, "浮窗·高 DPI 下最小尺寸更大");
        Check(OverlayLayout.PreferredHeight(3, 1.0) >= OverlayLayout.MinSize(3, 1.0).Height,
            "浮窗·默认高度不小于最小高度");
        Check(OverlayLayout.PreferredWidth(1.0) > min1.Width, "浮窗·默认宽度比最小宽度略宽");

        // 默认位置：右上角 + 离边距 32（不贴边）
        var work = new RectI(0, 0, 1920, 1040);
        RectI defaultBounds = OverlayLayout.DefaultBounds(work, 292, 138, 1.0, 236, 92);
        CheckEqual(work.Right - 292 - 32, defaultBounds.X, "浮窗·默认贴右上角并按边距内缩");
        CheckEqual(32, defaultBounds.Y, "浮窗·默认顶部留 32px");
        CheckEqual(292, defaultBounds.Width, "浮窗·默认宽度不变");

        // ── 命中区域 ──
        var box = new RectI(0, 0, 200, 100);
        CheckEqual(OverlayZone.TopLeft, OverlayLayout.HitTest(box, 1, 1, 1.0), "浮窗·左上角命中缩放");
        CheckEqual(OverlayZone.BottomRight, OverlayLayout.HitTest(box, 198, 98, 1.0), "浮窗·右下角命中缩放");
        CheckEqual(OverlayZone.Right, OverlayLayout.HitTest(box, 198, 50, 1.0), "浮窗·右边命中横向缩放");
        CheckEqual(OverlayZone.Top, OverlayLayout.HitTest(box, 100, 1, 1.0), "浮窗·上边命中纵向缩放");
        CheckEqual(OverlayZone.Body, OverlayLayout.HitTest(box, 100, 50, 1.0), "浮窗·中间命中拖动");
        CheckEqual(OverlayZone.Outside, OverlayLayout.HitTest(box, 300, 300, 1.0), "浮窗·界外不算命中");
        CheckEqual(OverlayZone.Body, OverlayLayout.HitTest(box, 20, 20, 3.0), "浮窗·高 DPI 下内缩也不会误判边");

        // ── 拖动 / 缩放 ──
        var start = new RectI(100, 100, 240, 120);
        RectI moved = OverlayLayout.ApplyDrag(start, OverlayZone.Body, 15, -10, 236, 92);
        CheckEqual(115, moved.X, "浮窗·拖动改 X");
        CheckEqual(90, moved.Y, "浮窗·拖动改 Y");
        CheckEqual(240, moved.Width, "浮窗·拖动不改宽");

        RectI resized = OverlayLayout.ApplyDrag(start, OverlayZone.BottomRight, 30, 20, 236, 92);
        CheckEqual(270, resized.Width, "浮窗·右下缩放改宽");
        CheckEqual(140, resized.Height, "浮窗·右下缩放改高");
        CheckEqual(100, resized.X, "浮窗·右下缩放不动左上角");

        RectI leftResize = OverlayLayout.ApplyDrag(start, OverlayZone.Left, 40, 0, 236, 92);
        CheckEqual(340, leftResize.Right, "浮窗·左边界缩放时右边固定");
        CheckEqual(236, leftResize.Width, "浮窗·左边界缩放受最小宽度保护");
        RectI wideResize = OverlayLayout.ApplyDrag(new RectI(100, 100, 400, 120), OverlayZone.Left, 40, 0, 236, 92);
        CheckEqual(500, wideResize.Right, "浮窗·宽窗左边界缩放时右边固定");
        CheckEqual(360, wideResize.Width, "浮窗·宽窗左边界缩放改宽");

        RectI clamped = OverlayLayout.ApplyDrag(start, OverlayZone.BottomRight, -500, -500, 236, 92);
        CheckEqual(236, clamped.Width, "浮窗·缩放有最小宽度");
        CheckEqual(92, clamped.Height, "浮窗·缩放有最小高度");

        // ── 位置持久化 ──
        OverlayPlacement placement = OverlayPlacementMath.Capture(
            new RectI(work.Right - 292 - 32, 32, 292, 138), work, @"\\.\\DISPLAY1");
        Check(placement.IsValid, "浮窗·位置记录有效");
        RectI restored = OverlayPlacementMath.Resolve(placement, work, 236, 92);
        Check(Math.Abs(restored.X - (work.Right - 292 - 32)) <= 1 && Math.Abs(restored.Y - 32) <= 1 &&
              Math.Abs(restored.Width - 292) <= 1 && Math.Abs(restored.Height - 138) <= 1,
            "浮窗·同分辨率还原到原位置（切换交互开关不会跳回左上角）");

        var halfWork = new RectI(0, 0, 960, 520);
        RectI half = OverlayPlacementMath.Resolve(placement, halfWork, 236, 92);
        Check(half.Width == 236, "浮窗·半屏下受最小宽度保护（不缩到看不见）");

        OverlayPlacement bigPlacement = OverlayPlacementMath.Capture(
            new RectI(work.Right - 600 - 32, 32, 600, 300), work, @"\\.\\DISPLAY1");
        RectI halfBig = OverlayPlacementMath.Resolve(bigPlacement, halfWork, 236, 92);
        Check(Math.Abs(halfBig.Right - (960 - 16)) <= 2, "浮窗·换分辨率后保持比例右边距");
        Check(Math.Abs(halfBig.Width - 300) <= 2, "浮窗·换分辨率后尺寸等比缩小");

        RectI offscreen = OverlayPlacementMath.Resolve(
            new OverlayPlacement(@"\\.\\DISPLAY9", 0.9, 0.9, 0.4, 0.4), work, 236, 92);
        Check(offscreen.Right <= work.Right && offscreen.Bottom <= work.Bottom, "浮窗·越界记录被拉回可见区域");

        RectI fallback = OverlayPlacementMath.Resolve(default, work, 236, 92);
        Check(fallback.Width >= 236 && fallback.Height >= 92 && fallback.Right <= work.Right,
            "浮窗·非法记录给出可用默认框（不是 0 尺寸）");
        Check(fallback.X < work.Right && fallback.Y >= work.Y, "浮窗·默认框落在右上区域");

        // ── 文案 ──
        CheckEqual("VT9 PRO", OverlayText.ShortLabel("VT9 Pro（无线接收器）"), "浮窗·标签去掉括号备注");
        CheckEqual("PERIPHERAL", OverlayText.ShortLabel("peripheral"), "浮窗·标签转大写");
        CheckEqual("VT9 PRO", OverlayText.ShortLabel("  VT9   Pro "), "浮窗·标签压缩空白并转大写");
        CheckEqual("DEVICE", OverlayText.ShortLabel(""), "浮窗·空名字有兜底");
        Check(OverlayText.ShortLabel(new string('A', 60)).Length <= OverlayText.MaxLabelLength, "浮窗·超长标签被截断");
        Check(OverlayText.ShortLabel(new string('A', 60)).EndsWith("…"), "浮窗·截断后带省略号");
        CheckEqual("STAGE 5/5", OverlayText.StageLabel(4, 5), "浮窗·档位文字为纯 ASCII");
        CheckEqual("STAGE 1/4", OverlayText.StageLabel(0, 4), "浮窗·档位序号从 1 起算");
        Check(OverlayText.StageLabel(null, 5) is null, "浮窗·非档位设备没有档位文字");
        Check(OverlayText.StageLabel(2, 0) is null, "浮窗·档位数非法时不显示");
        Check(OverlayText.StageLabel(9, 5) is not null && OverlayText.StageLabel(9, 5)!.EndsWith("/5"),
            "浮窗·档位越界也不会崩");

        var live = new OverlayDevice("k1", "雷蛇", "Viper Ultimate", 87, false, false, null);
        var charging = live with { Charging = true };
        var stale = live with { Stale = true };
        var missing = new OverlayDevice("k2", "雷柏", "VT9 Pro", null, false, false, null);

        CheckEqual("LINK", live.StatusText, "浮窗·在线状态");
        CheckEqual("CHARGING", charging.StatusText, "浮窗·充电状态");
        CheckEqual("STALE", stale.StatusText, "浮窗·陈旧读数状态");
        CheckEqual("NO LINK", missing.StatusText, "浮窗·未应答状态");
        CheckEqual("NO RESP", (missing with { Stale = true }).StatusText, "浮窗·从未读到的陈旧状态");
        Check(live.StatusText.Length <= 8 && charging.StatusText.Length <= 8 && stale.StatusText.Length <= 8,
            "浮窗·状态词够短（窄窗格不会被省略号吃掉）");
        CheckEqual("87", live.PercentText, "浮窗·百分比文本");
        CheckEqual("--", missing.PercentText, "浮窗·没读数是 -- 而不是 0");

        // ── 环形与配色 ──
        CheckEqual(0.0, OverlayTheme.RingSweep(0), "浮窗·0% 不画弧");
        Check(OverlayTheme.RingSweep(1) > 0 && OverlayTheme.RingSweep(1) < 10, "浮窗·1% 至少有一点弧");
        CheckEqual(OverlayTheme.RingSweepAngle, OverlayTheme.RingSweep(100), "浮窗·100% 画满整段弧");
        Check(OverlayTheme.RingSweep(50) > 145 && OverlayTheme.RingSweep(50) < 155, "浮窗·50% 约一半弧长");
        CheckEqual(0.0, OverlayTheme.RingSweep(null), "浮窗·没读数不画弧");
        CheckEqual(OverlayAccent.Critical, OverlayTheme.AccentFor(10, false), "浮窗·10% 危急色");
        CheckEqual(OverlayAccent.Low, OverlayTheme.AccentFor(25, false), "浮窗·25% 低电量色");
        CheckEqual(OverlayAccent.Healthy, OverlayTheme.AccentFor(60, false), "浮窗·60% 正常色");
        CheckEqual(OverlayAccent.Charging, OverlayTheme.AccentFor(5, true), "浮窗·充电优先于低电量色");

        // ── 快照 ──
        var snapshot = new OverlaySnapshot(new[] { live, missing }, DateTimeOffset.Now);
        CheckEqual(2, snapshot.Count, "浮窗·快照设备数");
        Check(snapshot.KeySignature.Contains("k1") && snapshot.KeySignature.Contains("k2"), "浮窗·签名包含全部设备");
        CheckEqual(0, OverlaySnapshot.Empty.Count, "浮窗·空快照没有设备");
    }

    private static void TestTrayImage()
    {
        var image = TrayImageFactory.Render(TrayIconMode.Percent, 16, new TrayVisualState(true, 74, false));
        CheckEqual(image.Width, 16, "图标宽度 16");
        CheckEqual(image.Height, 16, "图标高度 16");
        CheckEqual(image.Pixels.Length, 256, "像素数 16x16");
        Check(image.OpaqueCount() > 10, "数字模式确实画了像素");
        CheckEqual(TrayImageFactory.ColorFor(new TrayVisualState(true, 74, false)),
            unchecked((int)0xFF4CAF50), "74% 用绿色");

        var low = TrayImageFactory.Render(TrayIconMode.Percent, 16, new TrayVisualState(true, 12, false));
        CheckEqual(TrayImageFactory.ColorFor(new TrayVisualState(true, 12, false)),
            unchecked((int)0xFFE53935), "12% 用红色");
        CheckEqual(TrayImageFactory.ColorFor(new TrayVisualState(true, 35, false)),
            unchecked((int)0xFFF5A623), "35% 用琥珀色");
        CheckEqual(TrayImageFactory.ColorFor(new TrayVisualState(true, 80, true)),
            unchecked((int)0xFF00BCD4), "充电中用青色");
        CheckEqual(TrayImageFactory.ColorFor(TrayVisualState.Disconnected),
            unchecked((int)0xFF8C8C8C), "断开时用灰色");

        CheckEqual(TrayImageFactory.TextFor(new TrayVisualState(true, 74, false)), "74", "文本 74");
        CheckEqual(TrayImageFactory.TextFor(new TrayVisualState(true, 100, false)), "100", "文本 100");
        CheckEqual(TrayImageFactory.TextFor(TrayVisualState.Disconnected), "--", "断开显示 --");
        CheckEqual(TrayImageFactory.TextFor(new TrayVisualState(true, 130, false)), "100", "超过 100 夹住");

        // 16px 下两位数用 2 倍字号（14px 宽），三位数只能 1 倍
        CheckEqual(TrayImageFactory.ChooseScale(2, 16), 2, "两位数字号 = 2");
        CheckEqual(TrayImageFactory.ChooseScale(3, 16), 1, "三位数字号 = 1（放不下 2 倍）");
        CheckEqual(TrayImageFactory.ChooseScale(2, 32), 3, "32px 下两位数可用 3 倍");

        var battery = TrayImageFactory.Render(TrayIconMode.Battery, 16, new TrayVisualState(true, 50, false));
        Check(battery.OpaqueCount() > 10, "电池模式画了外壳");
        var batteryFull = TrayImageFactory.Render(TrayIconMode.Battery, 16, new TrayVisualState(true, 100, false));
        Check(batteryFull.OpaqueCount() > battery.OpaqueCount(), "100% 的填充比 50% 多");

        var disconnected = TrayImageFactory.Render(TrayIconMode.Battery, 16, TrayVisualState.Disconnected);
        Check(disconnected.OpaqueCount() > 4, "断开时仍画出空心电池");

        // 同样输入必须产同样像素（图标不能闪）
        var again = TrayImageFactory.Render(TrayIconMode.Percent, 16, new TrayVisualState(true, 74, false));
        bool identical = image.Pixels.SequenceEqual(again.Pixels);
        Check(identical, "渲染是确定性的（相同输入相同像素）");
    }

    // ────────────────────────── 配置 ──────────────────────────

    private static void TestSettings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "razermini-verify-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");
        try
        {
            var log = new ListLog();
            AppSettings defaults = AppSettings.Load(path, log);
            CheckEqual(defaults.PollIntervalSeconds, 60, "默认轮询 60s");
            CheckEqual(defaults.ParsedBatteryScale, BatteryScale.Raw255, "默认电量口径 Raw255");
            CheckEqual(defaults.ParsedIconMode, TrayIconMode.Percent, "默认图标模式 Percent");
            Check(File.Exists(path), "首次运行会写出默认配置");

            defaults.PollIntervalSeconds = 120;
            defaults.BatteryScale = "Direct";
            defaults.IconMode = "Battery";
            defaults.DpiPresets = new[] { 800, 2400 };
            defaults.Save(path, log);

            AppSettings reloaded = AppSettings.Load(path, log);
            CheckEqual(reloaded.PollIntervalSeconds, 120, "轮询间隔往返一致");
            CheckEqual(reloaded.ParsedBatteryScale, BatteryScale.Direct, "电量口径往返一致");
            CheckEqual(reloaded.ParsedIconMode, TrayIconMode.Battery, "图标模式往返一致");
            CheckEqual(reloaded.DpiPresets.Length, 2, "DPI 预设往返一致");

            File.WriteAllText(path, "{ 这不是合法 json ");
            AppSettings broken = AppSettings.Load(path, log);
            CheckEqual(broken.PollIntervalSeconds, 60, "配置损坏时回退默认值");
            Check(log.Contains("读取配置失败"), "配置损坏有日志（不静默）");

            PollPolicy policy = defaults.CreatePolicy();
            CheckEqual(policy.ConnectedInterval.TotalSeconds, 120d, "配置能生成轮询策略");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ────────────────────────── 工具 ──────────────────────────

    private static RazerMouseModel Model() => RazerCatalog.Find(0x1532, 0x007B)!;

    private static byte[] BuildResponse(byte status, byte transactionId, byte commandClass, byte commandId,
        params (int Index, byte Value)[] arguments)
    {
        byte[] frame = RazerReport.CreateFrame(transactionId, commandClass, commandId, 0x02);
        frame[1] = status;
        foreach ((int index, byte value) in arguments)
        {
            frame[9 + index] = value;
        }
        frame[RazerReport.CrcOffset] = RazerReport.ComputeCrc(frame);
        return frame;
    }
}

/// <summary>
/// 雷柏协议模拟器：模拟 dongle 的 busy→OK 时序 + EEPROM 块读写。
/// 有了它，DPI 的"读表 → 安全校验 → 改字节 → 写回 → 读回校验"整条链路都能在 Linux 上真跑。
/// </summary>
internal sealed class FakeRapooMouse : IHidTransport
{
    private readonly Queue<byte[]> _pending = new();

    public byte[] XTable { get; } = new byte[16];
    public byte[] YTable { get; } = new byte[16];
    public byte[] CurTable { get; } = new byte[4];

    /// <summary>true = 设备收下写帧但不真正落盘（模拟"写入被忽略"）。</summary>
    public bool IgnoreWrites { get; init; }

    /// <summary>X/Y 表 [14] 处的字节。真机 VT9 Pro 上是 0（旧判据误判成"档数不对"）。</summary>
    public byte StageCountByte { get; init; } = 7;

    /// <summary>
    /// 前 N 次块读返回"全 0 块"—— 模拟**鼠标休眠**时接收器 ACK 一个空块
    /// （2026-10-27 真机形态：X/Y/档位三块读回来全是 00）。
    /// </summary>
    public int EmptyBlockReadsFirst { get; init; }

    public int WriteCount { get; private set; }
    public int BlockWriteCount { get; private set; }
    public int ReadBlockCount { get; private set; }
    public bool Disposed { get; private set; }
    public bool CanWrite => true;

    public HidDeviceInfo Info => new()
    {
        Path = @"\\.\fakerapoo",
        VendorId = 0x24AE,
        ProductId = 0x1205,
        UsagePage = 0xFF00,
        Usage = 0x000E,
        InputReportLength = 32,
        OutputReportLength = 32,
    };

    public void SetDpiTable(int[] stages, int activeStage)
    {
        for (int i = 0; i < 7; i++)
        {
            int value = i < stages.Length ? stages[i] : 0;
            XTable[i * 2] = (byte)(value & 0xFF);
            XTable[i * 2 + 1] = (byte)(value >> 8);
            YTable[i * 2] = (byte)(value & 0xFF);
            YTable[i * 2 + 1] = (byte)(value >> 8);
        }

        XTable[14] = StageCountByte;
        YTable[14] = StageCountByte;
        CurTable[0] = (byte)activeStage;
    }

    public int GetDpi(int stage) => XTable[stage * 2] | (XTable[stage * 2 + 1] << 8);

    public bool TryWriteOutputReport(byte[] frame, out int win32Error)
    {
        WriteCount++;
        byte cmd = frame[2];
        byte length = frame[3];
        uint address = (uint)(frame[4] | (frame[5] << 8) | (frame[6] << 16) | (frame[7] << 24));

        // dongle 时序：先回 busy，再回应答
        var busy = new byte[32];
        busy[0] = RapooVt9Pro.ReportId;
        busy[1] = 0x02;
        _pending.Enqueue(busy);

        var response = new byte[32];
        response[0] = RapooVt9Pro.ReportId;
        response[1] = RapooVt9Pro.AckOk;

        switch (cmd)
        {
            case RapooVt9Pro.CmdBattery:
                response[2] = 0x01;   // 放电
                response[3] = 77;     // 77%
                break;

            case RapooVt9Pro.CmdReadBlock:
                ReadBlockCount++;
                bool empty = ReadBlockCount <= EmptyBlockReadsFirst;
                byte[] source = empty
                    ? new byte[length]
                    : address switch
                    {
                        RapooVt9Pro.AddrDpiX => XTable,
                        RapooVt9Pro.AddrDpiY => YTable,
                        RapooVt9Pro.AddrDpiCur => CurTable,
                        _ => new byte[length],
                    };
                for (int i = 0; i < length && i < source.Length; i++) response[5 + i] = source[i];
                break;

            case RapooVt9Pro.CmdWriteBlock:
                BlockWriteCount++;
                if (!IgnoreWrites)
                {
                    byte[]? target = address switch
                    {
                        RapooVt9Pro.AddrDpiX => XTable,
                        RapooVt9Pro.AddrDpiY => YTable,
                        RapooVt9Pro.AddrDpiCur => CurTable,
                        _ => null,
                    };
                    if (target is not null)
                    {
                        for (int i = 0; i < length && i < target.Length; i++) target[i] = frame[8 + i];
                    }
                }
                break;
        }

        _pending.Enqueue(response);
        win32Error = 0;
        return true;
    }

    public bool TryGetInputReport(byte[] buffer, out int bytesRead, out int win32Error)
    {
        bytesRead = 0;
        if (_pending.Count == 0)
        {
            win32Error = 6;
            return false;
        }

        byte[] report = _pending.Dequeue();
        Array.Clear(buffer);
        int copy = Math.Min(report.Length, buffer.Length);
        Array.Copy(report, buffer, copy);
        bytesRead = copy;
        win32Error = 0;
        return true;
    }

    public bool TryGetFeature(byte[] buffer, out int win32Error)
    {
        win32Error = 1;
        return false;
    }

    public bool TrySetFeature(byte[] buffer, out int win32Error)
    {
        win32Error = 1;
        return false;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>假 HID 通道：按队列返回预设应答，同时记录发了什么。feature / output / input 三种都支持。</summary>
internal sealed class FakeTransport : IHidTransport
{
    private readonly Queue<byte[]> _responses = new();
    private readonly Queue<byte[]> _inputReports = new();
    private readonly int _featureLength;

    public FakeTransport(int featureLength, params byte[][] responses)
    {
        _featureLength = featureLength;
        foreach (byte[] response in responses) _responses.Enqueue(response);
    }

    public string PathValue { get; init; } = @"\\.\fake";
    public ushort Vid { get; init; } = 0x1532;
    public ushort Pid { get; init; } = 0x007B;
    public ushort UsagePage { get; init; } = 0x0001;
    public ushort Usage { get; init; } = 0x0002;
    public ushort InLength { get; init; } = 9;
    public ushort OutLength { get; init; }

    public HidDeviceInfo Info => new()
    {
        Path = PathValue,
        VendorId = Vid,
        ProductId = Pid,
        UsagePage = UsagePage,
        Usage = Usage,
        FeatureReportLength = (ushort)_featureLength,
        InputReportLength = InLength,
        OutputReportLength = OutLength,
    };

    public int SetCount { get; private set; }
    public int GetCount { get; private set; }
    public int WriteCount { get; private set; }
    public int InputCount { get; private set; }
    public byte[]? LastRequest { get; private set; }
    public byte[]? LastOutput { get; private set; }
    public int FailGetWith { get; init; }
    public int FailSetWith { get; init; }
    public bool FailInput { get; set; }
    public bool Disposed { get; private set; }
    public bool ReadOnly { get; init; }
    public bool CanWrite => !ReadOnly;

    public void QueueInput(params byte[][] reports)
    {
        foreach (byte[] report in reports) _inputReports.Enqueue(report);
    }

    public bool TrySetFeature(byte[] buffer, out int win32Error)
    {
        SetCount++;
        LastRequest = (byte[])buffer.Clone();
        if (FailSetWith != 0)
        {
            win32Error = FailSetWith;
            return false;
        }

        win32Error = 0;
        return true;
    }

    public bool TryGetFeature(byte[] buffer, out int win32Error)
    {
        GetCount++;
        if (FailGetWith != 0)
        {
            win32Error = FailGetWith;
            return false;
        }

        if (_responses.Count == 0)
        {
            win32Error = 6;
            return false;
        }

        byte[] response = _responses.Dequeue();
        Array.Clear(buffer);
        Array.Copy(response, buffer, Math.Min(response.Length, buffer.Length));
        win32Error = 0;
        return true;
    }

    public bool TryWriteOutputReport(byte[] buffer, out int win32Error)
    {
        WriteCount++;
        LastOutput = (byte[])buffer.Clone();
        if (ReadOnly)
        {
            win32Error = 5;
            return false;
        }

        win32Error = 0;
        return true;
    }

    public bool TryGetInputReport(byte[] buffer, out int bytesRead, out int win32Error)
    {
        InputCount++;
        bytesRead = 0;
        if (FailInput || _inputReports.Count == 0)
        {
            win32Error = 6;
            return false;
        }

        byte[] report = _inputReports.Dequeue();
        Array.Clear(buffer);
        int copy = Math.Min(report.Length, buffer.Length);
        Array.Copy(report, buffer, copy);
        bytesRead = copy;
        win32Error = 0;
        return true;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// 假 HECATE 通道：按 2026-10-26 抓包确认的格式（`04 序号 00 命令 长度 偏移 00 00 + 数据`）
/// 实现一问一答，用来在 Linux 上验证主动查询与分块读页。
/// </summary>
internal sealed class FakeHecateMouse : IHidTransport, IInterruptReader
{
    private readonly Queue<byte[]> _pending = new();
    private readonly Queue<byte[]> _pushed = new();
    private readonly List<string> _chunks = new();

    public bool Writable { get; init; } = true;
    public byte BatteryPercent { get; init; } = 91;
    public bool Charging { get; init; }
    public byte Stage { get; init; } = 5;

    public Dictionary<byte, byte[]> Pages { get; } = new();
    public int OutputCount { get; private set; }
    public int InterruptReadCount { get; private set; }
    public int HeartbeatCount { get; private set; }
    public int SetStageCount { get; private set; }
    public List<string> WriteChunks { get; } = new();
    public List<byte> Commands { get; } = new();

    /// <summary>接下来几次读设置页只回"帧头 + 全 0 数据"（模拟设备没进配置会话）。</summary>
    public int ZeroSettingsPageReplies { get; set; }

    /// <summary>写完最后一页后把当前档位下标改回 0（模拟设备自己挪档，真机踩过）。</summary>
    public bool MoveStageAfterWrite { get; set; }
    public string LastChunks => string.Join(',', _chunks);
    public bool CanWrite => Writable;

    /// <summary>清空分块记录（会话初始化自己也会读页，断言前要先清）。</summary>
    public void ResetChunks() => _chunks.Clear();

    /// <summary>模拟 Windows 上那条 overlapped ReadFile 路径（真实设备只有这条能取到报文）。</summary>
    public bool TryReadInputReport(byte[] buffer, int timeoutMs, out int bytesRead, out int win32Error)
    {
        InterruptReadCount++;
        return TryGetInputReport(buffer, out bytesRead, out win32Error);
    }

    public FakeHecateMouse()
    {
        // 真机页面原样（2026-10-26 20:17 diag，当前档位下标 4）：
        // 7 档 = 400/800/1000/1200/1300/1600/2500，offset 0x10 + 9×k，DPI=(raw+1)×50
        byte[] real =
        {
            0x00, 0x00, 0x04, 0x02, 0x00, 0x01, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x03, 0x04, 0x00, 0x01, 0x00,
            0x07, 0x00, 0x40, 0x06, 0xFF, 0x00, 0x00, 0x01,
            0x00, 0x0F, 0x00, 0x80, 0x0C, 0xFE, 0x65, 0x23, 0x01, 0x00, 0x13, 0x00, 0x88, 0x13, 0xFC, 0xE5,
            0x14, 0x01, 0x00, 0x17, 0x00, 0x64, 0x19, 0x00,
            0xC4, 0x00, 0x01, 0x00, 0x19, 0x00, 0x90, 0x01, 0x00, 0xCE, 0x7F, 0x01, 0x00, 0x1F, 0x00, 0x90,
            0x01, 0x09, 0x00, 0x87, 0x01, 0x00, 0x31, 0x00,
            0x90, 0x01, 0x21, 0x01, 0x43, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x01,
        };

        var settings = new byte[EdifierHecate.SettingsPageLength];
        real.CopyTo(settings, 0);
        settings[12] = Stage;
        Pages[EdifierHecate.PageSettings] = settings;
        Pages[EdifierHecate.PageDevice] = new byte[EdifierHecate.DevicePageLength];
        Pages[EdifierHecate.PageProfile] = new byte[EdifierHecate.ProfilePageLength];
    }

    public HidDeviceInfo Info => new()
    {
        Path = @"\\.\fakehecate",
        VendorId = 0x320F,
        ProductId = 0x706E,
        UsagePage = 0xFF1C,
        Usage = 0x0092,
        InputReportLength = 64,
        OutputReportLength = 64,
    };

    /// <summary>让设备先"推"几帧（模拟被动状态帧/心跳）。</summary>
    public void QueueInput(params byte[][] reports)
    {
        foreach (byte[] report in reports) _pushed.Enqueue(report);
    }

    public bool TryWriteOutputReport(byte[] frame, out int win32Error)
    {
        win32Error = 0;
        if (!Writable)
        {
            win32Error = 5;
            return false;
        }

        if (frame.Length != 64)
        {
            win32Error = 87;
            return false;
        }

        OutputCount++;
        var reply = new byte[64];
        Array.Copy(frame, reply, 8);   // 设备把请求头原样回显

        byte command = frame[3];
        byte length = frame[4];
        byte offset = frame[5];
        Commands.Add(command);

        switch (command)
        {
            case EdifierHecate.CmdHeartbeat:
                HeartbeatCount++;
                reply[8] = 0xFF;   // 真机抓包里握手应答的 [8] 就是 0xFF
                break;

            case EdifierHecate.CmdBattery:
                reply[8] = BatteryPercent;
                reply[9] = (byte)(Charging ? 1 : 0);
                break;

            case EdifierHecate.CmdSetStage:
                SetStageCount++;          // 正常流程永远不该走到这里（那是"设档位"）
                reply[8] = Stage;
                break;

            case EdifierHecate.CmdWritePage:
                // 写页：数据从 [8] 起，长度 [4]、偏移 [5]，写的是设置页（官方 App 的做法）
                WriteChunks.Add($"{length}@{offset}");
                byte[] settingsPage = Pages[EdifierHecate.PageSettings];
                for (int i = 0; i < length && offset + i < settingsPage.Length; i++)
                {
                    settingsPage[offset + i] = frame[8 + i];
                    reply[8 + i] = frame[8 + i];   // 真机也是原样回显数据
                }

                if (MoveStageAfterWrite)
                {
                    settingsPage[EdifierHecate.SettingsStageIndexOffset] = 0;
                }
                break;

            default:
                if (Pages.TryGetValue(command, out byte[]? page))
                {
                    if (command == EdifierHecate.PageSettings && ZeroSettingsPageReplies > 0)
                    {
                        ZeroSettingsPageReplies--;   // 只回帧头、不填数据 = 全 0
                        _chunks.Add($"{length}@{offset:X}");
                        break;
                    }

                    for (int i = 0; i < length && offset + i < page.Length; i++)
                    {
                        reply[8 + i] = page[offset + i];
                    }

                    _chunks.Add($"{length}@{offset:X}");
                }
                break;
        }

        _pending.Enqueue(reply);
        return true;
    }

    public bool TryGetInputReport(byte[] buffer, out int bytesRead, out int win32Error)
    {
        bytesRead = 0;
        win32Error = 0;

        Queue<byte[]> source = _pending.Count > 0 ? _pending : _pushed;
        if (source.Count == 0)
        {
            win32Error = 6;
            return false;
        }

        byte[] report = source.Dequeue();
        Array.Clear(buffer);
        int copy = Math.Min(report.Length, buffer.Length);
        Array.Copy(report, buffer, copy);
        bytesRead = copy;
        win32Error = 0;
        return true;
    }

    public bool TryGetFeature(byte[] buffer, out int win32Error)
    {
        win32Error = 1;
        return false;
    }

    public bool TrySetFeature(byte[] buffer, out int win32Error)
    {
        win32Error = 1;
        return false;
    }

    public void Dispose()
    {
    }
}
