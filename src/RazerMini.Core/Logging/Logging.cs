using System.Text;

namespace RazerMini.Core.Logging;

public interface ILog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}

/// <summary>测试用：把日志留在内存里，断言时检查"失败分支是否打了原因"。</summary>
public sealed class ListLog : ILog
{
    private readonly List<string> _lines = new();
    public IReadOnlyList<string> Lines => _lines;
    private void Add(string level, string message) => _lines.Add($"{level} {message}");
    public void Debug(string message) => Add("DBG", message);
    public void Info(string message) => Add("INF", message);
    public void Warn(string message) => Add("WRN", message);
    public void Error(string message) => Add("ERR", message);
    public bool Contains(string fragment) => _lines.Any(l => l.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 文件日志：%APPDATA%\RazerMini\razer-mini.log，超过 512KB 轮转一份 .1。
/// 设计原则（见 HANDOFF 第七节第 3 条）：任何"读不到"的分支都必须留下原因，不允许静默返回。
/// </summary>
public sealed class FileLog : ILog
{
    private const long MaxBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _echoToConsole;

    public FileLog(string path, bool echoToConsole = false)
    {
        _path = path;
        _echoToConsole = echoToConsole;
    }

    public string Path => _path;

    /// <summary>false 时不写 Debug 行（配 settings.json 的 verboseLogging）。</summary>
    public bool Verbose { get; set; }

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string backup = _path + ".1";
                    try { File.Delete(backup); } catch { /* 无所谓 */ }
                    try { File.Move(_path, backup); } catch { /* 无所谓 */ }
                }
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志自身失败不能再抛，否则会拖垮轮询线程
            }
        }
        if (_echoToConsole)
        {
            Console.WriteLine(line);
        }
    }

    public void Debug(string message)
    {
        if (!Verbose) return;
        Write("DBG", message);
    }
    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);
    public void Error(string message) => Write("ERR", message);
}
