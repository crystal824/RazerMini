// 显式声明全局 using，而不是依赖 ImplicitUsings 生成的文件 ——
// 这样 NAS 上 csc 直编（不走 MSBuild）和 Windows 上 dotnet build 用的是同一份声明。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
