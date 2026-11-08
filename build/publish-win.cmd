@echo off
rem 在 Windows 上出「自包含单文件」版本：目标机器不需要装 .NET 运行时。
rem 需要 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0）
rem 产物：dist-win\RazerMini.exe（约 70MB）

setlocal
cd /d "%~dp0.."

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [x] 找不到 dotnet 命令。请先安装 .NET 8 SDK：
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

echo [*] 发布 win-x64 自包含单文件...
dotnet publish src\RazerMini\RazerMini.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o dist-win

if errorlevel 1 (
    echo [x] 发布失败，看上面的编译错误。
    exit /b 1
)

echo.
echo [ok] 产物：%CD%\dist-win\RazerMini.exe
echo      这个 exe 可以直接拷到别的 Windows 机器双击运行。
endlocal
