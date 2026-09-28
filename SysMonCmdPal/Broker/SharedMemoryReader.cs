// SysMonCmdPal/Broker/SharedMemoryReader.cs
// Broker shared-memory thread, connection, and lifetime management.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace SysMonCmdPal.Broker;

/// <summary>
/// Connects to Broker shared memory and publishes validated sensor snapshots.
/// Supports the current global map and legacy local maps during rolling upgrades.
/// </summary>
public sealed partial class SharedMemoryReader : IDisposable
{
    private const int PollIntervalMilliseconds = 1000;
    private const int MaxBackoffMilliseconds = 30_000;
    private const int SleepGranularityMilliseconds = 250;

    private readonly Thread _readerThread;
    private readonly SharedMemorySnapshotReader _snapshotReader = new();
    private volatile bool _running;
    private bool _disposed;

    // 仅由 reader 线程访问：连续连接失败次数（退避状态）。
    private int _consecutiveFailures;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private int _viewSize;
    private string _connectedMapName = "";

    public SharedMemoryReader()
    {
        _running = true;
        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "ShmReader",
        };
        _readerThread.Start();
    }

    private void ReaderLoop()
    {
        try
        {
            while (_running)
            {
                int sleepMs = PollIntervalMilliseconds;
                try
                {
                    if (!EnsureConnected())
                    {
                        sleepMs = RecordConnectionFailure();
                        BrokerPushReceiver.Instance.MarkUnavailable();
                        UpdateDiagnostics(
                            connected: false,
                            protocolValid: false,
                            stalled: false,
                            mapName: "",
                            error: "");
                    }
                    else
                    {
                        ResetConnectionFailures();
                        ReadOnce();
                    }
                }
                catch (FileNotFoundException)
                {
                    Disconnect();
                    sleepMs = RecordConnectionFailure();
                    BrokerPushReceiver.Instance.MarkUnavailable();
                    UpdateDiagnostics(
                        connected: false,
                        protocolValid: false,
                        stalled: false,
                        mapName: "",
                        error: "");
                }
                catch (Exception ex)
                {
                    string mapName = _connectedMapName;
                    Disconnect();
                    sleepMs = RecordConnectionFailure();
                    BrokerPushReceiver.Instance.MarkUnavailable();
                    UpdateDiagnostics(
                        connected: false,
                        protocolValid: false,
                        stalled: false,
                        mapName: mapName,
                        error: $"{ex.GetType().Name}: {ex.Message}");
                }

                // 分段睡眠：退避可达 30s，分段让线程在停止后快速退出并清理。
                int remaining = sleepMs;
                while (_running && remaining > 0)
                {
                    Thread.Sleep(Math.Min(remaining, SleepGranularityMilliseconds));
                    remaining -= SleepGranularityMilliseconds;
                }
            }
        }
        finally
        {
            // 线程退出路径统一清理：无论因停止请求还是异常离开循环，
            // 都释放句柄，关闭 Dispose Join 超时后的句柄泄漏窗口。
            Disconnect();
            BrokerPushReceiver.Instance.MarkUnavailable();
            UpdateDiagnostics(connected: false, stalled: false, mapName: "");
        }
    }

    /// <summary>记录一次连续连接失败，返回下次重试的指数退避延迟（1s→2s→4s→…上限 30s）。</summary>
    private int RecordConnectionFailure()
    {
        _consecutiveFailures++;
        int delay = PollIntervalMilliseconds << Math.Min(_consecutiveFailures - 1, 5);
        return Math.Min(delay, MaxBackoffMilliseconds);
    }

    /// <summary>连接成功即复位退避计数。</summary>
    private void ResetConnectionFailures() => _consecutiveFailures = 0;

    private bool EnsureConnected()
    {
        if (_mmf != null && _accessor != null)
            return true;

        Exception? firstOpenError = null;
        foreach (string mapName in ShmLayout.MapNames)
        {
            MemoryMappedFile? mmf = null;
            MemoryMappedViewAccessor? accessor = null;
            try
            {
                mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
                accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                if (accessor.Capacity > int.MaxValue)
                    throw new InvalidDataException("Shared memory view is too large");

                _mmf = mmf;
                _accessor = accessor;
                _viewSize = (int)accessor.Capacity;
                _connectedMapName = mapName;

                UpdateDiagnostics(
                    connected: true,
                    stalled: false,
                    mapName: mapName,
                    connectionDelta: 1);
                return true;
            }
            catch (FileNotFoundException)
            {
                accessor?.Dispose();
                mmf?.Dispose();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                accessor?.Dispose();
                mmf?.Dispose();
                firstOpenError ??= ex;
            }
        }

        if (firstOpenError != null)
            throw firstOpenError;

        return false;
    }

    private void ReadOnce()
    {
        MemoryMappedViewAccessor accessor = _accessor
            ?? throw new InvalidOperationException("Shared memory is not connected");
        StableReadStatus status = _snapshotReader.TryRead(
            accessor,
            _viewSize,
            out StableSnapshot stableSnapshot,
            out int rawVersion,
            out int retryCount,
            out string error);

        if (status == StableReadStatus.Success)
        {
            ProcessStableSnapshot(stableSnapshot, retryCount);
            return;
        }

        // 三条判据分离（阈值真实关系见 SharedMemoryReader.Health.cs 头部注释）：
        //   stalled        = 5s 单次 raw 观察（只写进 Diagnostics.IsStalled，阈值不变）；
        //   stallConfirmed = 15s 时间基确认窗口（T2-4），决定是否 MarkUnavailable/Disconnect。
        // 原计数式去抖（连续 N 次观察）已废弃：轮询间隔是 1s 而非 5s，阈值 2→3 只把确认
        // 从 6s 推到 7s，覆盖不到 Broker 合法静默上界 ≈15s，高负载下仍会提前断连。
        bool stalled = IsStalled();
        bool stallConfirmed = IsStallConfirmed();
        if (status != StableReadStatus.Unstable || stallConfirmed)
            BrokerPushReceiver.Instance.MarkUnavailable();

        UpdateDiagnostics(
            connected: true,
            protocolValid: status == StableReadStatus.Unstable ? null : false,
            stalled: stalled,
            mapName: _connectedMapName,
            version: rawVersion,
            unstableReadDelta: retryCount,
            error: error);

        if (stallConfirmed)
            Disconnect();
    }

    private void Disconnect()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
        _accessor = null;
        _mmf = null;
        _viewSize = 0;
        _connectedMapName = "";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _running = false;

        bool joined = false;
        try
        {
            joined = _readerThread.Join(3000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShmReader] Join failed: {ex.Message}");
        }

        if (joined)
        {
            // 线程已退出并在退出路径完成清理；这里仅做幂等收尾，
            // 此时不存在与线程的并发，Disconnect 是安全的。
            Disconnect();
            BrokerPushReceiver.Instance.MarkUnavailable();
            UpdateDiagnostics(connected: false, stalled: false, mapName: "");
        }
        // Join 超时：线程仍存活，绝不能在此处 Disconnect——否则线程可能在
        // Disconnect 之后重新 OpenExisting 并赋值 _mmf/_accessor，无人释放
        // 造成句柄泄漏。改为由线程在下一轮退出前（ReaderLoop finally）统一清理。
        // 重复 Dispose 由 _disposed 标志保证幂等。
    }
}
