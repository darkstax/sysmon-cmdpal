// Copyright (c) 2026 SysMonCmdPal

using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal abstract class RefreshingContentPage : ContentPage, IDisposable
{
    private bool _subscribed;

    public void StartTimer()
    {
        if (_subscribed) return;
        _subscribed = true;
        DockBandRefreshCoordinator.Subscribe(Refresh);
        ThreadPool.QueueUserWorkItem(_ => Refresh());
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        DockBandRefreshCoordinator.Unsubscribe(Refresh);
        _subscribed = false;
    }

    protected abstract void RefreshContent();

    private void Refresh()
    {
        RefreshContent();
    }
}
