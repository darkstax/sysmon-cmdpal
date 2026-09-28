// SysMonCmdPal.Tests/BrokerSharedMemorySelfHealTests.cs
// P0-3（T1-3）写端自愈契约测试：BrokerSharedMemory.Write() 中途抛异常后，
// 「下一周期入口 rebase」必须做到三件事——
//   1. 不再误报 BrokerWriterConflictException（否则 Broker 一次写异常即自杀）；
//   2. 恢复动作不伪装成一次数据提交（counter 不因恢复而推进，保住读端新鲜度/回退语义）；
//   3. 身份守卫不削弱：外部实例接管 / extension magic 失效时仍照常抛 conflict，
//      绝不 clobber 真接管者的 map（Program.cs 的 FATAL 退出码 2 防线保留）。
//
// 测试策略与 BrokerSharedMemoryTests 一致：不构造真实实例（避免 Global\ 命名对象副作用），
// 用 RuntimeHelpers.GetUninitializedObject + 反射驱动 private 协议成员，在托管堆缓冲区
// 上验证逻辑。凡把 _pView 指向托管缓冲区的用例，退出前必须先复位 _pView 再 Dispose，
// 否则会对非 MapViewOfFile 地址调用 UnmapViewOfFile。
//
// 故障注入方式：传入含 null 元素的 gpus 列表 —— Write() 在 BeginCommit 置 odd、推进
// _counter/_lastUtcTimestamp/_lastMonotonicPublishMs 之后，才会执行到
// `gpus[i].Name`（null → NullReferenceException）。这精确复现「字段已推进、SHM 的
// OffCounter/OffMonotonicPublishMs 尚未落盘」的失配窗口，即 reviewer 实读代码指出的
// 那份机理（BeginCommit 在字段推进前、OffCounter 要到 CompleteCommit 才写）。

using System.Reflection;
using System.Runtime.CompilerServices;
using SysMonBroker.IPC;
using SysMonBroker.Sensors;
using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class BrokerSharedMemorySelfHealTests
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly Type WriterType = typeof(BrokerSharedMemory);
    private static readonly MethodInfo InitializeHeaderMethod =
        RequiredMethod("InitializeHeader");
    private static readonly MethodInfo RebaseMethod =
        RequiredMethod("RebaseAfterCorruption");
    private static readonly FieldInfo PViewField = RequiredField("_pView");
    private static readonly FieldInfo CounterField = RequiredField("_counter");
    private static readonly FieldInfo CommitSequenceField = RequiredField("_commitSequence");
    private static readonly FieldInfo UtcTimestampField = RequiredField("_lastUtcTimestamp");
    private static readonly FieldInfo MonotonicField = RequiredField("_lastMonotonicPublishMs");
    private static readonly FieldInfo CorruptedField = RequiredField("_writeCorrupted");

    private static readonly List<GpuReading> HealthyGpus =
    [
        new("RTX 4090", 55.0, 62.5, 4096.0, 8192.0),
    ];

    private static readonly List<SensorEntry> HealthySensors =
    [
        new(0, "CPU Package", 42.5, "°C", 0),
    ];

    // 触发 Write() 中途抛出（BeginCommit 之后、payload 落盘期间）的输入。
    private static List<GpuReading> PoisonedGpus() => [null!];

    // ---- 门禁 1：Write 抛后，下一周期不得误报冲突（直接断言不抛） ----

    [Fact]
    public unsafe void Write_ThrowsMidCycle_NextCycleRebases_WithoutReportingWriterConflict()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            fixed (byte* p = buffer)
            {
                PViewField.SetValue(writer, new IntPtr(p));
                InitializeHeaderMethod.Invoke(writer, null);

                writer.Write(30.0, "LHM", HealthyGpus, HealthySensors);
                Assert.Equal(1, ReadInt32(buffer, ShmLayout.OffCounter));

                // 注入中途异常
                Assert.Throws<NullReferenceException>(
                    () => writer.Write(31.0, "LHM", PoisonedGpus(), HealthySensors));

                // 失配窗口确实成立：提交序列停在 odd、SHM counter 仍是上次已提交值
                Assert.True((ReadInt32(buffer, ShmLayout.OffCommitSequence) & 1) == 1,
                    "failed cycle must leave the commit sequence odd (in flight)");
                Assert.Equal(1, ReadInt32(buffer, ShmLayout.OffCounter));
                Assert.True((bool)CorruptedField.GetValue(writer)!);

                // 本用例的核心门禁：下一周期正常写「不得」抛 BrokerWriterConflictException。
                // 只置标记不回滚、或 rebase 前移校验顺序的实现会在此自杀。
                writer.Write(32.0, "LHM", HealthyGpus, HealthySensors);

                Assert.False((bool)CorruptedField.GetValue(writer)!);
                Assert.Equal(2, ReadInt32(buffer, ShmLayout.OffCounter));
                Assert.Equal(32.0, ReadDouble(buffer, ShmLayout.OffCpuTemp));
                Assert.True((ReadInt32(buffer, ShmLayout.OffCommitSequence) & 1) == 0,
                    "recovered publish must commit (even sequence)");
            }
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
    }

    // ---- 门禁 2：恢复动作不是数据提交（counter 不推进），且后续真实发布仍单调 ----

    [Fact]
    public unsafe void RebaseAfterCorruption_DoesNotAdvanceCounter_SoRecoveryIsNotACommit()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            fixed (byte* p = buffer)
            {
                PViewField.SetValue(writer, new IntPtr(p));
                InitializeHeaderMethod.Invoke(writer, null);

                writer.Write(30.0, "LHM", HealthyGpus, HealthySensors);
                Assert.Throws<NullReferenceException>(
                    () => writer.Write(31.0, "LHM", PoisonedGpus(), HealthySensors));

                // 直接调用恢复例程：只对齐状态，不得推进 counter（同 InitializeHeader
                // 保留 previousCounter 的理由：推进会让读端把半写 payload 当新快照发布）
                int counterBeforeRebase = ReadInt32(buffer, ShmLayout.OffCounter);
                RebaseMethod.Invoke(writer, [new IntPtr(p)]);

                Assert.Equal(counterBeforeRebase, ReadInt32(buffer, ShmLayout.OffCounter));
                Assert.Equal(counterBeforeRebase, (int)CounterField.GetValue(writer)!);
                Assert.True((ReadInt32(buffer, ShmLayout.OffCommitSequence) & 1) == 0,
                    "rebase must withdraw the dangling odd marker");
                Assert.False((bool)CorruptedField.GetValue(writer)!);
                Assert.Equal(
                    ReadLong(buffer, ShmLayout.OffMonotonicPublishMs),
                    (long)MonotonicField.GetValue(writer)!);

                // 恢复后的一次真实发布才是唯一的 counter 前进
                writer.Write(32.0, "LHM", HealthyGpus, HealthySensors);
                Assert.Equal(counterBeforeRebase + 1, ReadInt32(buffer, ShmLayout.OffCounter));

                writer.Write(33.0, "LHM", HealthyGpus, HealthySensors);
                Assert.Equal(counterBeforeRebase + 2, ReadInt32(buffer, ShmLayout.OffCounter));
            }
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
    }

    // ---- 门禁 3a：外部实例身份接管 → 不得 rebase，仍抛 conflict 且不 clobber ----

    [Fact]
    public unsafe void Rebase_RefusesForeignInstanceId_StillThrowsWriterConflict()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            fixed (byte* p = buffer)
            {
                PViewField.SetValue(writer, new IntPtr(p));
                InitializeHeaderMethod.Invoke(writer, null);
                writer.Write(30.0, "LHM", HealthyGpus, HealthySensors);
                Assert.Throws<NullReferenceException>(
                    () => writer.Write(31.0, "LHM", PoisonedGpus(), HealthySensors));

                const ulong foreignInstance = 0xFEED_FACE_1234_5678UL;
                BitConverter.TryWriteBytes(
                    buffer.AsSpan(ShmLayout.OffInstanceId, sizeof(ulong)),
                    foreignInstance);

                Assert.Throws<BrokerWriterConflictException>(
                    () => writer.Write(32.0, "LHM", HealthyGpus, HealthySensors));

                // 真接管者的身份必须原样保留：自愈一个字节都不许写进别人的 map
                Assert.Equal(foreignInstance, ReadUInt64(buffer, ShmLayout.OffInstanceId));
                Assert.True((bool)CorruptedField.GetValue(writer)!,
                    "identity guard must keep the corruption marker; only a real rebase clears it");
            }
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
    }

    // ---- 门禁 3b：extension magic 失效 → 仍抛 conflict ----

    [Fact]
    public unsafe void Rebase_RefusesWhenExtensionMagicLost_StillThrowsWriterConflict()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            fixed (byte* p = buffer)
            {
                PViewField.SetValue(writer, new IntPtr(p));
                InitializeHeaderMethod.Invoke(writer, null);
                writer.Write(30.0, "LHM", HealthyGpus, HealthySensors);
                Assert.Throws<NullReferenceException>(
                    () => writer.Write(31.0, "LHM", PoisonedGpus(), HealthySensors));

                BitConverter.TryWriteBytes(
                    buffer.AsSpan(ShmLayout.OffExtensionMagic, sizeof(int)),
                    0);

                Assert.Throws<BrokerWriterConflictException>(
                    () => writer.Write(32.0, "LHM", HealthyGpus, HealthySensors));

                Assert.Equal(0, ReadInt32(buffer, ShmLayout.OffExtensionMagic));
                Assert.True((bool)CorruptedField.GetValue(writer)!);
            }
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
    }

    // ---- 门禁 4：旧实现的「回滚失败」残留态 → 入口 rebase 必须成功恢复 ----

    [Fact]
    public unsafe void EntryRebase_RecoversRolledBackDesyncState_WithoutThrowing()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            fixed (byte* p = buffer)
            {
                PViewField.SetValue(writer, new IntPtr(p));
                InitializeHeaderMethod.Invoke(writer, null);

                // 直接构造旧版手动回滚失败后的残留态：SHM 已提交 counter=3 / 序列停在 odd，
                // 而 in-memory 字段被回滚成另一套值（身份仍是我方）。
                WriteInt32(buffer, ShmLayout.OffCounter, 3);
                WriteInt32(buffer, ShmLayout.OffCommitSequence, 5);
                WriteLong(buffer, ShmLayout.OffMonotonicPublishMs, 10_001L);

                CounterField.SetValue(writer, 7);
                CommitSequenceField.SetValue(writer, 4);
                MonotonicField.SetValue(writer, 9_999L);
                UtcTimestampField.SetValue(writer, DateTime.UtcNow.AddSeconds(-30).Ticks);
                CorruptedField.SetValue(writer, true);

                // 不带失配硬跑会误报冲突；入口 rebase 必须先以 SHM 现状重建自身字段
                writer.Write(34.0, "LHM", HealthyGpus, HealthySensors);

                Assert.False((bool)CorruptedField.GetValue(writer)!);
                Assert.Equal(4, ReadInt32(buffer, ShmLayout.OffCounter));
                // 恢复发布本身是唯一一次提交：序列由 rebase 后的 even(4) 走 odd(5) → even(6)
                int committedSequence = ReadInt32(buffer, ShmLayout.OffCommitSequence);
                Assert.Equal(6, committedSequence);
                Assert.Equal(committedSequence, (int)CommitSequenceField.GetValue(writer)!);
                Assert.Equal(34.0, ReadDouble(buffer, ShmLayout.OffCpuTemp));
            }
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
    }

    // ---- 门禁 5：连续多次写失败不得级联恶化（每周期都能自愈） ----

    [Fact]
    public unsafe void RepeatedWriteFailures_Recover_EachCycle()
    {
        BrokerSharedMemory writer = NewUninitialized();
        try
        {
            byte[] buffer = new byte[BrokerSharedMemory.MapSize];
            fixed (byte* p = buffer)
            {
                PViewField.SetValue(writer, new IntPtr(p));
                InitializeHeaderMethod.Invoke(writer, null);

                writer.Write(30.0, "LHM", HealthyGpus, HealthySensors);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Assert.Throws<NullReferenceException>(
                        () => writer.Write(31.0, "LHM", PoisonedGpus(), HealthySensors));

                    // 每个后续周期都必须能自愈并正常发布（不抛 conflict）
                    writer.Write(32.0 + attempt, "LHM", HealthyGpus, HealthySensors);
                    Assert.False((bool)CorruptedField.GetValue(writer)!);
                    Assert.Equal(2 + attempt, ReadInt32(buffer, ShmLayout.OffCounter));
                }

                Assert.True((ReadInt32(buffer, ShmLayout.OffCommitSequence) & 1) == 0);
            }
        }
        finally
        {
            ResetPViewAndDispose(writer);
        }
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

    private static int ReadInt32(byte[] buffer, int offset) =>
        BitConverter.ToInt32(buffer, offset);

    private static long ReadLong(byte[] buffer, int offset) =>
        BitConverter.ToInt64(buffer, offset);

    private static ulong ReadUInt64(byte[] buffer, int offset) =>
        BitConverter.ToUInt64(buffer, offset);

    private static double ReadDouble(byte[] buffer, int offset) =>
        BitConverter.ToDouble(buffer, offset);

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, sizeof(int)), value);

    private static void WriteLong(byte[] buffer, int offset, long value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, sizeof(long)), value);

    private static MethodInfo RequiredMethod(string name) =>
        WriterType.GetMethod(name, InstancePrivate)
        ?? throw new MissingMethodException(WriterType.FullName, name);

    private static FieldInfo RequiredField(string name) =>
        WriterType.GetField(name, InstancePrivate)
        ?? throw new MissingFieldException(WriterType.FullName, name);
}
