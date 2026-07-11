// Copyright (c) 2026 SysMonCmdPal
// SysMonExtension — IExtension 接口实现

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

[ComVisible(true)]
[Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
[ComDefaultInterface(typeof(IExtension))]
public sealed partial class SysMonExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;
    private readonly SysMonCommandsProvider _provider = new();
    private readonly SharedMemoryReader _shmReader = new();

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

    // ===== IDisposable =====

    public void Dispose()
    {
        _shmReader.Dispose();
        _provider.Dispose();
        _extensionDisposedEvent.Set();
    }
}
