using System.Text.Json;
using System.Text.Json.Serialization;
using RazerMini.Core.Logging;
using RazerMini.Core.Monitoring;
using RazerMini.Core.Rendering;

// 属性名 BatteryScale（配置里的字符串）会遮蔽同名枚举类型，这里起个别名区分
using BatteryScaleKind = RazerMini.Core.Devices.BatteryScale;

namespace RazerMini.Core.Settings;

public sealed class AppSettings
{
    /// <summary>连着时多久读一次电量（秒）。</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>设备不在时多久重扫一次（秒）。插回接收器后靠这个自动恢复。</summary>
    public int PollIntervalWhenDisconnectedSeconds { get; set; } = 5;

    /// <summary>
    /// 设备在但读失败时多久重试（秒）。
    /// v1.15 从 10 提到 30：无线设备"休眠"时每一轮都要付出几次 HID 往返（实测最坏 4.5 秒），
    /// 10 秒重试等于一直在忙。全部设备都读不到超过 6 轮后，托盘还会自动退到 5 分钟一轮。
    /// </summary>
    public int RetryIntervalSeconds { get; set; } = 30;

    /// <summary>是否额外查一次充电状态（多一条命令，可关）。</summary>
    public bool ReadChargingStatus { get; set; } = true;

    /// <summary>"Raw255" 或 "Direct"，见 <see cref="BatteryScale"/>。</summary>
    public string BatteryScale { get; set; } = "Raw255";

    /// <summary>右键菜单里的 DPI 预设值。</summary>
    public int[] DpiPresets { get; set; } = { 400, 800, 1600, 3200, 6400 };

    /// <summary>"Percent" 或 "Battery"。</summary>
    public string IconMode { get; set; } = "Percent";

    /// <summary>开机自启（写 HKCU Run 键）。</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>鼠标悬停提示里附带原始字节，便于核对换算口径。</summary>
    public bool ShowRawBatteryInTooltip { get; set; } = true;

    /// <summary>写详细日志（每次读取都记一行）。</summary>
    public bool VerboseLogging { get; set; }

    // ────────────────────────── 桌面浮窗 ──────────────────────────

    /// <summary>是否显示桌面浮窗（托盘菜单可切换，关掉后不再创建窗口）。</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>
    /// 浮窗是否可交互。默认 false：点击穿透 + 位置锁定（鼠标完全穿透到下面的窗口）。
    /// 只有用户在托盘菜单里手动打开时才允许拖动/缩放。
    /// </summary>
    public bool OverlayInteractive { get; set; }

    /// <summary>
    /// "acrylic"（毛玻璃，默认）或 "solid"（纯色面板）。
    /// 某些系统关闭了"透明效果"或显卡驱动把 DWM 毛玻璃搞坏时，改成 solid 可保证可读。
    /// </summary>
    public string OverlayGlass { get; set; } = "acrylic";

    /// <summary>
    /// 浮窗位置记录的版本号。v1.18.0 的旧版会把"切交互后跳到 (0,0)"的坏位置写进来，
    /// 升级后用这个版本号把坏记录一次性作废（&lt;1 一律当作没存过，回到默认右上角）。
    /// </summary>
    public int OverlayPlacementVersion { get; set; }

    /// <summary>浮窗所在显示器的设备名（如 <c>\\.\DISPLAY1</c>）。</summary>
    public string OverlayMonitor { get; set; } = "";

    // 位置/大小存成"显示器工作区内的比例"，不是绝对像素：
    // 换分辨率或改缩放后仍能落回原处，而绝对像素会跑到屏幕外面。
    public double OverlayLeft { get; set; } = -1;
    public double OverlayTop { get; set; } = -1;
    public double OverlayWidth { get; set; }
    public double OverlayHeight { get; set; }

    /// <summary>是否已经存过浮窗位置（-1/0 表示首次运行，用默认右上角）。</summary>
    [JsonIgnore]
    public bool HasOverlayPlacement =>
        OverlayWidth > 0.01 && OverlayHeight > 0.01 && OverlayLeft >= 0 && OverlayTop >= 0;


    [JsonIgnore]
    public BatteryScaleKind ParsedBatteryScale =>
        string.Equals(BatteryScale, "Direct", StringComparison.OrdinalIgnoreCase)
            ? BatteryScaleKind.Direct
            : BatteryScaleKind.Raw255;

    [JsonIgnore]
    public TrayIconMode ParsedIconMode =>
        string.Equals(IconMode, "Battery", StringComparison.OrdinalIgnoreCase)
            ? TrayIconMode.Battery
            : TrayIconMode.Percent;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public PollPolicy CreatePolicy() => new(
        connectedInterval: TimeSpan.FromSeconds(Math.Max(5, PollIntervalSeconds)),
        disconnectedInterval: TimeSpan.FromSeconds(Math.Max(1, PollIntervalWhenDisconnectedSeconds)),
        retryInterval: TimeSpan.FromSeconds(Math.Max(1, RetryIntervalSeconds)));

    /// <summary>读失败/文件损坏一律回退到默认值并记日志，绝不因为配置问题启动失败。</summary>
    public static AppSettings Load(string path, ILog? log = null)
    {
        log ??= NullLog.Instance;
        try
        {
            if (!File.Exists(path))
            {
                log.Info($"配置文件不存在，使用默认值：{path}");
                var defaults = new AppSettings();
                defaults.Save(path, log);
                return defaults;
            }

            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            if (settings is null)
            {
                log.Warn($"配置文件解析为空，使用默认值：{path}");
                return new AppSettings();
            }

            log.Info($"已加载配置：{path}");
            return settings;
        }
        catch (Exception ex)
        {
            log.Error($"读取配置失败，回退默认值：{ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(string path, ILog? log = null)
    {
        log ??= NullLog.Instance;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
            log.Debug($"配置已保存：{path}");
        }
        catch (Exception ex)
        {
            log.Error($"保存配置失败：{ex.Message}");
        }
    }
}

/// <summary>应用数据目录：%APPDATA%\RazerMini。</summary>
public static class AppPaths
{
    public const string FolderName = "RazerMini";

    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "razer-mini.log");
}
