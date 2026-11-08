#!/bin/bash
# 在 fnOS/NAS 上离线编译 RazerMini（跳过 NuGet restore，csc 直编）。
#
#   build-linux.sh            编译 Core + 跑断言 + 编 WinForms 主程序
#   build-linux.sh --no-verify 跳过断言
#
# 产物：dist/RazerMini/  ← 整个文件夹拷到 Windows 就能跑（需要 .NET 8 桌面运行时）
# 依赖：$DOTNET_DIR（.NET 8 SDK）、$OFF/ext/wdref（Microsoft.WindowsDesktop.App.Ref 8.0.32）
set -uo pipefail
# fnOS 上 csc 产物默认权限是 000，不设 umask 用户拷到 Windows 都读不了
umask 022

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_DIR="${DOTNET_DIR:-/vol1/@appdata/trim.hermes/hermes/workspace/dotnet}"
OFF="${OFF:-/vol1/@appdata/trim.hermes/hermes/workspace/nuget-offline}"
SDK_VER="${SDK_VER:-8.0.426}"
REF_VER="${REF_VER:-8.0.32}"

CSC="$DOTNET_DIR/sdk/$SDK_VER/Roslyn/bincore/csc.dll"
REF="$DOTNET_DIR/packs/Microsoft.NETCore.App.Ref/$REF_VER/ref/net8.0"
WDREF="$OFF/ext/wdref/ref/net8.0"
DN="$DOTNET_DIR/dotnet"
OUT="$ROOT/out"
DIST="$ROOT/dist/RazerMini"
APPHOST_NUPKG="$OFF/pkgs/host-win-x64.nupkg"

VERIFY=1
for a in "$@"; do [ "$a" = "--no-verify" ] && VERIFY=0; done

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/vol1/@appdata/trim.hermes/hermes/workspace/dotnet-home}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

fail() { echo "✗ $*" >&2; exit 1; }
step() { echo; echo "▶ $*"; }

[ -f "$CSC" ] || fail "找不到编译器 $CSC"
[ -d "$REF" ] || fail "找不到基础引用程序集 $REF"
[ -d "$WDREF" ] || fail "找不到 WinForms 引用程序集 $WDREF（先下 Microsoft.WindowsDesktop.App.Ref $REF_VER）"

mkdir -p "$OUT" "$DIST"
# 每次清空 dist，避免旧版本残留文件混进交付包（曾经漏过 deps.json 就是因为只有增量覆盖）
rm -rf "$DIST"
mkdir -p "$DIST"

# ─────────── 1. Core 类库 ───────────
step "编译 RazerMini.Core"
CORE_RSP="$OUT/core.rsp"
{
  for f in "$REF"/*.dll; do echo "-r:$f"; done
  echo "-target:library"
  echo "-optimize+"
  echo "-nologo"
  echo "-nullable:enable"
  echo "-langversion:latest"
  echo "-deterministic"
  echo "-out:$OUT/RazerMini.Core.dll"
  find "$ROOT/src/RazerMini.Core" -name '*.cs' | sort
} > "$CORE_RSP"

CORE_LOG="$OUT/core.log"
if ! "$DN" "$CSC" "@$CORE_RSP" > "$CORE_LOG" 2>&1; then
  echo "✗ Core 编译失败：" >&2
  grep -E "error CS" "$CORE_LOG" | head -25 >&2
  exit 1
fi
echo "✓ Core 编译通过（$(du -h "$OUT/RazerMini.Core.dll" | cut -f1)）"
grep -c "warning CS" "$CORE_LOG" >/dev/null 2>&1 && echo "  ⚠ $(grep -c 'warning CS' "$CORE_LOG") 条警告" || true
grep -E "warning CS" "$CORE_LOG" | head -10

# ─────────── 2. 协议断言（直接在 Linux 跑）───────────
if [ "$VERIFY" = "1" ]; then
  step "编译并运行协议断言"
  VER_RSP="$OUT/verify.rsp"
  {
    for f in "$REF"/*.dll; do echo "-r:$f"; done
    echo "-target:exe"
    echo "-nologo"
    echo "-nullable:enable"
    echo "-langversion:latest"
    echo "-debug:portable"
    echo "-out:$OUT/verify.dll"
    find "$ROOT/src/RazerMini.Core" -name '*.cs' | sort
    echo "$ROOT/tests/Verify/Verify.cs"
  } > "$VER_RSP"

  VER_LOG="$OUT/verify-build.log"
  if ! "$DN" "$CSC" "@$VER_RSP" > "$VER_LOG" 2>&1; then
    echo "✗ 断言程序编译失败：" >&2
    grep -E "error CS" "$VER_LOG" | head -25 >&2
    exit 1
  fi

  cat > "$OUT/verify.runtimeconfig.json" <<'EOF'
{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}
EOF

  if ! "$DN" "$OUT/verify.dll"; then
    fail "断言失败（见上方 ✗ 项）"
  fi
else
  step "跳过断言（--no-verify）"
fi

# ─────────── 3. WinForms 主程序 ───────────
step "编译 RazerMini（WinForms, net8.0-windows）"
APP_RSP="$OUT/app.rsp"
{
  for f in "$REF"/*.dll; do echo "-r:$f"; done
  for f in "$WDREF"/*.dll; do echo "-r:$f"; done
  echo "-r:$OUT/RazerMini.Core.dll"
  echo "-target:winexe"
  echo "-optimize+"
  echo "-nologo"
  echo "-nullable:enable"
  echo "-langversion:latest"
  echo "-deterministic"
  echo "-win32manifest:$ROOT/src/RazerMini/app.manifest"
  echo "-out:$DIST/RazerMini.dll"
  find "$ROOT/src/RazerMini" -name '*.cs' | sort
} > "$APP_RSP"

APP_LOG="$OUT/app.log"
if ! "$DN" "$CSC" "@$APP_RSP" > "$APP_LOG" 2>&1; then
  echo "✗ 主程序编译失败：" >&2
  grep -E "error CS" "$APP_LOG" | head -30 >&2
  exit 1
fi
echo "✓ 主程序编译通过（$(du -h "$DIST/RazerMini.dll" | cut -f1)）"
grep -E "warning CS" "$APP_LOG" | head -10

# ─────────── 4. 组装可运行目录 ───────────
step "组装 dist/RazerMini/"
cp -f "$OUT/RazerMini.Core.dll" "$DIST/"
cp -f "$OUT/RazerMini.Core.pdb" "$DIST/" 2>/dev/null || true

cat > "$DIST/RazerMini.runtimeconfig.json" <<'EOF'
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "framework": {
      "name": "Microsoft.WindowsDesktop.App",
      "version": "8.0.0"
    },
    "configProperties": {
      "System.Windows.Forms.HighDpiMode": "PerMonitorV2"
    }
  }
}
EOF

# deps.json 必需：没有它 hostpolicy 不会把 RazerMini.Core.dll 放进 TPA，
# 表现就是"双击毫无反应、没有任何报错"（GUI 子系统把宿主错误吞掉了）。
cat > "$DIST/RazerMini.deps.json" <<'EOF'
{
  "runtimeTarget": {
    "name": ".NETCoreApp,Version=v8.0",
    "signature": ""
  },
  "compilationOptions": {},
  "targets": {
    ".NETCoreApp,Version=v8.0": {
      "RazerMini/1.19.0": {
        "dependencies": {
          "RazerMini.Core": "1.19.0"
        },
        "runtime": {
          "RazerMini.dll": {}
        }
      },
      "RazerMini.Core/1.19.0": {
        "runtime": {
          "RazerMini.Core.dll": {}
        }
      }
    }
  },
  "libraries": {
    "RazerMini/1.19.0": {
      "type": "project",
      "serviceable": false,
      "sha512": ""
    },
    "RazerMini.Core/1.19.0": {
      "type": "project",
      "serviceable": false,
      "sha512": ""
    }
  }
}
EOF

# 入口只有 RazerMini.exe（apphost 已修补，双击即托盘，无控制台窗口）。
# 需要看控制台输出时用 diag.cmd，或命令行 `RazerMini.exe --diag`（会自己 AttachConsole）。

cat > "$DIST/diag.cmd" <<'EOF'
@echo off
setlocal
cd /d "%~dp0"
echo Generating diagnostics report...
echo.
dotnet "RazerMini.dll" --diag
echo.
echo ------------------------------------------------------------
echo Copy everything above and send it back.
echo Log files:
echo   %APPDATA%\RazerMini\startup.log
echo   %APPDATA%\RazerMini\razer-mini.log
pause
EOF

# 无窗口入口就是 RazerMini.exe（apphost 已修补）。这里不再提供 vbs 启动器。

# 强制 CRLF（否则 cmd 在某些行上解析异常）
for f in "$DIST/diag.cmd"; do
  sed -i 's/\r$//; s/$/\r/' "$f"
done

# 有了 win-x64 的 apphost，双击 RazerMini.exe 就能启动（否则只能用 dotnet RazerMini.dll）
if [ ! -f "$APPHOST_NUPKG" ]; then
  mkdir -p "$(dirname "$APPHOST_NUPKG")"
  curl -sSL -o "$APPHOST_NUPKG" \
    "https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.host.win-x64/$REF_VER/microsoft.netcore.app.host.win-x64.$REF_VER.nupkg" \
    || echo "⚠ apphost 下载失败，将只有 RazerMini.dll（可用 dotnet 启动）"
fi
if [ -f "$APPHOST_NUPKG" ]; then
  TMP_APPHOST="$OUT/apphost-extract"
  rm -rf "$TMP_APPHOST"; mkdir -p "$TMP_APPHOST"
  unzip -o -q "$APPHOST_NUPKG" -d "$TMP_APPHOST" 2>/dev/null || true
  APPHOST=$(find "$TMP_APPHOST" -name 'apphost.exe' | head -1)
  if [ -n "${APPHOST:-}" ]; then
    cp -f "$APPHOST" "$DIST/RazerMini.exe"
    # 两处必须改的 PE/apphost 内容（SDK 正常构建由 AppHostPatcher 完成，手工组装必须自己来）：
    #  1) PE 头 Subsystem 3(CONSOLE) → 2(GUI)，否则双击弹黑框
    #  2) 把内嵌的"应用名占位符"换成真实 dll 名。**这一步不做，双击会静默退出**：
    #     hostfxr 会拿那串 64 位 hex 当 app 路径去找，找不到直接退，GUI 下没有任何输出。
    python3 - "$DIST/RazerMini.exe" <<'PY' || echo "⚠ apphost 补丁失败（双击可能仍无反应，详见 README）"
import struct, sys
path = sys.argv[1]
data = bytearray(open(path, 'rb').read())

# 1) 子系统 → GUI
pe = struct.unpack_from('<I', data, 0x3C)[0]
if data[pe:pe+4] != b'PE\0\0':
    sys.exit("不是有效 PE 文件")
opt = pe + 24
if struct.unpack_from('<H', data, opt)[0] not in (0x10B, 0x20B):
    sys.exit("未知 PE 格式")
before = struct.unpack_from('<H', data, opt + 68)[0]
struct.pack_into('<H', data, opt + 68, 2)
print(f"  PE 子系统 {before} → 2 (GUI)")

# 2) 应用名占位符 → RazerMini.dll（Microsoft.NET.HostModel 的 AppBinaryPathPlaceholder）
PLACEHOLDER = b"c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2"
APP_NAME = b"RazerMini.dll"

idx = data.find(PLACEHOLDER)
if idx < 0:
    sys.exit("找不到应用名占位符：这个 apphost 可能已被 SDK 打过补丁")
if len(APP_NAME) >= len(PLACEHOLDER):
    sys.exit("应用名过长，占位符空间不够")

data[idx:idx + len(APP_NAME)] = APP_NAME
data[idx + len(APP_NAME):idx + len(PLACEHOLDER)] = b"\x00" * (len(PLACEHOLDER) - len(APP_NAME))
print(f"  应用名占位符 → {APP_NAME.decode()}（偏移 {idx}）")

open(path, 'wb').write(data)
PY
    chmod +x "$DIST/RazerMini.exe"
    echo "✓ 已放入并修补 apphost（RazerMini.exe，双击可启动）"
  else
    echo "⚠ nupkg 里没找到 apphost.exe"
  fi
fi

cat > "$DIST/说明.txt" <<'EOF'
RazerMini —— 外设电量托盘小工具（鼠标 / 键盘 / 手柄）

支持设备：
  雷蛇 Viper Ultimate    1532:007B / 007E / 007A   电量 + DPI
  雷柏 VT9 Pro           24AE:1205                电量 + DPI（当前档位）
  漫步者 HECATE G3M Pro  320F:706E                电量 + DPI
  高斯 XS75T 键盘        05AC:024F                只读电量
  任天堂 Switch Pro      057E:2009（蓝牙）         电量 5 档（空/低/中/高/满）
  Xbox 手柄（接收器）    XInput 槽位识别           电量 4 档（空/低/中/满）；有线直连无电池
多只同时插着时全部显示，点菜单里的设备行切换托盘图标。

桌面浮窗（v1.19.0 起）：
  托盘右键 →「桌面浮窗」开关显隐（默认开）。每台受支持设备一行（环形电量 + 名称 + 状态）。
  **默认不可交互**：鼠标点击、滚轮完全穿透到下面的窗口，位置也锁定，不会误触。
  **不是置顶窗口**：平时沉在桌面底层，不会挡住任何窗口（打开交互时会临时提上来方便抓它）。
  默认出现在主屏右上角、离边缘 32px；位置/大小按显示器比例记忆，换分辨率不会跑偏。
  要用鼠标挪它：托盘右键 →「浮窗可交互（移动 / 缩放）」打勾，然后
    拖动面板空白处 = 移动，拖四边/四角 = 改大小（界面上没有任何按钮）；
    再点一次该菜单项 = 恢复点击穿透 + 位置锁定。
  设备插上后 1 秒内自动出现（订阅了系统插拔通知，不用点"立即刷新"）；界面无任何动画，
  只有电量/状态真的变化时才重画一帧。
  毛玻璃是**程序自己算的**（截屏→强模糊→深色叠色），不依赖系统"透明效果"开关，
  也不受显卡驱动影响；想退回纯色面板：把 %APPDATA%\RazerMini\settings.json 里的
  "overlayGlass" 改成 "solid" 重启即可。

运行前提：装 .NET 8 桌面运行时（Desktop Runtime, x64）
  https://dotnet.microsoft.com/download/dotnet/8.0/runtime
先确认：命令行执行 dotnet --list-runtimes，输出里应有 Microsoft.WindowsDesktop.App 8.0.x

双击 RazerMini.exe 启动（无控制台窗口，图标进托盘；Win11 可能把它收进 ^ 溢出区）。
注意：不要改 exe 文件名——apphost 里烘死了 RazerMini.dll 这个名字，改名后双击无反应。
排障：双击 diag.cmd（等价 RazerMini.exe --diag）输出诊断报告；
再看 %APPDATA%\RazerMini\startup.log 有没有本次启动的时间戳
（没有 = 进程没进 .NET，多半是缺运行时或缺文件；有 = 日志会写原因）。

本目录必需文件（缺一个都可能静默失败）：
  RazerMini.exe / RazerMini.dll / RazerMini.Core.dll
  RazerMini.runtimeconfig.json / RazerMini.deps.json

命令行核验：
  RazerMini.exe --once      读一次电量/DPI
  RazerMini.exe --diag      完整诊断报告（鼠标/键盘也要看时用）
配置：%APPDATA%\RazerMini\settings.json
日志：%APPDATA%\RazerMini\razer-mini.log
EOF

echo
echo "▶ 产物"
chmod 755 "$DIST" 2>/dev/null || true
chmod 644 "$DIST"/* 2>/dev/null || true
chmod 755 "$DIST/RazerMini.exe" 2>/dev/null || true
ls -la "$DIST"

step "产物自检"
python3 - "$DIST" <<'PY' || exit 1
import json, struct, sys, pathlib
dist = pathlib.Path(sys.argv[1])
fails = []

def subsystem(path):
    data = path.read_bytes()
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe:pe+4] != b'PE\0\0':
        return None
    return struct.unpack_from('<H', data, pe + 24 + 68)[0]

exe_sub = subsystem(dist / "RazerMini.exe")
dll_sub = subsystem(dist / "RazerMini.dll")
print(f"  RazerMini.exe 子系统 = {exe_sub} {'(GUI ✓)' if exe_sub == 2 else '(应为 2!)'}")
if exe_sub != 2: fails.append("apphost 子系统不是 GUI")

# apphost 里内嵌的应用名必须是 RazerMini.dll，且占位符必须已被替换掉
# （没替换 = 双击静默退出，2026-10-26 的根因）
exe_bytes = (dist / "RazerMini.exe").read_bytes()
placeholder = b"c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2"
has_name = b"RazerMini.dll\x00" in exe_bytes
has_placeholder = placeholder in exe_bytes
print(f"  apphost 内嵌应用名 RazerMini.dll: {has_name}，残留占位符: {has_placeholder}")
if not has_name: fails.append("apphost 没有内嵌应用名（双击会无反应）")
if has_placeholder: fails.append("apphost 仍残留应用名占位符（双击会无反应）")

print(f"  RazerMini.dll 子系统 = {dll_sub} {'(GUI ✓)' if dll_sub == 2 else '(应为 2!)'}")
if dll_sub != 2: fails.append("托管 DLL 子系统不是 GUI")

dll = (dist / "RazerMini.dll").read_bytes()
has_manifest = b"PerMonitorV2" in dll and b"requestedExecutionLevel" in dll
print(f"  嵌入 manifest（PerMonitorV2 + asInvoker）: {has_manifest}")
if not has_manifest: fails.append("manifest 未嵌入")

has_ref = b"RazerMini.Core" in dll
print(f"  引用 RazerMini.Core: {has_ref}")
if not has_ref: fails.append("缺少 RazerMini.Core 引用")

core = (dist / "RazerMini.Core.dll").read_bytes()
# 类型名在元数据 #Strings 堆里是 UTF-8；显示名在 #US 堆里是 UTF-16
has_proto = b"RazerMouseModel" in core and "Viper Ultimate".encode("utf-16-le") in core
print(f"  Core 内含雷蛇型号表: {has_proto}")
if not has_proto: fails.append("Core 里没有型号表（编错文件？）")

cfg = json.loads((dist / "RazerMini.runtimeconfig.json").read_text())
fw = cfg["runtimeOptions"]["framework"]
print(f"  runtimeconfig: {fw['name']} {fw['version']}")
if fw["name"] != "Microsoft.WindowsDesktop.App": fails.append("runtimeconfig 框架不对")

deps_path = dist / "RazerMini.deps.json"
if not deps_path.exists():
    fails.append("缺 deps.json（会导致静默启动失败）")
else:
    deps = json.loads(deps_path.read_text())
    target = deps["targets"][deps["runtimeTarget"]["name"]]
    listed = [f for lib in target.values() for f in lib.get("runtime", {})]
    ok = "RazerMini.dll" in listed and "RazerMini.Core.dll" in listed
    print(f"  deps.json 列出的运行时文件: {listed} {'✓' if ok else '(缺 Core!)'}")
    if not ok: fails.append("deps.json 没列出 RazerMini.Core.dll")

ver = None
try:
    # 版本号在元数据里，先粗查字符串（跟 AssemblyInfo.cs 的 AssemblyVersion 对齐）
    ver = "1.19.0" if b"1.19.0.0" in dll else None
except Exception:
    pass
print(f"  程序集版本 1.19.0: {bool(ver)}")
if not ver: fails.append("程序集版本不是 1.19.0（AssemblyInfo 没编进去？）")

if fails:
    print("  ✗ 自检失败：" + "；".join(fails))
    sys.exit(1)
print("  ✓ 自检全部通过")
PY

echo
echo "总大小: $(du -sh "$DIST" | cut -f1)"
