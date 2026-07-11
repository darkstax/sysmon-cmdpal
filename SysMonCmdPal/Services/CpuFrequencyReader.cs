// Copyright (c) 2026 SysMonCmdPal
// CPU 频率读取器 — 和任务管理器相同的算法
// 实际频率 = Processor Frequency (基础频率) × % Processor Performance / 100
// 只读 _Total 实例，显示全核心平均频率

using System.Diagnostics;

namespace SysMonCmdPal;

internal sealed class CpuFrequencyReader : IDisposable
{
    private PerformanceCounter? _freqCounter;   // Processor Frequency (基础频率 MHz)
    private PerformanceCounter? _perfCounter;   // % Processor Performance (相对基础频率的百分比)
    private bool _initAttempted;
    private bool _available;

    public bool IsAvailable
    {
        get { if (!_initAttempted) Init(); return _available; }
    }

    private void Init()
    {
        _initAttempted = true;
        try
        {
            _freqCounter = new PerformanceCounter(
                "Processor Information", "Processor Frequency", "_Total", true);
            _perfCounter = new PerformanceCounter(
                "Processor Information", "% Processor Performance", "_Total", true);
            // 预热（首次 NextValue 返回 0）
            _freqCounter.NextValue();
            _perfCounter.NextValue();
            _available = true;
            Debug.WriteLine("[CpuFreq] Initialized (Task Manager algorithm)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CpuFreq] Init failed: {ex.Message}");
        }
    }

    /// <summary>读取全核心平均实际频率 (MHz)。不可用返回 -1。</summary>
    public double ReadFrequency()
    {
        if (!IsAvailable || _freqCounter == null || _perfCounter == null) return -1;

        try
        {
            double baseFreq = _freqCounter.NextValue();
            double perfPct = _perfCounter.NextValue();
            if (baseFreq <= 0 || perfPct <= 0) return -1;
            // 实际频率 = 基础频率 × 性能百分比 / 100
            return Math.Round(baseFreq * perfPct / 100.0, 0);
        }
        catch
        {
            return -1;
        }
    }

    public void Dispose()
    {
        _freqCounter?.Dispose();
        _perfCounter?.Dispose();
    }
}
