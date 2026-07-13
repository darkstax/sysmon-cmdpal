using System;
using System.Diagnostics;

namespace SysMonCmdPal;

internal sealed class CpuUsageReader
{
    private PerformanceCounter? _counter;
    private readonly object _lock = new();

    public CpuUsageReader()
    {
        try { _counter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] CPU counter init failed: {ex.Message}"); }
    }

    public double Read()
    {
        try
        {
            PerformanceCounter? counter;
            lock (_lock)
            {
                if (_counter is null)
                {
                    try { _counter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
                    catch (Exception ex) { Debug.WriteLine($"[SysMon] CPU counter re-init failed: {ex.Message}"); return 0; }
                }
                counter = _counter;
            }

            return Math.Round(counter.NextValue(), 1);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadCpuUsage: {ex.Message}");
            lock (_lock)
            {
                _counter?.Dispose();
                _counter = null;
            }
            return 0;
        }
    }
}
