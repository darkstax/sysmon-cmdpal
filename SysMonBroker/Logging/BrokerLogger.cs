// SysMonBroker/Logging/BrokerLogger.cs
// Buffered + size-rotating logger shared by Program.cs and BrokerComServer.cs.
// Replaces File.AppendAllText (open/write/close per call) with a background flush thread.
//
// 容错设计（R05）：
//   - flush 失败不抛异常（日志失败绝不中断业务），记录失败计数并限频输出到 stderr；
//   - 内存 buffer 有 64KB 上限，超限丢弃最旧内容，防止写入失败时内存无限增长；
//   - 每行带 PID 前缀，便于多实例部署时日志归属诊断。

using System.Diagnostics;
using System.Text;

namespace SysMonBroker.Logging;

public static class BrokerLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SysMonCmdPal", "Logs", "broker.log");

    private const long MaxLogSize = 10 * 1024 * 1024;
    private const int MaxBufferBytes = 64 * 1024;            // 内存缓冲上限，超限丢最旧
    private const int BufferTrimTarget = MaxBufferBytes * 3 / 4;

    private static readonly Lock _lock = new();
    private static readonly StringBuilder _buffer = new(8192);
    private static DateTime _lastFlush = DateTime.MinValue;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    private static long s_flushErrorCount;
    private static readonly int Pid = Process.GetCurrentProcess().Id;

    public static void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [pid={Pid}] {msg}\n";
        lock (_lock)
        {
            _buffer.Append(line);
            TrimBufferLocked();
            if (DateTime.UtcNow - _lastFlush > FlushInterval || _buffer.Length > 4096)
            {
                FlushLocked();
                _lastFlush = DateTime.UtcNow;
            }
        }
    }

    public static void Log(string tag, string msg) => Log($"[{tag}] {msg}");

    // 缓冲超限时丢弃最旧内容，保证内存有界（R05）。
    private static void TrimBufferLocked()
    {
        if (_buffer.Length <= MaxBufferBytes) return;
        int drop = _buffer.Length - BufferTrimTarget;
        _buffer.Remove(0, drop);
        _buffer.Insert(0,
            $"{DateTime.Now:HH:mm:ss.fff} [pid={Pid}] [BrokerLogger] dropped {drop} bytes of oldest log lines (buffer overflow)\n");
    }

    private static void FlushLocked()
    {
        if (_buffer.Length == 0) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxLogSize)
                    fi.MoveTo(LogPath + ".old", overwrite: true);
            }
            catch { }

            // 用 FileShare.ReadWrite|Delete 代替 File.AppendAllText 的默认 FileShare.Read：
            // 升级/双开窗口新旧 Broker 实例可能并存写同一日志，默认共享模式会让第二个
            // 实例 Open 直接失败（IO 异常被吞 → 整个 buffer 丢失，审计 A-F8）。宽容共享
            // 下写入允许并发，MoveTo 轮转也不会被已打开的句柄阻止。
            using (var fs = new FileStream(LogPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var sw = new StreamWriter(fs, System.Text.Encoding.UTF8))
            {
                sw.Write(_buffer.ToString());
                sw.Flush();
            }
            _buffer.Clear();
        }
        catch (Exception ex)
        {
            // 不抛：日志失败不应当中断业务。记录计数、限频提示；buffer 保留待下次重试。
            s_flushErrorCount++;
            if (s_flushErrorCount <= 3 || s_flushErrorCount % 10 == 0)
            {
                try { Console.Error.WriteLine($"[BrokerLogger] flush failed ({s_flushErrorCount}x): {ex.Message}"); }
                catch { }
            }
        }
    }

    public static void Flush()
    {
        lock (_lock)
        {
            FlushLocked();
        }
    }
}
