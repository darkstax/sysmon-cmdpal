// SysMonCmdPal.Tests/ShmLayoutConsistencyTests.cs
// R22 常量守护测试：共享内存布局常量在两端（SysMonBroker.IPC.BrokerSharedMemory
// 写端 ↔ SysMonCmdPal.Broker.ShmLayout 读端）必须逐一一致。
//
// ShmLayout.cs 头部注明"此文件在 Broker 和 Plugin 之间共享（手动同步）"——本测试
// 用反射把手工同步变成可强制执行的守护：
//   * public const 直接取值；
//   * private const 通过 FieldInfo.GetRawConstantValue() 读取（const 是元数据
//     literal，无需反射权限、无需 InternalsVisibleTo）。
// 因此不需要给 SysMonBroker 程序集加 InternalsVisibleTo，也不改动 Broker 侧声明。

using System.Reflection;
using System.Text;
using SysMonBroker.IPC;
using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class ShmLayoutConsistencyTests
{
    // Broker 常量名 → ShmLayout 常量名（仅两端命名不同的常量需要映射）
    public static TheoryData<string, string> ConstantPairs => new()
    {
        // ---- Map names ----
        { "MapName", "MapName" },
        { "EventName", "EventName" },
        // ---- Magic & Version ----
        { "MagicValue", "MagicValue" },
        { "MapVersion", "Version" },
        // ---- Map sizing ----
        { "MapSize", "MapSize" },
        { "MaxGpus", "MaxGpus" },
        { "MaxSensors", "MaxSensors" },
        // ---- v2 offsets ----
        { "OffMagic", "OffMagic" },
        { "OffVersion", "OffVersion" },
        { "OffCounter", "OffCounter" },
        { "OffCommitSequence", "OffCommitSequence" },
        { "OffCpuTemp", "OffCpuTemp" },
        { "OffSource", "OffSource" },
        { "OffGpuCount", "OffGpuCount" },
        { "OffGpuBase", "OffGpuBase" },
        { "OffTimestamp", "OffTimestamp" },
        { "OffSensorCount", "OffSensorCount" },
        { "OffSensorBase", "OffSensorBase" },
        // ---- v2 compatible extension ----
        { "OffExtensionMagic", "OffExtensionMagic" },
        { "OffInstanceId", "OffInstanceId" },
        { "OffMonotonicPublishMs", "OffMonotonicPublishMs" },
        { "ExtensionMagicValue", "ExtensionMagicValue" },
        // ---- GPU entry layout ----
        { "GpuNameLen", "GpuNameLen" },
        { "GpuTempOff", "GpuTempOff" },
        { "GpuUsageOff", "GpuUsageOff" },
        { "GpuMemUsedOff", "GpuMemUsedOff" },
        { "GpuMemTotalOff", "GpuMemTotalOff" },
        { "GpuEntrySize", "GpuEntrySize" },
        // ---- Sensor entry layout ----
        { "SensorTagOff", "SensorTagOff" },
        { "SensorNameOff", "SensorNameOff" },
        { "SensorValueOff", "SensorValueOff" },
        { "SensorUnitOff", "SensorUnitOff" },
        { "SensorHwOff", "SensorHardwareOff" }, // 两端命名不同，值必须一致
        { "SensorEntrySize", "SensorEntrySize" },
    };

    [Theory]
    [MemberData(nameof(ConstantPairs))]
    public void BrokerConstant_MatchesPluginLayout(string brokerName, string shmName)
    {
        object brokerValue = ReadConstant(typeof(BrokerSharedMemory), brokerName);
        object shmValue = ReadConstant(typeof(ShmLayout), shmName);

        if (brokerValue is string || shmValue is string)
            Assert.Equal(shmValue, brokerValue);
        else
            Assert.Equal(Convert.ToInt64(shmValue), Convert.ToInt64(brokerValue));
    }

    [Fact]
    public void ExtensionMagic_IsTextMagicWithLittleEndianByteOrder()
    {
        // ExtensionMagicValue = 0x31584D53：按小端写入共享内存后字节序列为
        // 53 4D 58 31 = "SMX1"，与两端代码注释一致。
        // 注意：MagicValue = 0x5342524B 的小端字节序列是 "KRBS"（注释里的
        // "SBRK" 是反向缩写），读端只做数值比较、不解释文本，故不做文本断言。
        Assert.Equal("SMX1", Encoding.ASCII.GetString(BitConverter.GetBytes(ShmLayout.ExtensionMagicValue)));
    }

    [Fact]
    public void DerivedLayoutInvariants_HoldOnBrokerSide()
    {
        // 与两端代码注释中声明的事实一致：
        //   GPU area ends at 60 + 4*72 = 348
        //   Sensor area ends at 364 + 250*64 = 16364
        //   尾部 20 字节 = int32 + uint64 + int64
        Assert.Equal(Broker("OffGpuBase") + Broker("MaxGpus") * Broker("GpuEntrySize"),
            Broker("OffTimestamp"));
        Assert.Equal(Broker("OffSensorCount") + sizeof(int), Broker("OffSensorBase"));
        Assert.Equal(Broker("OffSensorBase") + Broker("MaxSensors") * Broker("SensorEntrySize"),
            Broker("OffExtensionMagic"));
        Assert.Equal(Broker("OffExtensionMagic") + sizeof(int) + sizeof(ulong) + sizeof(long),
            Broker("MapSize"));
    }

    [Fact]
    public void DerivedLayoutInvariants_HoldOnPluginSide()
    {
        Assert.Equal(ShmLayout.OffGpuBase + ShmLayout.MaxGpus * ShmLayout.GpuEntrySize,
            ShmLayout.OffTimestamp);
        Assert.Equal(ShmLayout.OffSensorCount + sizeof(int), ShmLayout.OffSensorBase);
        Assert.Equal(ShmLayout.OffSensorBase + ShmLayout.MaxSensors * ShmLayout.SensorEntrySize,
            ShmLayout.OffExtensionMagic);
        Assert.Equal(ShmLayout.OffExtensionMagic + sizeof(int) + sizeof(ulong) + sizeof(long),
            ShmLayout.MapSize);
    }

    [Fact]
    public void SensorAndGpuAreas_DoNotOverlap()
    {
        Assert.True(ShmLayout.OffSensorBase + ShmLayout.MaxSensors * ShmLayout.SensorEntrySize
            <= ShmLayout.OffExtensionMagic);
        Assert.True(ShmLayout.OffGpuBase + ShmLayout.MaxGpus * ShmLayout.GpuEntrySize
            <= ShmLayout.OffTimestamp);
    }

    // ---- helpers ----

    private static long Broker(string name) =>
        Convert.ToInt64(ReadConstant(typeof(BrokerSharedMemory), name));

    private static object ReadConstant(Type type, string name)
    {
        FieldInfo field = type.GetField(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(type.FullName, name);
        return field.GetRawConstantValue()
            ?? throw new InvalidOperationException(
                $"{type.FullName}.{name} is not a constant");
    }
}
