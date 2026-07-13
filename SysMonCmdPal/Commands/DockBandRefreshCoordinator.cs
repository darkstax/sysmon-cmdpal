// Copyright (c) 2026 SysMonCmdPal

using System;
using System.Collections.Generic;
using System.Threading;
using System.Timers;

namespace SysMonCmdPal;

/// <summary>
/// Singleton timer shared by all dock bands. Calls SystemInfoService.Refresh()
/// once per tick, then notifies all subscribers to read the cached snapshot.
/// </summary>
internal static class DockBandRefreshCoordinator
{
    private static readonly List<Action> _subscribers = [];
    private static readonly object _lock = new();
    private static System.Timers.Timer? _timer;
    private static int _refCount;
    private static int _isRefreshing; // 0=idle, 1=refreshing

    public static void Subscribe(Action refresh)
    {
        lock (_lock)
        {
            if (_subscribers.Contains(refresh))
                return;

            _subscribers.Add(refresh);
            _refCount++;
            if (_timer == null)
            {
                _timer = new System.Timers.Timer(1000) { AutoReset = true };
                _timer.Elapsed += OnTick;
                _timer.Start();
            }
        }
    }

    public static void Unsubscribe(Action refresh)
    {
        lock (_lock)
        {
            if (!_subscribers.Remove(refresh))
                return;

            _refCount--;
            if (_refCount <= 0 && _timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            _subscribers.Clear();
            _refCount = 0;
        }
    }

    private static void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (Interlocked.Exchange(ref _isRefreshing, 1) != 0)
            return;

        try
        {
            SystemInfoService.Instance.Refresh();
            Action[] snapshot;
            lock (_lock) { snapshot = _subscribers.ToArray(); }
            foreach (var action in snapshot)
            {
                try { action(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SysMon] DockBand refresh subscriber: {ex.Message}"); }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }
}
