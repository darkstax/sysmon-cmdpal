// SysMonBroker/Logging/BrokerLogger.cs
// Buffered + size-rotating logger shared by Program.cs and BrokerComServer.cs.
// Replaces File.AppendAllText (open/write/close per call) with a background flush thread.

using System.Text;

namespace SysMonBroker.Logging;

public static class BrokerLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonCmdPal", "broker.log");

    private const long MaxLogSize = 10 * 1024 * 1024;

    private static readonly Lock _lock = new();
    private static readonly StringBuilder _buffer = new(8192);
    private static DateTime _lastFlush = DateTime.MinValue;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    public static void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}\n";
        lock (_lock)
        {
            _buffer.Append(line);
            if (DateTime.UtcNow - _lastFlush > FlushInterval || _buffer.Length > 4096)
            {
                FlushLocked();
                _lastFlush = DateTime.UtcNow;
            }
        }
    }

    public static void Log(string tag, string msg) => Log($"[{tag}] {msg}");

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

            File.AppendAllText(LogPath, _buffer.ToString());
            _buffer.Clear();
        }
        catch { }
    }

    public static void Flush()
    {
        lock (_lock)
        {
            FlushLocked();
        }
    }
}
