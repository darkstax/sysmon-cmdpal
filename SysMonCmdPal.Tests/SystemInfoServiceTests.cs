// Copyright (c) 2026 SysMonCmdPal
// SystemInfoService 测试 — CalcSpeed 算法 + 结构体 + SensorBackend 枚举

using System.Reflection;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SystemInfoServiceTests
{
    // ================================================================
    // CalcSpeed — 速率计算（private static）
    // ================================================================

    [Fact]
    public void CalcSpeed_NormalDelta_ReturnsBytesPerSecond()
    {
        // prev=0, current=1000, elapsed=1s → speed = 1000 B/s
        long prev = 0;
        long current = 1000;
        DateTime prevTime = DateTime.UtcNow.AddSeconds(-1);
        DateTime now = prevTime.AddSeconds(1);

        double speed = InvokeCalcSpeed(ref prev, current, ref prevTime, now);

        Assert.True(speed > 900 && speed < 1100, $"Expected ~1000, got {speed}");
    }

    [Fact]
    public void CalcSpeed_UpdatesPrevValues()
    {
        long prev = 100;
        long current = 200;
        DateTime prevTime = DateTime.UtcNow.AddSeconds(-1);
        DateTime now = prevTime.AddSeconds(1);

        InvokeCalcSpeed(ref prev, current, ref prevTime, now);

        Assert.Equal(200, prev);          // prev 更新为 current
        Assert.Equal(now, prevTime);      // prevTime 更新为 now
    }

    [Fact]
    public void CalcSpeed_CounterReset_ReturnsZero()
    {
        // 计数器重置：current < prev（比如接口重启后字节计数归零）
        long prev = 1_000_000;
        long current = 100;
        DateTime prevTime = DateTime.UtcNow.AddSeconds(-5);
        DateTime now = prevTime.AddSeconds(5);

        double speed = InvokeCalcSpeed(ref prev, current, ref prevTime, now);

        Assert.Equal(0, speed);           // 负值返回 0
    }

    [Fact]
    public void CalcSpeed_ZeroElapsed_ReturnsZero()
    {
        long prev = 0;
        long current = 1000;
        DateTime now = DateTime.UtcNow;
        DateTime prevTime = now;           // elapsed = 0

        double speed = InvokeCalcSpeed(ref prev, current, ref prevTime, now);

        Assert.Equal(0, speed);
    }

    // ================================================================
    // SystemSnapshot — 默认值
    // ================================================================

    [Fact]
    public void SystemSnapshot_Default_AllZeros()
    {
        var s = new SystemSnapshot();
        Assert.Equal(0, s.CpuUsage);
        Assert.Equal(0, s.MemoryTotalBytes);
        Assert.Equal(0, s.MemoryUsedBytes);
        Assert.Equal(0, s.NetDown);
        Assert.Equal(0, s.NetUp);
        Assert.Equal(0, s.BatteryPercent);
        // struct default: reference types are null
        Assert.Null(s.BatteryStatus);
        Assert.Null(s.Disks);
        Assert.Null(s.Gpus);
        Assert.Null(s.BackendNote);
        Assert.Equal(0, s.CpuTemperature);
        // enum default ctor = 0 = PawnMsr (first value)
        Assert.Equal(SensorBackend.PawnMsr, s.Backend);
    }

    // ================================================================
    // DiskInfo — 默认值
    // ================================================================

    [Fact]
    public void DiskInfo_Default_Values()
    {
        var d = new DiskInfo();
        Assert.Null(d.Name);
        Assert.Null(d.VolumeLabel);
        Assert.Equal(0, d.TotalBytes);
        Assert.Equal(0, d.FreeBytes);
        Assert.Equal(0, d.UsedPercent);
        // struct default ctor zeroes all fields
        Assert.Equal(0, d.ReadBytesPerSec);
        Assert.Equal(0, d.WriteBytesPerSec);
    }

    // ================================================================
    // GpuInfo — 默认值
    // ================================================================

    [Fact]
    public void GpuInfo_Default_AllZero()
    {
        var g = new GpuInfo();
        Assert.Null(g.Name);
        Assert.Equal(0, g.UsagePercent);   // struct default ctor zeroes
        Assert.Equal(0, g.Temperature);
        Assert.Equal(0, g.MemoryUsedMB);
        Assert.Equal(0, g.MemoryTotalMB);
    }

    // ================================================================
    // SensorBackend — 枚举值
    // ================================================================

    [Fact]
    public void SensorBackend_AllValuesDefined()
    {
        var values = Enum.GetValues<SensorBackend>();
        Assert.Contains(SensorBackend.PawnMsr, values);
        Assert.Contains(SensorBackend.PawnSmu, values);
        Assert.Contains(SensorBackend.Nvapi, values);
        Assert.Contains(SensorBackend.AdlGpu, values);
        Assert.Contains(SensorBackend.Igcl, values);
        Assert.Contains(SensorBackend.Lhm, values);
        Assert.Contains(SensorBackend.LhmWmi, values);
        Assert.Contains(SensorBackend.AmdAdl, values);
        Assert.Contains(SensorBackend.HwInfo, values);
        Assert.Contains(SensorBackend.None, values);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static double InvokeCalcSpeed(
        ref long prevBytes, long currentBytes, ref DateTime prevTime, DateTime now)
    {
        // CalcSpeed 是 private static 方法
        var method = typeof(SystemInfoService).GetMethod("CalcSpeed",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        // 参数是 ref long, long, ref DateTime, DateTime
        var args = new object[] { prevBytes, currentBytes, prevTime, now };
        double result = (double)method.Invoke(null, args)!;

        // 读取回 ref 参数
        prevBytes = (long)args[0]!;
        prevTime = (DateTime)args[2]!;

        return result;
    }
}
