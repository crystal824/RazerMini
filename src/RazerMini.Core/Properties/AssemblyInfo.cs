using System.Reflection;
using System.Runtime.Versioning;

// 手工编译（NAS 上 csc 直编）不会生成这些特性，所以显式写一份，
// 保证 dotnet build 与 csc 直编产出的程序集版本一致（deps.json 里也要用到）。
[assembly: AssemblyTitle("RazerMini")]
[assembly: AssemblyDescription("雷蛇 Viper Ultimate 托盘小工具：电量 + DPI")]
[assembly: AssemblyProduct("RazerMini")]
[assembly: AssemblyCompany("RazerMini")]
[assembly: AssemblyVersion("1.19.0.0")]
[assembly: AssemblyFileVersion("1.19.0.0")]
[assembly: AssemblyInformationalVersion("1.19.0")]
[assembly: TargetFramework(".NETCoreApp,Version=v8.0")]
