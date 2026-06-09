// Copyright (c) 2026 SysMonCmdPal
// 切换传感器 Dock 状态的命令

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>切换传感器 Dock 状态 — 点击添加/移除 Dock 栏</summary>
internal sealed partial class ToggleSensorCommand : InvokableCommand
{
    private readonly SensorReading _reading;
    private readonly bool _currentlyInDock;
    private readonly System.Action _onChanged;

    public ToggleSensorCommand(SensorReading reading, bool currentlyInDock, System.Action onChanged)
    {
        _reading = reading;
        _currentlyInDock = currentlyInDock;
        _onChanged = onChanged;
        Name = currentlyInDock ? $"从 Dock 移除 {reading.DisplayName}" : $"添加到 Dock {reading.DisplayName}";
    }

    public override CommandResult Invoke()
    {
        var svc = LhmSensorService.Instance;
        if (_currentlyInDock)
        {
            svc.RemoveSensorFromConfig(_reading.UniqueKey ?? "");
            _onChanged();
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = $"已移除: {_reading.DisplayName}",
                Result = CommandResult.KeepOpen(),
            });
        }
        else
        {
            svc.AddSensorToConfig(_reading);
            _onChanged();
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = $"已添加: {_reading.DisplayName} — 可在 Dock 栏固定",
                Result = CommandResult.KeepOpen(),
            });
        }
    }
}
