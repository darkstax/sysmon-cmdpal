// Copyright (c) 2026 SysMonCmdPal
// Intel CPU 温度读取器 — 通过 PawnIO 读取 MSR 寄存器获取 DTS 温度。
// 回退链位置: Phase 1 (Intel CPU 最高优先级)

using System;
using System.IO;
using System.Reflection;
using PawnIO;

namespace SysMonCmdPal;

/// <summary>
/// 通过 PawnIO 读取 Intel CPU MSR 寄存器获取 DTS (Digital Thermal Sensor) 温度。
/// 需要 PawnIO 驱动已安装。
/// </summary>
internal sealed class IntelMsrReader : IDisposable
{
    public static IntelMsrReader Instance { get; } = new();

    private readonly PawnIOWrapper _io = new();
    private bool _initAttempted;
    private bool _available;
    private int _tjMax = 100; // 默认 100°C，会在初始化时从 MSR 读取

    // MSR registers
    private const uint MSR_IA32_THERM_STATUS = 0x19C;   // bits[22:16] = Digital Readout
    private const uint MSR_TEMPERATURE_TARGET = 0x1A2;  // bits[29:24] = TjMAX

    public bool IsAvailable
    {
        get
        {
            if (!_initAttempted) TryInit();
            return _available;
        }
    }

    private IntelMsrReader() { }

    private void TryInit()
    {
        _initAttempted = true;
        try
        {
            if (_io.Connect() != PawnIOWrapper.ConnectResult.OK)
            {
                SensorLogger.ForceLog("IntelMSR: PawnIO 设备不可用");
                return;
            }

            // 加载 IntelMSR.bin ring0 模块（嵌入资源）
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetName().Name + ".Pawn.IntelMSR.bin";
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null)
            {
                SensorLogger.ForceLog("IntelMSR: 嵌入资源 IntelMSR.bin 未找到");
                return;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var data = ms.ToArray();

            if (!_io.LoadModule(data))
            {
                SensorLogger.ForceLog("IntelMSR: LoadModule 失败");
                return;
            }

            // 读取 TjMAX
            if (ReadMsr(MSR_TEMPERATURE_TARGET, out ulong target))
            {
                int tj = (int)((target >> 24) & 0x7F);
                if (tj > 0 && tj < 150)
                {
                    _tjMax = tj;
                    SensorLogger.ForceLog($"IntelMSR: TjMAX = {_tjMax}°C");
                }
            }

            _available = true;
            SensorLogger.ForceLog("IntelMSR: 初始化成功");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"IntelMSR 初始化异常: {ex.Message}");
        }
    }

    /// <summary>读取 CPU Package 温度 (°C)，不可用返回 -1</summary>
    public double ReadPackageTemp()
    {
        if (!IsAvailable) return -1;

        try
        {
            if (!ReadMsr(MSR_IA32_THERM_STATUS, out ulong status))
                return -1;

            // bits[22:16] = Digital Readout (相对值)
            int digitalReadout = (int)((status >> 16) & 0x7F);
            if (digitalReadout == 0) return -1; // 无效读数

            double absTemp = _tjMax - digitalReadout;
            return absTemp;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"IntelMSR 读取异常: {ex.Message}");
            return -1;
        }
    }

    private bool ReadMsr(uint msr, out ulong value)
    {
        value = 0;
        var output = new ulong[1];
        if (!_io.Execute("ioctl_read_msr", new ulong[] { msr }, output))
            return false;
        value = output[0];
        return true;
    }

    public void Dispose() => _io.Dispose();
}
