using System.Runtime.InteropServices;
using System.Text;
using RazerMini.Core.Devices;
using RazerMini.Core.Logging;
using RazerMini.Core.Overlay;
using RazerMini.Core.Settings;
using RazerMini.Platform;
using RazerMini.Ui;

namespace RazerMini;

internal static class Program
{
    private const string MutexName = @"Local\RazerMini.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // 第一件事就落一行痕迹：这样"进程根本没起来"和"起来了但界面没出现"能区分开
        EarlyTrace(args);

        try
        {
            return RunCore(args);
        }
        catch (Exception ex)
        {
            // 托盘构造阶段的异常不会走 Application.ThreadException，必须在这里兜住
            ReportFatal(ex, "Main");
            return 1;
        }
    }

    private static int RunCore(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            ConsoleHost.Attach();
            PrintHelp();
            return 0;
        }

        if (args.Any(a => a is "--diag" or "-d"))
        {
            return RunHeadless(diagnostics: true, setDpi: null);
        }

        if (args.Any(a => a is "--once" or "-o"))
        {
            return RunHeadless(diagnostics: false, setDpi: ParseSetDpi(args));
        }

        if (ParseSetDpi(args) is int target)
        {
            return RunHeadless(diagnostics: false, setDpi: target, quiet: false);
        }

        return RunTray();
    }

    /// <summary>启动痕迹：写进日志文件，也写一份到 startup.log（日志轮转/写失败时的兜底证据）。</summary>
    private static void EarlyTrace(string[] args)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} 进程启动 pid={Environment.ProcessId} " +
                          $"exe={Environment.ProcessPath} args=[{string.Join(' ', args)}] " +
                          $"dotnet={Environment.Version} os={Environment.OSVersion} 64bit={Environment.Is64BitProcess}";
            File.AppendAllText(Path.Combine(AppPaths.Root, "startup.log"), line + Environment.NewLine);
            new FileLog(AppPaths.LogFile).Info(line);
        }
        catch
        {
            // 连 %APPDATA% 都写不了就只能放弃了
        }
    }

    private static int? ParseSetDpi(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--set-dpi" or "-s" && i + 1 < args.Length && int.TryParse(args[i + 1], out int value))
            {
                return value;
            }
        }
        return null;
    }

    private static int RunTray()
    {
        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("RazerMini 已经在运行了（看任务栏右下角托盘图标）。",
                "RazerMini", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => ReportFatal(e.Exception, "UI 线程");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception, "后台线程");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportFatal(e.Exception, "任务");
            e.SetObserved();
        };

        using var app = new TrayApp();
        Application.Run(app);
        return 0;
    }

    private static void ReportFatal(Exception? exception, string source)
    {
        string message = exception?.ToString() ?? "(无异常对象)";
        try
        {
            var log = new FileLog(AppPaths.LogFile);
            log.Error($"[{source}] 未处理异常：{message}");
        }
        catch
        {
            // 日志也失败就只能弹框了
        }

        MessageBox.Show($"发生未处理异常（已写入日志 {AppPaths.LogFile}）：\n\n{message}",
            "RazerMini", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>无界面模式：--diag / --once / --set-dpi。给"我在命令行核验一下"用的。</summary>
    private static int RunHeadless(bool diagnostics, int? setDpi, bool quiet = false)
    {
        ConsoleHost.Attach();
        var log = new FileLog(AppPaths.LogFile, echoToConsole: true);
        var settings = AppSettings.Load(AppPaths.SettingsFile, log);

        Console.WriteLine($"RazerMini {typeof(Program).Assembly.GetName().Version}");
        Console.WriteLine($"日志: {AppPaths.LogFile}");
        Console.WriteLine();

        if (diagnostics)
        {
            Console.WriteLine(Diagnostics.BuildReport(log, settings));
            return 0;
        }

        DeviceSelection selection = DeviceSelector.Select(HidEnumerator.EnumerateRazer(log), log);
        if (!selection.Found)
        {
            Console.WriteLine($"✗ 未找到雷蛇设备：{selection.Reason}");
            return 1;
        }

        using HidDevice? transport = HidDevice.TryOpen(selection.Interface!, log);
        if (transport is null)
        {
            Console.WriteLine("✗ 打开设备失败（见上方 Win32 错误码）");
            return 2;
        }

        using var mouse = new RazerMouse(transport, selection.Model!, log)
        {
            ReadChargingStatus = settings.ReadChargingStatus,
        };

        if (!mouse.TryReadBattery(settings.ParsedBatteryScale, out BatterySample sample, out string error))
        {
            Console.WriteLine($"✗ 读电量失败：{error}");
            return 3;
        }

        Console.WriteLine($"✓ 电量: {sample.Percent}%（raw {sample.RawValue} / 0x{sample.RawValue:X2}，" +
                          $"充电={sample.Charging?.ToString() ?? "未知"}，口径 {sample.Scale}）");

        if (mouse.TryReadDpi(out int dpiX, out int dpiY, out string dpiError))
        {
            Console.WriteLine($"✓ DPI: {dpiX}x{dpiY}（上限 {mouse.MaxDpi}）");
        }
        else
        {
            Console.WriteLine($"⚠ DPI 读取失败：{dpiError}");
        }

        if (setDpi is int target)
        {
            if (!mouse.TryWriteDpi(target, target, out string writeError))
            {
                Console.WriteLine($"✗ 设置 DPI 失败：{writeError}");
                return 4;
            }

            Thread.Sleep(60);
            if (mouse.TryReadDpi(out int readX, out int readY, out _))
            {
                Console.WriteLine($"✓ DPI 已设为 {readX}x{readY}（读回确认）");
            }
            else
            {
                Console.WriteLine("⚠ 写入成功但读回失败，请用雷蛇官方软件核对");
            }
        }

        if (!quiet)
        {
            Console.WriteLine();
            Console.WriteLine("提示：双击 exe 进入托盘模式；--diag 出诊断报告。");
        }

        return 0;
    }

    private static string Trim(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";

    private static void PrintHelp()
    {
        Console.WriteLine("""
            RazerMini —— 外设电量托盘 + 桌面浮窗（鼠标 / 键盘 / 手柄）

            用法:
              RazerMini.exe              托盘常驻（默认）
              RazerMini.exe --once       读一次电量和 DPI 并退出（退出码 0 = 成功）
              RazerMini.exe --set-dpi N  把 DPI 设成 N（X/Y 同步）并退出
              RazerMini.exe --diag       打印完整诊断报告（含原始字节、通道枚举）
              RazerMini.exe --help       本帮助

            配置文件: %APPDATA%\RazerMini\settings.json
            日志文件: %APPDATA%\RazerMini\razer-mini.log
            """);
    }

    /// <summary>
    /// 解析 `--probe-page &lt;页号&gt;`（十六进制，可带 0x 前缀）。
    /// 只用于"从抓包里看到某个页号后，单独读出来看内容"——只读，不会写任何东西。
    /// </summary>
}

/// <summary>WinExe 默认没有控制台；--diag/--once 时挂到父控制台，从资源管理器启动则新开一个。</summary>
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    public static void Attach()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }

        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            Console.SetOut(stdout);
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 没控制台就算了，输出丢弃
        }
    }
}
