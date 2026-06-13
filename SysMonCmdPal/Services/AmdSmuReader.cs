// Copyright (c) 2026 SysMonCmdPal
// AMD CPU 温度读取器 — 通过 PawnIO 加载 RyzenSMU.bin ring0 模块，
// 执行 SMU 邮箱协议读取 PM Table 中的 TctlTemp。
// 回退链位置: Phase 1 (AMD CPU 最高优先级)

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using PawnIO;

namespace SysMonCmdPal;

/// <summary>
/// 通过 PawnIO 加载 RyzenSMU.bin 执行 SMU 邮箱协议，从 PM Table 读取 TctlTemp。
/// 复刻 G-Helper 的 RyzenSmuService.GetPowerLimits() 实现。
/// 需要 PawnIO 驱动已安装。
/// </summary>
internal sealed class AmdSmuReader : IDisposable
{
    public static AmdSmuReader Instance { get; } = new();

    private readonly PawnIOWrapper _io = new();
    private bool _initAttempted;
    private bool _available;
    private uint _smuTableVersion;

    public bool IsAvailable
    {
        get
        {
            if (!_initAttempted) TryInit();
            return _available;
        }
    }

    private AmdSmuReader() { }

    private void TryInit()
    {
        _initAttempted = true;
        try
        {
            if (_io.Connect() != PawnIOWrapper.ConnectResult.OK)
            {
                SensorLogger.ForceLog("AmdSMU: PawnIO 设备不可用");
                return;
            }

            // 加载 RyzenSMU.bin ring0 模块（嵌入资源）
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetName().Name + ".Pawn.RyzenSMU.bin";
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null)
            {
                SensorLogger.ForceLog("AmdSMU: 嵌入资源 RyzenSMU.bin 未找到");
                return;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var data = ms.ToArray();

            if (!_io.LoadModule(data))
            {
                SensorLogger.ForceLog("AmdSMU: LoadModule 失败");
                return;
            }

            // 探测 SMU 版本（验证模块加载成功）
            ulong[] result = new ulong[1];
            if (!_io.Execute("ioctl_get_smu_version", null, result))
            {
                SensorLogger.ForceLog("AmdSMU: ioctl_get_smu_version 失败");
                return;
            }

            _smuTableVersion = (uint)result[0];
            _available = true;
            SensorLogger.ForceLog($"AmdSMU: 初始化成功, SMU 版本 = 0x{_smuTableVersion:X8}");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"AmdSMU 初始化异常: {ex.Message}");
        }
    }

    /// <summary>读取 CPU Tctl/Tdie 温度 (°C)，不可用返回 -1</summary>
    public double ReadCpuTemp()
    {
        if (!IsAvailable) return -1;

        try
        {
            // Step 1: 解析 PM Table 版本
            ulong[] resolveOut = new ulong[2];
            if (!_io.Execute("ioctl_resolve_pm_table", null, resolveOut))
                return -1;

            uint tableVersion = (uint)resolveOut[0];
            SensorLogger.ForceLog($"AmdSMU: PM Table 版本 = 0x{tableVersion:X6}");

            // Step 2: 刷新 PM Table（调用两次确保最新数据）
            _io.Execute("ioctl_update_pm_table", null, null);
            System.Threading.Thread.Sleep(100);
            if (!_io.Execute("ioctl_update_pm_table", null, null))
                return -1;
            System.Threading.Thread.Sleep(200);

            // Step 3: 读取 PM Table
            ulong[] words = new ulong[64];
            if (!_io.Execute("ioctl_read_pm_table", null, words))
                return -1;

            // Step 4: 解析 TctlTemp 的 float 索引
            ReadOnlySpan<float> floats = MemoryMarshal.Cast<ulong, float>(words);
            int thmIdx = GetTctlIndex(tableVersion);
            if (thmIdx < 0 || floats.Length <= thmIdx || floats[thmIdx] <= 0)
                return -1;

            double temp = floats[thmIdx];
            SensorLogger.ForceLog($"AmdSMU: TctlTemp = {temp:F1}°C (table=0x{tableVersion:X6}, idx={thmIdx})");

            return temp > 0 && temp < 150 ? temp : -1;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"AmdSMU 读取异常: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 返回 PM Table 中 tctl_temp 的 float 索引。
    /// 匹配 G-Helper RyzenSmu.cs GetTctlIndex() 实现。
    /// </summary>
    private static int GetTctlIndex(uint tableVersion)
    {
        uint hi = tableVersion >> 16;
        return hi switch
        {
            // Raven / Picasso / Dali (0x1Exxxx) / StrixHalo (0x64xxxx)
            0x1E or 0x64 => 22,
            // Renoir/Lucienne/Cezanne/Rembrandt/Phoenix/StrixPoint (各种版本)
            0x37 or 0x3F or 0x40 or 0x45 or 0x4C or 0x5D or 0x65 => 16,
            // DragonRange / Raphael / GraniteRidge (0x54xxxx, 0x62xxxx)
            0x54 or 0x62 => 10,
            _ => 16, // 安全兜底
        };
    }

    public void Dispose() => _io.Dispose();
}
