using System.Reflection;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SensorLoggerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempLogPath;

    public SensorLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SysMonTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempLogPath = Path.Combine(_tempDir, "test.log");

        // 重定向 LogPath 到临时文件
        var field = typeof(SensorLogger).GetField("LogPath",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, _tempLogPath);
    }

    public void Dispose()
    {
        // 恢复原始路径
        var field = typeof(SensorLogger).GetField("LogPath",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysMonCmdPal", "sensor_backend.log"));

        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void ForceLog_WritesToFile()
    {
        SensorLogger.ForceLog("test message 123");

        Assert.True(File.Exists(_tempLogPath), "Log file should be created");
        string content = File.ReadAllText(_tempLogPath);
        Assert.Contains("test message 123", content);
    }

    [Fact]
    public void ForceLog_IncludesTimestamp()
    {
        SensorLogger.ForceLog("timestamp check");

        string content = File.ReadAllText(_tempLogPath);
        // 行格式: "HH:mm:ss.fff message"
        Assert.Matches(@"\d{2}:\d{2}:\d{2}\.\d{3}", content);
    }

    [Fact]
    public void ForceLog_AppendsMultipleEntries()
    {
        SensorLogger.ForceLog("first");
        SensorLogger.ForceLog("second");

        string content = File.ReadAllText(_tempLogPath);
        Assert.Contains("first", content);
        Assert.Contains("second", content);
    }

    [Fact]
    public void ForceLog_CreatesDirectoryIfMissing()
    {
        // 重新指向一个不存在的子目录
        var subDir = Path.Combine(_tempDir, "sub1", "sub2");
        var subLog = Path.Combine(subDir, "nested.log");

        var field = typeof(SensorLogger).GetField("LogPath",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, subLog);

        SensorLogger.ForceLog("nested dir test");

        Assert.True(File.Exists(subLog));
        string content = File.ReadAllText(subLog);
        Assert.Contains("nested dir test", content);
    }
}
