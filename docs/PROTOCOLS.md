# 协议与实现细节

> 这是开发笔记，不是使用说明。使用请看 [README](../README.md)。
> 文中提到的 `--usb` / `--pads` / `--probe-page` / `--overlay-preview` 等调试入口在 1.19.0 封版时已移除，
> 保留记录只为说明结论是怎么来的。

雷蛇 Viper Ultimate 的 Windows 托盘小工具：**托盘显示电量** + **右键菜单调 DPI**。

- .NET 8 + WinForms，**零 NuGet 依赖**（HID 直接 P/Invoke `hid.dll`，配置用内置 `System.Text.Json`）
- 单 exe ~150KB，常驻内存 20–40MB
- 可同时显示**多只**鼠标的电量，图标跟随你选中的那只

### 支持的设备

| 品牌 | 型号 | VID:PID | 电量 | DPI |
|---|---|---|---|---|
| 雷蛇 | Viper Ultimate（无线接收器 / 充电底座 / 有线） | `1532:007B` / `007E` / `007A` | ✓ | ✓ 读+写 |
| 雷柏 | VT9 Pro（一代 0xBA 协议） | `24AE:1205` | ✓ | ✓ 读+写（当前档位，EEPROM，已真机验证） |
| 漫步者 | HECATE G3M Pro | `320F:706E` | ✓ 主动查询（interrupt 读，已真机验证 91%） | ✓ 读+写（7 档表，DPI=(raw+1)×50，见 §8.1） |
| 高斯 | **XS75T**（键盘；GS3104T-Pro 同 PID 无法区分） | `05AC:024F` | ✓ 主动查询（33/33 通道，真机 100%） | — 不支持 |
| 任天堂 | **Switch Pro 手柄**（蓝牙） | `057E:2009` | ✓ 5 档（空/低/中/高/满），切 0x30 报告模式后读 | — 不支持 |
| 微软 | **Xbox 手柄**（走专用接收器；有线直连无电池） | 由 XInput 槽位识别（接收器多为 `045E:02E6`/`02FE`） | ✓ **4 档**（空/低/中/满），XInput API | — 不支持 |

> **无线设备休眠是常态**：接收器/适配器在线 ≠ 设备在线。鼠标/键盘睡着时读不到真实电量，
> 程序会显示**上次读数 + （休眠）**，绝不把"没应答"当成 0%（v1.14.0 起；之前雷柏会显示假 0%）。

---

## 一、快速开始

**前提**：Windows 10/11 x64 + [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)（Desktop Runtime，不是 ASP.NET）。

先确认有没有：

```cmd
dotnet --list-runtimes
```

输出里要有 `Microsoft.WindowsDesktop.App 8.0.x`。没有就装上面那个运行时（约 55MB）。

**然后**：

1. 把 `dist/RazerMini/` 整个文件夹拷到 Windows（比如 `C:\Tools\RazerMini\`）
2. **双击 `RazerMini.exe`** —— 无控制台窗口，图标直接进托盘
3. 右键托盘图标出菜单

> ⚠️ **不要改 `RazerMini.exe` 的文件名**：apphost 在构建时就把 `RazerMini.dll` 这个名字烘进去了，
> 改名后双击会毫无反应（不报错）。要换名字得改 `AssemblyName` 重新编译。
>
> 想确认程序能不能起来、或看原始字节，用 `diag.cmd`（等价于命令行 `RazerMini.exe --diag`）。

删掉就是卸载。配置文件在 `%APPDATA%\RazerMini\`，不想要开机自启就在菜单里取消勾选。

---

## 二、托盘怎么用

图标含义：

| 显示 | 含义 |
|---|---|
| 绿色数字（如 `74`） | 电量 ≥ 50% |
| 琥珀色数字 | 20%–49% |
| 红色数字 | < 20% |
| 青色数字 | 正在充电 |
| 灰色 `--` | 设备未连接（拔了 / 接收器没插），或设备在枚举里但还没读到过电量（休眠中） |

> 设备休眠时菜单行会带 `（休眠·上次）`，图标仍显示**上次读到的真实电量**——不会跳成 0%。

右上角鼠标悬停可以看到完整信息：设备名 + 电量 + 充电状态 + 当前 DPI。

右键菜单：

```
已连接 3 台设备（点一行切换图标）
─────────────────────
  雷蛇 Viper Ultimate（无线接收器）  94%
  雷柏 VT9 Pro                      78%
  漫步者 HECATE G3M Pro              67%
─────────────────────
电量：94% · 放电中（原始 239，Raw255）
DPI：3200
调整 DPI  ▸  400 / 800 / 1600 / 3200 / 6400 / 自定义…
─────────────────────
立即刷新
开机启动
诊断信息…
打开日志文件
退出
```

- **设备行**：`勾选 = 当前图标显示的那只`，点一下切换；行尾带该设备的实时电量；悬浮可看设备接口路径
- **调整 DPI**：点一下生效（X/Y 同步），当前档位前有圆点，写完会读回设备确认
- **DPI 显示是实时刷新的**：每轮轮询（默认 60 秒）都会重读设备当前 DPI，所以你**按鼠标上的 DPI 键换档后，托盘会在一个轮询周期内跟着变**；想立刻看到就双击托盘图标或点「立即刷新」
- **自定义…**：输入 100–26000 之间的任意值（按设备的 `MaxDpi` 夹取）
- **开机启动**：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，不需要管理员权限
- **诊断信息…**：出问题先点这个，报告可以直接复制发我

双击托盘图标 = 立即刷新一次电量。

**插拔与休眠**：程序每 3 个轮询周期重扫一次接口，所以启动时没插、或在休眠里的鼠标，之后
唤醒/插上也能自动补进列表；单台设备连续 3 次读不到会被剔除并等下次重扫重连（拔了再插不用重启）。
想立刻生效就点「立即刷新」—— 它会顺带重扫一遍。

---

## 三、命令行

```cmd
RazerMini.exe              托盘常驻（默认）
RazerMini.exe --once       读一次电量和 DPI 后退出，退出码 0 = 成功
RazerMini.exe --set-dpi 800 把 DPI 设成 800 后退出（可以写脚本里）
RazerMini.exe --diag       打印完整诊断报告（接口枚举 + 原始字节 + 手柄）
RazerMini.exe --usb        列出 USB 总线设备（Xbox 接收器这类非 HID 设备只在这里可见）
RazerMini.exe --help       帮助
```

`--diag` 会同时写日志。想排查问题就把 `%APPDATA%\RazerMini\razer-mini.log` 一起发我。

---

## 四、配置文件

路径：`%APPDATA%\RazerMini\settings.json`（首次运行自动生成，改完重启程序生效）

```json
{
  "pollIntervalSeconds": 60,
  "pollIntervalWhenDisconnectedSeconds": 5,
  "retryIntervalSeconds": 10,
  "readChargingStatus": true,
  "batteryScale": "Raw255",
  "dpiPresets": [400, 800, 1600, 3200, 6400],
  "iconMode": "Percent",
  "startWithWindows": false,
  "showRawBatteryInTooltip": true,
  "verboseLogging": true
}
```

| 字段 | 说明 |
|---|---|
| `pollIntervalSeconds` | 连着时多久读一次电量（默认 60 秒，最小 5） |
| `pollIntervalWhenDisconnectedSeconds` | 设备不在时多久重扫一次，决定了插回接收器后多久恢复（默认 5 秒） |
| `retryIntervalSeconds` | 设备在但读失败时的重试间隔 |
| `readChargingStatus` | 是否额外查充电状态（多一条 HID 命令，关掉能少一半流量） |
| `batteryScale` | 电量换算口径：`Raw255`（0–255 → ×100/255，默认）或 `Direct`（原始值就是百分比）。**读数不对先改这个** |
| `dpiPresets` | 右键菜单里的 DPI 档位 |
| `iconMode` | `Percent` = 图标直接显示数字；`Battery` = 显示电池形状 |
| `startWithWindows` | 仅记录状态，实际以注册表为准（菜单勾选会同步写） |
| `showRawBatteryInTooltip` | 悬停提示里附带原始字节，便于核对换算 |
| `verboseLogging` | 是否写每次读取的调试日志 |

---

## 五、读数不对怎么办

### 5.0 双击完全没反应、也没报错

GUI 程序出错时不会弹控制台，所以先分清是**宿主层面**没起来，还是**程序起来了但图标藏起来了**。

**第 1 步：看任务管理器**有没有 `RazerMini.exe` 进程，以及**点任务栏右下角的 `^` 溢出区**——
Windows 11 默认把新托盘图标收进溢出区，图标可能就在那里（拖到任务栏上即可常驻显示）。

**第 2 步：双击 `diag.cmd`**（或在命令行跑 `RazerMini.exe --diag`）。
它会打印诊断报告；如果程序根本起不来，命令行的报错也会留在窗口里。

**第 3 步：看日志痕迹**。这个文件里有本次启动的时间戳，说明托管代码至少起来了：

```
%APPDATA%\RazerMini\startup.log
%APPDATA%\RazerMini\razer-mini.log
```

- `startup.log` **没有**这次启动的记录 → 进程在进入 .NET 之前就失败了，基本就是运行时缺失或文件不全（对照下面的"文件清单"）
- `startup.log` **有**记录但没托盘 → 日志末尾会写具体原因，把它发我

**第 4 步：命令行跑诊断**（能看到最详细的输出）：

```cmd
cd /d C:\Tools\RazerMini
RazerMini.exe --diag
```

**必需文件清单**（少一个都可能静默失败）：

```
RazerMini.exe                    启动器（apphost）
RazerMini.dll                    主程序
RazerMini.Core.dll               协议层
RazerMini.runtimeconfig.json     运行时配置
RazerMini.deps.json              依赖清单 ← 少了会"双击毫无反应"
```

### 5.1 电量百分比和雷蛇官方软件差很多

```
RazerMini.exe --diag
```

报告里会同时给出两种口径的结果，例如：

```
Raw255  : raw=189 (0xBD) → 74% 充电=放电中 args=BD ...
Direct  : raw=189 (0xBD) → 100% 充电=放电中 args=BD ...
判定提示：
  若 raw=189，按 0-255 换算 = 74%，直接当百分比 = 100%。
  拿雷蛇官方软件显示的电量对照，哪个吻合就把 batteryScale 设成哪一个。
```

对照官方软件的电量，把 `settings.json` 里的 `batteryScale` 改成吻合的那个，重启程序。

**显示灰色 `--`** → 设备没被枚举到。依次检查：

1. 接收器插了吗？换个 USB 口试试
2. 鼠标是不是休眠了？动一下鼠标
3. 雷蛇 Synapse 是不是开着并占用了设备？关掉 Synapse 或退出其后台再试
4. `RazerMini.exe --diag` 看报告里有没有 `1532:007B` / `1532:007E`，以及"选中通道"是哪一行

**DPI 点了没反应** → 看日志里的 `写 DPI 失败` 和 Win32 错误码。常见：`5 拒绝访问`（被 Synapse 占用，需要关掉它）、`6 无效句柄`（设备已拔出）。

### 5.2 菜单里出现「（休眠·上次）」或「（未应答）」

这是 v1.14 起的**正常状态显示**，不是故障。

**它只在"连续约 3 分钟（≈3 轮轮询）都读不到"时出现** —— 偶尔漏一轮不标，否则无线键盘
每分钟闪一次括号，纯噪音。设备读到一次就立刻恢复成正常显示。含义：

```
雷柏 VT9 Pro · 92%（休眠·上次）   ← 显示的是上次读到的真实电量，这一轮设备没应答
雷柏 VT9 Pro · --（未应答）        ← 还没读到过（程序刚启动，设备在睡）
```

为什么会这样：无线鼠标/键盘闲置几秒到几分钟就会休眠，**收发器还在 USB 上、设备本身不应答**。
协议层面看到的现象（2026-10-27 真机日志）：

| 设备 | 休眠时的应答 | 说明 |
|---|---|---|
| 雷蛇 007B 接收器 | `0x04 设备无应答` | 接收器在，鼠标不在线 |
| 雷柏 24AE:1205 | `status=0x01 charge=0x70 percent=0`，EEPROM 三块全 `00` | 接收器 ACK 了一个"空数据"应答 |
| 高斯键盘 | 整个 750ms 窗口没有报文 | 键盘休眠，按一下键就回来 |

**处理**：动一下鼠标/按一下键盘，再点托盘菜单里的「立即刷新」。程序每 60 秒本来也会自己重试，
设备醒过来会自动恢复，不需要重启。

**绝对不要做的事**：把这种状态当"电量 0%"——v1.13 及以前雷柏就是这么错的（`charge=0x70` 被当成
合法电量），现在协议层有内容闸门：充电态必须是 1（放电）或 2（充电）、百分比必须 ≤ 100，
否则一律判"应答不可信"并重试，绝不把垃圾数据交给界面。

---

## 六、从源码构建

### Windows（推荐，需要 .NET 8 SDK）

```cmd
build\publish-win.cmd
```

产物在 `dist-win\RazerMini.exe`，**自包含单文件**（约 70MB，目标机器不需要装运行时）。

或者普通构建：

```cmd
dotnet build src\RazerMini\RazerMini.csproj -c Release
```

### fnOS / Linux NAS（本项目的实际开发方式）

```bash
./build/build-linux.sh          # 编 Core + 跑 419 条协议断言 + 编 WinForms + 产物自检
./build/build-linux.sh --no-verify
```

产物在 `dist/RazerMini/`。这套流程靠 `csc.dll` 直编（绕开 NuGet restore 不可用的问题），
需要离线引用包 `Microsoft.WindowsDesktop.App.Ref 8.0.32`（技能 `dotnet-build-on-linux-nas` 有全文）。

**能在 Linux 上验证什么**：协议帧字节、CRC、应答解析、重试/错误分支、通道选择、轮询策略、配置读写、托盘图标像素。
**不能在 Linux 上验证什么**：HID API 调用本身、托盘、菜单、图标句柄——这些只能在 Windows 上跑。

---

## 七、代码结构

```
src/RazerMini.Core/          纯 .NET 8 类库，可在 Linux 上编译与断言
├─ Devices/
│  ├─ RazerReport.cs         91 字节 feature report 帧构造/解析/CRC
│  ├─ RazerCommands.cs       电量、充电、读 DPI、写 DPI 四条命令
│  ├─ RazerMouse.cs          雷蛇驱动：交换、重试、错配检测、busy 兜底
│  ├─ RazerCatalog.cs        VID:PID → 型号/事务号/最大 DPI/优先级
│  ├─ DeviceSelector.cs      雷蛇专用通道挑选（旧接口，保留兼容）
│  ├─ DeviceCatalog.cs       四族统一的候选排序 + VID 白名单（雷蛇→雷柏→漫步者→高斯）
│  ├─ RapooVt9Pro.cs         雷柏一代 0xBA 协议（写 output + 轮询 input，busy→OK 才算数）
│  ├─ EdifierHecate.cs       漫步者：常量/属性/电量（主动 + 被动兜底）
│  ├─ EdifierHecate.Frames.cs   └ 帧构造、校验和、交换、会话初始化
│  ├─ EdifierHecate.Pages.cs    └ 配置页按 24 字节分块读
│  ├─ EdifierHecate.Dpi.cs      └ 7 档 DPI 表读写与校验
│  ├─ GanssKeyboard.cs       高斯 XS75T 键盘（33 字节查询 `20 01`，只读电量）
│  ├─ NintendoSwitchPro.cs   任天堂 Switch Pro（蓝牙，子命令切 0x30 全量报告 + 5 档电量）
│  ├─ XboxBattery.cs         Xbox 4 档电量的换算与文案（纯逻辑，无 P/Invoke）
│  └─ ReadOutcome.cs         读失败的语义枚举（Unsupported/NotWritable/HandleDead/休眠…）
├─ Hid/                      HidDeviceInfo、IHidTransport（feature / output / input 三通道抽象）
├─ Monitoring/PollPolicy.cs  自适应轮询节奏（60s / 5s / 30s；全睡 6 轮后退到 5 分钟）
├─ Rendering/TrayImage.cs   3x5 像素字体 + 电池图形（纯像素运算）
├─ Settings/AppSettings.cs   配置
└─ Logging/Logging.cs        文件日志（512KB 轮转）

src/RazerMini/               WinForms 层（net8.0-windows）
├─ Program.cs                Main / --diag / --once / --set-dpi
├─ TrayApp.cs                NotifyIcon + 多设备菜单 + 轮询调度
├─ Interop/HidNative.cs      hid.dll / setupapi.dll / kernel32.dll P/Invoke
├─ Platform/                 枚举、通道实现、多族连接器、图标落图、UI 派发
│  ├─ XInputController.cs    Xbox 手柄：XInputGetBatteryInformation + 槽位发现
│  └─ UsbEnumerator.cs       USB 总线枚举（`--usb`，看 Xbox 接收器这类非 HID 设备）
└─ Ui/                       诊断窗口、自定义 DPI 对话框

tests/Verify/Verify.cs       205 条断言，Linux 上直接跑
build/build-linux.sh         NAS 构建 + 断言 + 自检
build/publish-win.cmd        Windows 自包含单文件发布
```

---

## 八、已知限制

1. **只认上表那几台设备**（各自的 VID:PID + usage + 长度）。加新设备要按品牌补判据与帧格式——不同品牌协议完全不通，甚至同品牌不同代际都不兼容。
2. **雷柏 DPI 只改"当前档位"**：值存在 EEPROM 的两个 16 字节块里（`0x888` X 表 / `0x8C8` Y 表，都是
   7 × u16 小端；`[14]`/`[15]` 恒为 0，**不是档数**）。程序先读回来确认「当前档位的值像 DPI」才动手，
   写完读回逐字节校验，不一致会尝试写回原值。其它档位、灯效、回报率一律不碰。
   真机实测表：`3200 / 1600 / 800 / 1200 / 6400 / 26000 / 400`。
3. **漫步者要用 interrupt 读**（2026-10-26 抓包 + 真机确认）：`HidD_GetInputReport` 在它的厂商集合上
   恒回 **Win32=31**（驱动不填"最后一条报告"缓存），报文只能靠挂起的 interrupt IN 读拿到 ——
   程序为此另开一个 `FILE_FLAG_OVERLAPPED` 只读句柄走 `ReadFile`。DPI 字段还没定位（见 §8.1）。
4. **DPI 的 X/Y 始终保持相同值**（菜单只有单个数值）。要分离 X/Y 得改调用处。
5. **100% 时托盘数字会变小**：3 位数在 16px 图标里放不下 2 倍字号，只能按 1 倍画。不喜欢可以把 `iconMode` 改成 `Battery`。
6. **需要 .NET 8 桌面运行时**（除非用 `publish-win.cmd` 出自包含版本）。
7. 依赖 `hid.dll` 的三种报文；如果某天厂商改了固件协议，`--diag` 里的原始字节是第一手证据。
8. 不处理灯效、宏、回报率——只做电量和 DPI。
9. **手柄只有"档位"，没有精确百分比**：Switch Pro 是 5 档（空/低/中/高/满 → 5/25/50/75/100%），
   Xbox 是 4 档（空/低/中/满 → 5/25/60/100%）。这是协议/API 的上限，不是本程序的取舍（见 §8.3）。
10. **Xbox 有线直连不显示电量**：XInput 会报 `BATTERY_TYPE_WIRED`（USB 供电、根本没有电池），
    这种情况程序不登记该设备，属正常行为。

### 8.1 漫步者 DPI 是怎么定位的（已解决，留作方法论）

官方 App 的配置页是**按标签页按需读**的，所以第一次抓包（只到启动）里没有 DPI。
第二次抓包的做法值得记下来：**打开 App → 点开 DPI 标签 → 改一次 DPI → 应用**，然后抓 USBPcap。

结果一目了然：App 改 DPI 只做了「`0x01` → 写页 0x05 偏移 `0x30` 的 24 字节块 → `0x02`」，
两次 burst 逐字节 diff **只有一处数据不同**。但**那一次只定位到"改的是这一格"，还没搞清整页布局** ——
真正的结构是后来把官方 App 里七档的实际数值全部要来、和页面字节逐一对照才定下来的：

```
档位      1     2     3     4     5      6     7
App 显示  400   800   1000  1200  1300   1600  2500
页内 raw  07    0F    13    17    19     1F    31
偏移      0x10  0x19  0x22  0x2B  0x34   0x3D  0x46
```

**7 条记录、每条 9 字节，第 k 档在 `0x10 + 9k`；记录头 2 字节是该档 DPI 的 u16 小端，
DPI = (raw + 1) × 50**（`(25+1)×50=1300`、`(49+1)×50=2500` 全中）。
当前档位下标在页内偏移 `12`，读/写都要按它定位到对应记录 —— 认死某个偏移会读到别的档位。

写的时候照抄 App 的动作最稳：读整页 → 只改该档那 2 字节 → 用 `0x01`/`0x02` 夹住写"包含该字段的
那个 24 字节块" → **读回校验**（不一致就把原块写回去）。诊断里的检查（页够长、非零字节数够多、
档位下标 0..6、换算值不超上限）是防止读串页之后照着垃圾数据写。

> 想单独看某个配置页（页号从抓包里读出来）：
> `RazerMini.exe --diag --probe-page 0A` —— 只发读帧，不写任何东西。

> **两条实测结论（2026-10-27 真机）**
> 1. **必须整页四块写**。只写含 DPI 的那一块也能立刻生效（手感马上变），但**切档就回滚** ——
>    设备只把它当成"即时 DPI"，没落到存储里。整页四块（24/24/24/12）写完才是持久的。
> 2. **HECATE Connect 的显示不可信**。我们把它改成 800 且切档往返都保持 800，App 里那一档
>    仍显示旧值 —— 它读的是自己的缓存/配置副本。判断标准是**鼠标手感 + 我们读回的页面字节**。

| 项 | 值 |
|---|---|
| 通道 | report ID `0x04`，64 字节，中断 OUT `0x05` / IN `0x82` |
| 帧头 | `[0]=0x04 [1]=**校验和** [2]=0 [3]=命令 [4]=长度 [5]=偏移 [6..7]=0 [8..]=数据` |
| **校验和** | `[1] = (字节[3]..字节[63] 之和) & 0xFF` —— **算错设备就静默丢帧**（抓包 130 条请求全部吻合） |
| 查电量 | `[3]=0x1A` `[4]=0x06` → 回 `[8]`=百分比、`[9]`=充电标志 |
| **会话初始化** | 设备只回 `0xAA`、其它命令一概不理时，要先按官方 App 的序列走一遍：`AA` → `01` → 页03/07/05 整页读 → `02`（逐帧见 `EdifierHecate.SessionSequence`） |
| 读配置页 | `[3]=页号`，页 `0x03`(34B = `0x18`@0 + `0x0A`@0x18) / `0x05`(84B) / `0x07`(126B)，按 24 字节分块读 |
| **DPI 表** | 页 `0x05` 自偏移 `0x10` 起 **7 条记录，每条 9 字节**，第 k 档在 `0x10 + 9k`；记录头 u16 小端 = raw |
| **DPI 换算** | `DPI = (raw + 1) × 50`；实测七档：400/800/1000/1200/1300/1600/2500 ↔ raw 07/0F/13/17/19/1F/31 |
| **写 DPI** | 读整页 → 按当前档位定位 → 只改那 2 字节 → `0x01` → **整页四块连写**（24/24/24/12）→ `0x02` → **按写入的偏移**读回校验 |
| **设当前档位** | `[3]=0x28` `[4]=0x01`，`[8]`=目标下标（0..6）。⚠️ **这是"设"不是"读"**，数据填 0 就把鼠标切回第 1 档 |
| 当前档位在哪 | 页 `0x05` 偏移 **12**（App 设完 `0x28` 就写这一格；读这一格即可） |
| 写配置页 | `[3]=0x06`，同样带 `[4]=长度 [5]=偏移`，数据从 `[8]` 起 |
| 切档位 | 官方 App 的做法：`0x28` 定档 → 写页 `0x05` 偏移 0 的 24 字节（页内偏移 12 = 档位）→ 整页读回 |

---

### 8.2 高斯键盘（`05AC:024F`）—— 只读电量

2.4G 接收器暴露一堆集合，**只认 `usage=0xFF60/0x0061` 且 in/out 都是 33 字节那一个**
（同 usage 的 65/65 集合不能用，那是另一个集合）。

| 项 | 值 |
|---|---|
| 查询 | 33 字节：`[0]=0x00`(report ID) `[1]=0x20` `[2]=0x01`，其余全 0 |
| 应答 | `20 01 00 [3]=电量%`（带不带 report ID 前缀都兼容：找 `20 01` 标记，其后第 3 字节） |
| 充电状态 | 协议里没有 → 托盘显示"未知" |
| 成功率 | 约 50%（750ms 应答窗口会撞上键盘休眠/唤醒）→ 代码里固定重试 1 次 |
| 型号 | `GS3104T-Pro` 与 `XS75T` 同 PID，接收器只报通用名字（`2.4G Dongle`），**协议上无法区分**；程序显示名按本机型号 **XS75T**（改 `GanssKeyboard.ModelName` 一行即可换名） |

**键盘休眠时读不到电量不是 bug**：按一下键唤醒，再点「立即刷新」即可。

---

### 8.3 Xbox 手柄为什么只有 4 档（以及为什么不去破解它）

Xbox 手柄的电量走微软私有 **GIP** 协议，Windows 上由 `xboxgip.sys` 独占，用户态碰不到那条通道。
能拿到的唯一官方来源是 `XInputGetBatteryInformation`，它只返回**类型 + 4 档**：

```
BatteryType : 0 未连接 / 1 有线 / 2 碱性(AA) / 3 镍氢(充电包) / 255 未知
BatteryLevel: 0 空 / 1 低 / 2 中 / 3 满
```

已知的同类工具（`NiyaShy/XB1ControllerBatteryIndicator`，支持 1537/1697/1698/1708/1797/1914 +
官方或第三方接收器）也是这么做的，并且明确写了：**只有接收器这条路可用，蓝牙读不到或读数错误**。

**注意**：`XInput` 这条路的前提是接收器由 **Xbox 的驱动**接管。如果 `--usb`/诊断里看到接收器的
驱动是 `mt7612US`（MediaTek Wi-Fi），说明它被当成网卡绑走了，XInput 会一直"未连接" —— 修法见 §8.5。

**想要精确百分比只有一条路**：把接收器从 `xboxgip.sys` 手里抢过来（换成 WinUSB/libwdi 之类），
自己实现 GIP 会话去问电量。代价是**该接收器在游戏里就不能再当手柄用了**（Windows 不再能把它识别为
Xbox 设备），属于"为了看电量牺牲功能"。所以本程序**不做**，只在 README 留这条说明。
如果哪天只想"临时查一次电量"，可以用 `--usb` + 独立的抓包工具单独验证，不影响日常使用。

### 8.5 Xbox 接收器出现在"网络适配器"下是**正常**的（别去改驱动）

很容易误判的一条，2026-10-29 我自己就在这里判错过一次：

```
网络适配器 → Xbox Wireless Adapter for Windows   045E:02FE
  驱动详情：mt7612US.sys（MediaTek MT7612 射频）+ xboxgip.sys（微软 Xbox 总线）
```

**老款大适配器（`02FE`）的官方驱动包本来就包含这两个驱动**，所以它在设备管理器里
**正常就显示在"网络适配器"分类下** —— 微软官方的适配器排障页也是让你去"网络适配器"里找它。
因此"类是 Net / 服务是 mt7612*" **不能**当成"被 Wi-Fi 驱动抢绑"，也不需要改驱动。

真正要理解的是**设备树的形状**：

```
USB\VID_045E&PID_02FE            ← 只有接收器是 USB 设备（挂在"网络适配器"下）
  └─ XBOXGIP\…PID_02D1…          ← xboxgip.sys 为**手柄**创建的虚拟设备
       └─ HID\VID_045E&PID_02D1&IG_00   ← xinputhid 包的 XInput HID 集合（游戏/Steam 看到的就是它）
```

由此可得两条实用结论：

1. **手柄节点永远不出现在 USB 枚举里**（它不是 USB 设备），所以 `usb.cmd` 里没有 `02D1` 是正常的 ——
   别据此判断"手柄没连上"。要看手柄请用 `diag.cmd`（XInput 槽位 + HID 接口 + 父设备）。
2. 手柄连不上时，排查顺序是**配对**而不是驱动：手柄开机 → 按接收器上的配对键（灯快闪）→
   按住手柄顶部配对键 → 两边灯变常亮。XInput 报 `1167` 就是没连上，报 `Wired` 才是插着 USB 线。

`usb.cmd`（v1.17.0 起）会直接把每个设备节点的**父设备**打出来，用来一眼确认：
父是 `USB\…`=有线、`XBOXGIP\…`=接收器、`BTHENUM/BTHLE\…`=蓝牙。

### 8.4 Switch Pro 为什么要先"切报告模式"

Windows 给这台手柄默认推的是 **0x3F 简化报告**，里面**没有电量字段**。要拿电量必须发一次
输出报告 `0x01`（蓝牙；`[0]=0x01 [1]=序号 [2..9]=8 字节中性震动 [10]=子命令 [11..]=数据`），
子命令 `0x03` 数据 `0x30` = **set input report mode = standard full**，之后设备每 1/60 秒推 0x30 全量报告。

0x30 报告里 **`[2]` = 电池与连接信息**（Linux `hid-nintendo.c` 的 `joycon_parse_battery_status`）：

```
bit0    = USB 供电
bit4    = 正在充电
bit5..7 = 档位 0 空 / 1 低 / 2 中 / 3 高 / 4 满
```

⚠️ 流传很广的"高 4 位就是电量"是错的：bit4 是充电位，把"满电充电中"读成 8（非法档位）。
所以代码里对档位 > 4 一律判失败，不硬解释成 100%。

两个实现细节（都踩过）：
- **输出报告必须带那段 8 字节震动数据**，少了设备把帧当非法直接丢；
- 手柄休眠/重连后模式复位，所以"读不到 0x30"时才补发子命令（不是每轮无条件发），
  并且零权限句柄也能发 —— `HidD_SetOutputReport` 是 `FILE_ANY_ACCESS`（`WriteFile` 不行）。

## 九、协议速查（改代码前必看）

技能 `user-peripheral-protocols` 里有完整事实库；关键几条：

| 项 | 值 |
|---|---|
| 通道 | 91 字节 feature report（含 report ID），`usage=0x0001/0x0002` |
| 事务号 | Viper Ultimate = **`0xFF`**（按型号分表，毒蝰 V4 Pro 是 `0x1F`） |
| 电量 | class `0x07` / id `0x80`，应答 `args[1]` = 原始值 |
| 充电 | class `0x07` / id `0x84`，应答 `args[1]` = 0/1 |
| 读 DPI | class `0x04` / id `0x85`，`args[0]=NOSTORE`，应答 x=`args[1..2]`、y=`args[3..4]`（大端） |
| 写 DPI | class `0x04` / id `0x05`，`args[0]=VARSTORE`，args[1..2]=X、args[3..4]=Y（大端） |
| CRC | `XOR(buf[3..88])`，**不含事务号**（对应 openrazer 的 `XOR(struct[2..87])`） |
| busy | `status=0x01` 按 openrazer 口径视为成功（本工具会先重试 5 次再兜底接受） |

无线经底座时系统只枚举 `007E`，看不到 `007B`——两个 PID 都认，并按 91 字节 feature 长度兜底。
