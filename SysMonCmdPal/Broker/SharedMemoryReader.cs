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

    private readonly Thread _readerThread;
    private readonly SharedMemorySnapshotReader _snapshotReader = new();
    private volatile bool _running;
    private bool _disposed;

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
        while (_running)
        {
            try
            {
                if (!EnsureConnected())
                {
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
                    ReadOnce();
                }
            }
            catch (FileNotFoundException)
            {
                Disconnect();
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
                BrokerPushReceiver.Instance.MarkUnavailable();
                UpdateDiagnostics(
                    connected: false,
                    protocolValid: false,
                    stalled: false,
                    mapName: mapName,
                    error: $"{ex.GetType().Name}: {ex.Message}");
            }

            Thread.Sleep(PollIntervalMilliseconds);
        }
    }

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

        bool stalled = IsStalled();
        if (status != StableReadStatus.Unstable || stalled)
            BrokerPushReceiver.Instance.MarkUnavailable();

        UpdateDiagnostics(
            connected: true,
            protocolValid: status == StableReadStatus.Unstable ? null : false,
            stalled: stalled,
            mapName: _connectedMapName,
            version: rawVersion,
            unstableReadDelta: retryCount,
            error: error);

        if (stalled)
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
        try
        {
            _readerThread.Join(3000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShmReader] Join failed: {ex.Message}");
        }

        Disconnect();
        BrokerPushReceiver.Instance.MarkUnavailable();
        UpdateDiagnostics(connected: false, stalled: false, mapName: "");
    }
}
