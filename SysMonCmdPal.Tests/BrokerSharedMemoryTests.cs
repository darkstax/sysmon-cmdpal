// SysMonCmdPal.Tests/BrokerSharedMemoryTests.cs
// R21 写端协议测试：覆盖 SysMonBroker.IPC.BrokerSharedMemory 的核心协议逻辑
// （SMX1 提交序列、counter/时间戳单调性、UTF-8 安全截断、初始化保留旧 counter）。
//
// 测试策略：不构造 BrokerSharedMemory 真实实例——构造函数会创建 Global\ 命名
// 互斥体（SysMonBrokerWriter）、命名 file mapping（SysMonBrokerShm）与命名事件
// （SysMonBrokerEvent），在测试/CI 环境会留下系统级副作用并可能受会话权限约束。
// 因此全部用例通过 RuntimeHelpers.GetUninitializedObject（跳过构造函数）+ 反射
// 调用 private 成员，在托管堆缓冲区上验证纯协议逻辑，不触碰任何全局命名对象。
// 注：凡把 _pView 指向托管缓冲区的用例，退出前必须先复位 _pView = IntPtr.Zero
// 再 Dispose——否则 Dispose 会对非 MapViewOfFile 的地址调用 UnmapViewOfFile
// （Linux 上 P/Invoke 直接抛 DllNotFoundException；Windows 上也是未定义行为），
// 且 SuppressFinalize 不会执行导致 finalizer 残留。
// 构造的真实句柄生命周期（互斥体竞争、map 创建/复用）不在本文件覆盖范围——
// 此类行为无法脱离全局对象测试，故按约定跳过。

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using SysMonBroker.IPC;
using SysMonBroker.Sensors;
using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class BrokerSharedMemoryTests
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly Type WriterType = typeof(BrokerSharedMemory);
    private static readonly MethodInfo NextCounterMethod =
        RequiredMethod("NextCounter", StaticPrivate);
    private static readonly MethodInfo NextUtcTimestampMethod =
        RequiredMethod("NextUtcTimestamp", StaticPrivate);
    private static readonly MethodInfo NextMonotonicPublishMsMethod =
        RequiredMethod("NextMonotonicPublishMs", StaticPrivate);
    private static readonly MethodInfo WriteStringMethod =
        RequiredMethod("WriteString", StaticPrivate);
    private static readonly MethodInfo BeginCommitMethod =
        RequiredMethod("BeginCommit", InstancePrivate);
    private static readonly MethodInfo CompleteCommitMethod =
        RequiredMethod("CompleteCommit", InstancePrivate);
    private static readonly MethodInfo InitializeHeaderMethod =
        RequiredMethod("InitializeHeader", InstancePrivate);
    private static readonly FieldInfo PViewField = RequiredField("_pView");

    // ---- NextCounter：单调递增且跳过 0 ----

    [Theory]
    [InlineData(0, 1)]                    // 越过 0
    [InlineData(1, 2)]
    [InlineData(41, 42)]
    [InlineData(-1, 1)]                   // unchecked(-1+1)=0 → 跳到 1
    [InlineData(int.MaxValue, int.MinValue)] // int32 回绕，仍不等于 0
    public void NextCounter_IsMonotonicAndSkipsZero(int input, int expected)
    {
        object? result = NextCounterMethod.Invoke(null, [input]);

        Assert.Equal(expected, result);
    }

    // ---- NextUtcTimestamp：严格单调，时钟回拨时 +1 ----

    [Fact]
    public void NextUtcTimestamp_IsStrictlyMonotonic()
    {
        long previous = (long)NextUtcTimestampMethod.Invoke(null, [0L])!;

        for (int i = 0; i < 200; i++)
        {
            long next = (long)NextUtcTimestampMethod.Invoke(null, [previous])!;
            Assert.True(next > previous,
                $"timestamp must advance: previous={previous}, next={next}");
            previous = next;
        }
    }

    [Fact]
    public void NextUtcTimestamp_AdvancesWhenClockMovesBackward()
    {
        long future = DateTime.UtcNow.Ticks + TimeSpan.FromHours(1).Ticks;

        long result = (long)NextUtcTimestampMethod.Invoke(null, [future])!;

        Assert.Equal(future + 1, result);
    }

    [Fact]
    public void NextUtcTimestamp_DoesNotOverflowAtDateTimeMaxValue()
    {
        // previous == DateTime.MaxValue.Ticks 时回退到当前时间，不产生溢出值
        long result = (long)NextUtcTimestampMethod.Invoke(
            null, [DateTime.MaxValue.Ticks])!;

        Assert.True(result > 0);
        Assert.True(result <= DateTime.UtcNow.Ticks + 1);
    }

    // ---- NextMonotonicPublishMs：单调，回拨时 +1 ----

    [Fact]
    public void NextMonotonicPublishMs_IsMonotonic()
    {
        long previous = (long)NextMonotonicPublishMsMethod.Invoke(null, [0L])!;

        for (int i = 0; i < 200; i++)
        {
            long next = (long)NextMonotonicPublishMsMethod.Invoke(null, [previous])!;
            Assert.True(next > previous,
                $"publish ms must advance: previous={previous}, next={next}");
            previous = next;
        }
    }

    [Fact]
    public void NextMonotonicPublishMs_AdvancesWhenClockMovesBackward()
    {
        // TickCount64 永远不可能大于 long.MaxValue → 走 previous+1 分支（回绕）
        long result = (long)NextMonotonicPublishMsMethod.Invoke(
            null, [long.MaxValue])!;

        Assert.Equal(long.MinValue, result);
    }

    // ---- WriteString：UTF-8 安全截断 ----

    [Fact]
    public void WriteString_WritesFullValueWithinCapacity()
    {
        Assert.Equal("abc", InvokeWriteString("abc", 8));
        Assert.Equal("CPU Package", InvokeWriteString("CPU Package", 32));
        // 恰好 maxBytes-1 字节，完整写入
        Assert.Equal("你好", InvokeWriteString("你好", 7));
    }

    [Theory]
    [InlineData("你好世界", 6, "你")]   // 6B 容量 → 保留 5B → 回退到 "你"(3B)
    [InlineData("AB你CD", 5, "AB")]     // 截断点落在多字节字符中间 → 回退到完整字符
    [InlineData("你", 2, "")]           // 容量容不下一个字符 → 空
    [InlineData("abcdefghij", 5, "abcd")]
    public void WriteString_TruncatesAtUtf8CharacterBoundary(
        string value, int maxBytes, string expected)
    {
        Assert.Equal(expected, InvokeWriteString(value, maxBytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WriteString_ClearsBufferForNullOrEmpty(string? value)
    {
        byte[] buffer = [0x41, 0x42, 0x43];

        unsafe
        {
            fixed (byte* p = buffer)
            {
                WriteStringMethod.Invoke(null, [new IntPtr(p), value, 3]);
            }
        }

        Assert.Equal(new byte[3], buffer);
    }

    // ---- SMX1 提交序列：BeginCommit 后为奇、CompleteCommit 后为偶 ----

    [Fact]
    public unsafe void CommitSequence_IsOddWhileWritingAndEvenAfterCommit()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            fixed (byte* p = buffer)
            {
                BeginCommitMethod.Invoke(writer, [new IntPtr(p)]);
                AssertCommitSequenceParity(buffer, odd: true, "BeginCommit");

                CompleteCommitMethod.Invoke(writer, [new IntPtr(p)]);
                AssertCommitSequenceParity(buffer, odd: false, "CompleteCommit");

                // 第二个周期：奇偶交替，且序列严格递增（reader 依赖 changed 检测）
                int firstEven = BitConverter.ToInt32(buffer, ShmLayout.OffCommitSequence);
                BeginCommitMethod.Invoke(writer, [new IntPtr(p)]);
                AssertCommitSequenceParity(buffer, odd: true, "second BeginCommit");

                CompleteCommitMethod.Invoke(writer, [new IntPtr(p)]);
                AssertCommitSequenceParity(buffer, odd: false, "second CompleteCommit");
                Assert.True(
                    BitConverter.ToInt32(buffer, ShmLayout.OffCommitSequence) > firstEven,
                    "commit sequence must strictly advance across cycles");
            }
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
    }

    // ---- InitializeHeader：保留旧 counter，发布空快照 ----

    [Fact]
    public unsafe void InitializeHeader_PreservesLegacyCounterFromV1Header()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            // 预置合法 v1 头：magic + version>=1，counter=12345，无 extension
            BitConverter.TryWriteBytes(
                buffer.AsSpan(ShmLayout.OffMagic, sizeof(int)),
                BrokerSharedMemory.MagicValue);
            BitConverter.TryWriteBytes(
                buffer.AsSpan(ShmLayout.OffVersion, sizeof(int)),
                ShmLayout.MinimumSupportedVersion);
            BitConverter.TryWriteBytes(
                buffer.AsSpan(ShmLayout.OffCounter, sizeof(int)),
                12345);

            fixed (byte* p = buffer)
            {
                PViewField.SetValue(writer, new IntPtr(p));
                InitializeHeaderMethod.Invoke(writer, null);
            }

            // 旧 counter 保留：老读者不会把初始化空快照当作新数据发布
            Assert.Equal(12345, BitConverter.ToInt32(buffer, ShmLayout.OffCounter));
            // 初始化不是一次传感器数据提交：提交序列为偶（已提交状态）
            AssertCommitSequenceParity(buffer, odd: false, "InitializeHeader");
            Assert.Equal(BrokerSharedMemory.MagicValue, BitConverter.ToInt32(buffer, ShmLayout.OffMagic));
            Assert.Equal(BrokerSharedMemory.MapVersion, BitConverter.ToInt32(buffer, ShmLayout.OffVersion));
            Assert.Equal(ShmLayout.ExtensionMagicValue, BitConverter.ToInt32(buffer, ShmLayout.OffExtensionMagic));
            Assert.Equal(0, BitConverter.ToInt32(buffer, ShmLayout.OffGpuCount));
            Assert.Equal(0, BitConverter.ToInt32(buffer, ShmLayout.OffSensorCount));
            Assert.Equal(-1.0, BitConverter.ToDouble(buffer, ShmLayout.OffCpuTemp));
            Assert.Equal("None", ReadString(buffer, ShmLayout.OffSource));
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
    }

    // ---- Write：完整 v2 快照发布 ----

    [Fact]
    public unsafe void Write_PublishesV2SnapshotAndAdvancesCounter()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            var gpus = new List<GpuReading>
            {
                new("RTX 4090", 55.0, 62.5, 4096.0, 8192.0),
                new("RTX 4090 #2", 60.0, 41.0, 2048.0, 8192.0),
            };
            var sensors = new List<SensorEntry>
            {
                new(0, "CPU Package", 42.5, "°C", 0),
                new(1, "CPU Total", 33.0, "%", 0),
            };

            fixed (byte* p = buffer)
            {
                PViewField.SetValue(writer, new IntPtr(p));
                InitializeHeaderMethod.Invoke(writer, null);

                // 初始化快照：counter 不推进、提交序列为偶
                Assert.Equal(0, BitConverter.ToInt32(buffer, ShmLayout.OffCounter));
                AssertCommitSequenceParity(buffer, odd: false, "initialization");

                writer.Write(36.5, "LHM", gpus, sensors);
                writer.Write(36.6, "LHM", gpus, sensors);
            }

            Assert.Equal(2, BitConverter.ToInt32(buffer, ShmLayout.OffCounter));
            AssertCommitSequenceParity(buffer, odd: false, "after writes");
            Assert.Equal(BrokerSharedMemory.MagicValue, BitConverter.ToInt32(buffer, ShmLayout.OffMagic));
            Assert.Equal(BrokerSharedMemory.MapVersion, BitConverter.ToInt32(buffer, ShmLayout.OffVersion));
            Assert.Equal(36.6, BitConverter.ToDouble(buffer, ShmLayout.OffCpuTemp));
            Assert.Equal("LHM", ReadString(buffer, ShmLayout.OffSource));
            Assert.Equal(2, BitConverter.ToInt32(buffer, ShmLayout.OffGpuCount));
            Assert.Equal(2, BitConverter.ToInt32(buffer, ShmLayout.OffSensorCount));
            Assert.True(BitConverter.ToInt64(buffer, ShmLayout.OffTimestamp) > 0);
            Assert.True(BitConverter.ToInt64(buffer, ShmLayout.OffMonotonicPublishMs) > 0);
            Assert.Equal(ShmLayout.ExtensionMagicValue, BitConverter.ToInt32(buffer, ShmLayout.OffExtensionMagic));

            // GPU 条目（72 字节/个）
            Assert.Equal("RTX 4090", ReadString(buffer, ShmLayout.OffGpuBase));
            Assert.Equal(55.0, BitConverter.ToDouble(buffer, ShmLayout.OffGpuBase + ShmLayout.GpuTempOff));
            Assert.Equal(62.5, BitConverter.ToDouble(buffer, ShmLayout.OffGpuBase + ShmLayout.GpuUsageOff));
            Assert.Equal(4096.0, BitConverter.ToDouble(buffer, ShmLayout.OffGpuBase + ShmLayout.GpuMemUsedOff));
            Assert.Equal(8192.0, BitConverter.ToDouble(buffer, ShmLayout.OffGpuBase + ShmLayout.GpuMemTotalOff));

            // 传感器条目（64 字节/个）
            Assert.Equal(0, BitConverter.ToInt32(buffer, ShmLayout.OffSensorBase + ShmLayout.SensorTagOff));
            Assert.Equal("CPU Package", ReadString(buffer, ShmLayout.OffSensorBase + ShmLayout.SensorNameOff));
            Assert.Equal(42.5, BitConverter.ToDouble(buffer, ShmLayout.OffSensorBase + ShmLayout.SensorValueOff));
            Assert.Equal("°C", ReadString(buffer, ShmLayout.OffSensorBase + ShmLayout.SensorUnitOff));
            Assert.Equal(0, BitConverter.ToInt32(buffer, ShmLayout.OffSensorBase + ShmLayout.SensorHardwareOff));
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
    }

    // ---- Dispose：空对象路径幂等（真实句柄生命周期不在此覆盖） ----

    [Fact]
    public void Dispose_IsIdempotentOnUninitializedInstance()
    {
        BrokerSharedMemory writer = NewUninitialized();

        writer.Dispose();
        writer.Dispose(); // 第二次调用直接返回，不抛异常
    }

    // ---- helpers ----

    private static BrokerSharedMemory NewUninitialized() =>
        (BrokerSharedMemory)RuntimeHelpers.GetUninitializedObject(WriterType);

    /// <summary>
    /// 复位 _pView 后 Dispose：让 ReleaseNativeResources 跳过全部 P/Invoke
    /// （句柄均为零），并确保 SuppressFinalize 正常执行，不残留 finalizer。
    /// </summary>
    private static void ResetPViewAndDispose(BrokerSharedMemory writer)
    {
        PViewField.SetValue(writer, IntPtr.Zero);
        writer.Dispose();
    }

    private static unsafe string InvokeWriteString(string? value, int maxBytes)
    {
        byte[] buffer = new byte[maxBytes];
        fixed (byte* p = buffer)
        {
            WriteStringMethod.Invoke(null, [new IntPtr(p), value, maxBytes]);
        }
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0');
    }

    private static void AssertCommitSequenceParity(byte[] buffer, bool odd, string stage)
    {
        int sequence = BitConverter.ToInt32(buffer, ShmLayout.OffCommitSequence);
        Assert.Equal(odd, (sequence & 1) == 1);
        Assert.True(sequence >= 0, $"{stage}: commit sequence must be non-negative, got {sequence}");
    }

    private static string ReadString(byte[] buffer, int offset)
    {
        int length = 0;
        while (offset + length < buffer.Length && buffer[offset + length] != 0)
            length++;
        return Encoding.UTF8.GetString(buffer, offset, length);
    }

    private static MethodInfo RequiredMethod(string name, BindingFlags flags) =>
        WriterType.GetMethod(name, flags)
        ?? throw new MissingMethodException(WriterType.FullName, name);

    private static FieldInfo RequiredField(string name) =>
        WriterType.GetField(name, InstancePrivate)
        ?? throw new MissingFieldException(WriterType.FullName, name);
}
