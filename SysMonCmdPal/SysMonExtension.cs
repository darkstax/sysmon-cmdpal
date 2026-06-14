// Copyright (c) 2026 SysMonCmdPal
// SysMonExtension — IExtension + ISysMonBrokerPush 双接口实现
// Broker 通过 CoCreateInstance(本 CLSID) 再 QI ISysMonBrokerPush 推送数据

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

[ComVisible(true)]
[Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
[ComDefaultInterface(typeof(IExtension))]
public sealed partial class SysMonExtension : IExtension, ISysMonBrokerPush, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;
    private readonly SysMonCommandsProvider _provider = new();

    public SysMonExtension(ManualResetEvent extensionDisposedEvent)
    {
        _extensionDisposedEvent = extensionDisposedEvent;
    }

    // ===== IExtension =====

    public object GetProvider(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Commands => _provider,
            _ => null!
        };
    }

    // ===== ISysMonBrokerPush — 委托给 BrokerPushReceiver 单例 =====

    public void PushCpuTemp(double celsius, string source)
    {
        BrokerPushReceiver.Instance.PushCpuTemp(celsius, source);
    }

    public void PushGpuData(int gpuIndex, string name, double tempCelsius,
        double usagePercent, double memUsedMB, double memTotalMB)
    {
        BrokerPushReceiver.Instance.PushGpuData(gpuIndex, name, tempCelsius,
            usagePercent, memUsedMB, memTotalMB);
    }

    public void Ping()
    {
        BrokerPushReceiver.Instance.Ping();
    }

    // ===== IDisposable =====

    public void Dispose()
    {
        _provider.Dispose();
        _extensionDisposedEvent.Set();
    }
}
