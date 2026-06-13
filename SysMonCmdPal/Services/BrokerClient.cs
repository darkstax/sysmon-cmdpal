// Copyright (c) 2026 SysMonCmdPal
// BrokerClient — 通过命名管道从 SysMonBroker（管理员进程）获取精准硬件数据。
// Broker 由计划任务以 HighestAvailable 运行，MSIX 应用以标准用户连接。
//
// 协议:
//   管道名: SysMonCmdPal
//   请求: [byte cmd]  1=AMD Tctl  2=Intel Package Temp  3=CPU Power
//   响应: [int32 status][double value][int32 source]
//     status: 0=OK  1=NotAvailable  2=Timeout
//     source: 1=SMU  2=MSR  0=None

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SysMonCmdPal;

public struct GpuData
{
    public string Name;
    public double Temperature;
    public double UsagePercent;
    public double MemoryUsedMB;
    public double MemoryTotalMB;
}

internal sealed class BrokerClient
{
    public static BrokerClient Instance { get; } = new();

    private const string PipeName = "SysMonCmdPal";
    private const int ConnectTimeoutMs = 2000;  // 本地管道通常很快，但 AppContainer 跨安全上下文连接需要更长时间
    private const int ReadTimeoutMs = 2000;
    private const int RetryIntervalMs = 30_000; // 30 秒后重试连接

    private readonly object _lock = new();
    private bool _isAvailable = true;  // 假设可用，首次失败后标记
    private DateTime _lastAttempt = DateTime.MinValue;

    private BrokerClient() { }

    /// <summary>Broker 进程是否可用（最近一次连接结果）</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                // 定期重置为可尝试状态
                if (!_isAvailable &&
                    (DateTime.UtcNow - _lastAttempt).TotalMilliseconds > RetryIntervalMs)
                {
                    _isAvailable = true;
                }
                return _isAvailable;
            }
        }
    }

    /// <summary>读取 AMD Tctl/Tdie 温度（SMU PM table，最准确）</summary>
    public double ReadAmdTctl() => Request(1);

    /// <summary>读取 Intel Package 温度（MSR IA32_THERM_STATUS）</summary>
    public double ReadIntelTemp() => Request(2);

    /// <summary>读取 CPU 功耗（SMU SPL 或 MSR RAPL）</summary>
    public double ReadCpuPower() => Request(3);

    private double Request(byte cmd)
    {
        lock (_lock)
        {
            if (!_isAvailable) return -1;
            _lastAttempt = DateTime.UtcNow;

            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.InOut);

                pipe.Connect(ConnectTimeoutMs);
                pipe.ReadMode = PipeTransmissionMode.Byte;

                // 发送命令
                pipe.Write(new[] { cmd }, 0, 1);

                // 读取响应: [int32 status][double value][int32 source] = 16 bytes
                var buf = new byte[16];
                int totalRead = 0;
                using var cts = new CancellationTokenSource(ReadTimeoutMs);
                while (totalRead < 16)
                {
                    int read = pipe.Read(buf, totalRead, 16 - totalRead);
                    if (read == 0)
                    {
                        MarkUnavailable();
                        return -1;
                    }
                    totalRead += read;
                    cts.Token.ThrowIfCancellationRequested();
                }

                int status = BitConverter.ToInt32(buf, 0);
                double value = BitConverter.ToDouble(buf, 4);
                // int source = BitConverter.ToInt32(buf, 12); // 可用于日志

                if (status == 0)
                    return value;

                // status 1=NotAvailable: Broker 已连接但该数据源不可用
                // status 2=Timeout: Broker 内部超时
                return -1;
            }
            catch (TimeoutException)
            {
                // Connect 超时 → Broker 未运行
                MarkUnavailable();
                return -1;
            }
            catch (IOException)
            {
                MarkUnavailable();
                return -1;
            }
            catch (Exception ex)
            {
                SensorLogger.ForceLog($"Broker request error (cmd={cmd}): {ex.Message}");
                return -1;
            }
        }
    }

    /// <summary>标记 Broker 不可用，触发 30 秒重试间隔</summary>
    public void MarkUnavailable()
    {
        lock (_lock)
        {
            _isAvailable = false;
            _lastAttempt = DateTime.UtcNow;
            SensorLogger.ForceLog("Broker unavailable, will retry in 30s");
        }
    }

    /// <summary>强制重置为可用状态（安装/启动 Broker 后调用）</summary>
    public void ResetAvailable()
    {
        lock (_lock)
        {
            _isAvailable = true;
            _lastAttempt = DateTime.UtcNow;
        }
    }

    /// <summary>检查 Broker 是否正在监听管道（快速探测，不影响状态）</summary>
    public bool Probe()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(500);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取所有 GPU 数据（cmd=4）。返回 null 表示 Broker 不可用，空列表表示无 GPU。</summary>
    public List<GpuData>? ReadAllGpus()
    {
        lock (_lock)
        {
            if (!_isAvailable) return null;
            _lastAttempt = DateTime.UtcNow;

            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                pipe.Connect(ConnectTimeoutMs);
                pipe.ReadMode = PipeTransmissionMode.Byte;

                pipe.Write(new byte[] { 4 }, 0, 1);

                var countBuf = new byte[4];
                int read = pipe.Read(countBuf, 0, 4);
                if (read != 4)
                {
                    MarkUnavailable();
                    return null;
                }
                int count = BitConverter.ToInt32(countBuf, 0);
                if (count <= 0)
                    return new List<GpuData>();

                var result = new List<GpuData>(count);
                for (int i = 0; i < count; i++)
                {
                    var nameLenBuf = new byte[4];
                    if (pipe.Read(nameLenBuf, 0, 4) != 4) break;
                    int nameLen = BitConverter.ToInt32(nameLenBuf, 0);
                    if (nameLen <= 0 || nameLen > 256) break;

                    var nameBuf = new byte[nameLen];
                    if (pipe.Read(nameBuf, 0, nameLen) != nameLen) break;
                    string name = Encoding.UTF8.GetString(nameBuf).TrimEnd('\0');

                    var dataBuf = new byte[32]; // 4 doubles
                    if (pipe.Read(dataBuf, 0, 32) != 32) break;

                    result.Add(new GpuData
                    {
                        Name = name,
                        Temperature = BitConverter.ToDouble(dataBuf, 0),
                        UsagePercent = BitConverter.ToDouble(dataBuf, 8),
                        MemoryUsedMB = BitConverter.ToDouble(dataBuf, 16),
                        MemoryTotalMB = BitConverter.ToDouble(dataBuf, 24),
                    });
                }
                return result;
            }
            catch (TimeoutException)
            {
                MarkUnavailable();
                return null;
            }
            catch (IOException)
            {
                MarkUnavailable();
                return null;
            }
            catch (Exception ex)
            {
                SensorLogger.ForceLog($"Broker ReadAllGpus error: {ex.Message}");
                MarkUnavailable();
                return null;
            }
        }
    }
}
