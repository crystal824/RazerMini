# RazerMini

把**鼠标 / 键盘 / 手柄的电量**放到 Windows 托盘和一块毛玻璃浮窗里，多台设备同时显示。

- .NET 8 + WinForms，**零第三方依赖**（HID 直接 P/Invoke `hid.dll`，配置用内置 `System.Text.Json`）
- 主程序约 110 KB，常驻内存 20–40 MB
- 支持同时连接多台设备；托盘图标跟随你选中的那只
- 桌面浮窗：Win11 系统卡片观感，**默认点击穿透、不挡任何窗口**

## 支持的设备

| 品牌 / 型号 | VID:PID | 电量 | DPI |
|---|---|---|---|
| 雷蛇 Viper Ultimate（接收器 / 底座 / 有线） | `1532:007B` / `007E` / `007A` | ✅ | ✅ |
| 雷柏 VT9 Pro | `24AE:1205` | ✅ | ✅ |
| 漫步者 HECATE G3M Pro | `320F:706E` | ✅ | ✅ |
| 高斯 XS75T（`GS3104T-Pro` 同协议） | `05AC:024F` | ✅ | — |
| 任天堂 Switch Pro（蓝牙） | `057E:2009` | 5 档 | — |
| Xbox 手柄（XInput 接收器） | XInput 槽位 | 4 档 | — |

> 档位制设备（Switch Pro 5 档、Xbox 4 档）没有精确百分比，界面会同时标注 `STAGE 4/5` 之类的档位，
> 避免把折算值当成电表读数。

## 快速开始

1. 安装 **.NET 8 桌面运行时（Desktop Runtime, x64）**  
   <https://dotnet.microsoft.com/download/dotnet/8.0/runtime>  
   验证：命令行执行 `dotnet --list-runtimes`，输出里应有 `Microsoft.WindowsDesktop.App 8.0.x`。
2. 解压发布包，双击 `RazerMini.exe`。没有控制台窗口，图标进托盘（Win11 默认收在 `^` 溢出区）。
3. 右键托盘图标开始用。

> 不要把 `RazerMini.exe` 改名：启动器里烘死了 `RazerMini.dll` 这个名字，改名后双击无反应。

## 托盘与浮窗

托盘右键菜单：

- **设备行**（每台设备一行）· 点击切换图标显示哪只
- **DPI**（支持时）· 快捷档位 / 自定义
- **桌面浮窗** · 显示或隐藏（默认显示，落在主屏右上角、离边 32px）
- **浮窗可交互（移动 / 缩放）** · 默认**不勾**：鼠标点击、滚轮完全穿透到下面的窗口，位置锁定；
  勾上后拖动面板空白处移动、拖四边/四角改大小（界面上没有任何按钮），再点一次恢复穿透
- **立即刷新** / **开机启动** / **诊断信息…** / **打开日志文件** / **退出**

浮窗是一个**桌面挂件**：不置顶、平时沉在窗口层最底，别的窗口会盖住它；只有打开交互时才临时提到前面方便拖动。
每台设备一行（环形电量 + 名称 + 状态）；读不到时显示 `--` + `NO RESP`，不会把"没应答"画成 0%。

设备插拔即时生效：鼠标键盘走系统插拔通知，Xbox 手柄走 1.5 秒一次的 XInput 哨兵，通常 1–2 秒内出现在列表里。

## 命令行

```
RazerMini.exe              托盘常驻（默认）
RazerMini.exe --once       读一次电量/DPI 并退出（退出码 0 = 成功）
RazerMini.exe --set-dpi N  把 DPI 设为 N（X/Y 同步）并退出
RazerMini.exe --diag       输出诊断报告（接口枚举 + 每台设备实测 + 配置）
RazerMini.exe --help       帮助
```

排障入口：双击 `diag.cmd`（等价 `--diag`），把输出贴回来即可。

## 配置文件

`%APPDATA%\RazerMini\settings.json`（首次运行自动生成）：

| 字段 | 默认 | 说明 |
|---|---|---|
| `pollIntervalSeconds` | 60 | 设备在线时的轮询间隔 |
| `retryIntervalSeconds` | 30 | 读失败后的重试间隔 |
| `batteryScale` | `Raw255` | 雷蛇口径；个别固件直接报百分比时改 `Direct` |
| `iconMode` | `Percent` | 托盘图标显示数字或电池图形 |
| `dpiPresets` | `[400,800,1600,3200,6400]` | 右键 DPI 档位 |
| `verboseLogging` | `false` | 打开后每轮读取都记一行（排障用） |
| `overlayEnabled` | `true` | 是否显示桌面浮窗 |
| `overlayInteractive` | `false` | 浮窗是否可交互（等同托盘的交互开关） |
| `overlayGlass` | `acrylic` | `solid` = 纯色面板（不要毛玻璃时） |
| `overlayMonitor` + `overlayLeft/Top/Width/Height` | — | 浮窗位置，按显示器工作区比例保存，换分辨率不会跑偏 |

## 排障

- **双击没反应**：先看 `%APPDATA%\RazerMini\startup.log` 有没有本次启动的时间戳。  
  没有 = 进程没进 .NET（多半缺 .NET 8 桌面运行时或文件不全）；有 = 日志末尾就是原因。
- **设备不出现**：`diag.cmd` 里能看到全部 HID 接口和每个候选的打开/读取结果（含 Win32 错误码）。
  常见原因：接收器没插好、鼠标休眠、被厂商软件（Synapse 等）独占句柄。
- **电量显示 `--` / `NO RESP`**：设备在、但这一轮没应答（无线设备休眠）。下一轮会自动恢复，
  并保留上次读数标 `STALE`。
- **毛玻璃不像系统卡片**：浮窗的玻璃是程序自己"截屏 + 模糊 + 叠色"算的，
  不依赖系统"透明效果"开关；想退回纯色面板把 `overlayGlass` 改成 `solid`。
- 日志：`%APPDATA%\RazerMini\razer-mini.log`（512 KB 轮转一份 `.1`），默认只记状态变化与错误。

## 从源码构建

Windows（需要 .NET 8 SDK）：

```powershell
build\publish-win.cmd
```

Linux / NAS 上交叉编译（不需要 Windows；Roslyn 直编 + apphost 修补）：

```bash
DOTNET_DIR=/path/to/dotnet OFF=/path/to/nuget-offline bash build/build-linux.sh
```

产物在 `dist/RazerMini/`。脚本自带断言（`tests/Verify`，587 条）与交付前自检
（PE 子系统、apphost 占位符、deps.json、版本号），任一项不通过就 `exit 1`。

## 代码结构

```
src/RazerMini.Core/   平台无关：各设备协议、电量换算、日志、浮窗几何与配色（纯函数，可在 Linux 上断言）
src/RazerMini/        Windows 层：HID 枚举与句柄、托盘、浮窗绘制、诊断、自绘毛玻璃
tests/Verify/         断言程序（协议帧字节/CRC/解析/轮询策略/浮窗布局）
build/                构建脚本与交付自检
docs/PROTOCOLS.md     协议与实现细节（改代码前必读）
```

## 已知限制

- Windows 10 1809+ / Windows 11，需要 .NET 8 桌面运行时。
- Xbox 手柄拿不到精确电量：微软私有 GIP 由 `xboxgip.sys` 独占，用户态只能通过 XInput 读 4 档；
  有线直连时报 WIRED（没有电池）。这是系统限制。
- 高斯键盘查询命中率约 50%（休眠周期），程序会重试并在下一轮补上。
- 桌面浮窗的毛玻璃是"截屏那一刻"的桌面；它沉在窗口最底层（背后就是壁纸），平时不会显得过时。
- 封版后已移除逆向期调试入口（`--pads` / `--usb` / `--probe-page` / `--overlay-preview`）。

## 许可

个人项目，按现状提供（AS IS）。
