// Copyright (c) 2026 SysMonCmdPal
// T1-2 (路线图 P0-2)：详情页惰性壳 —— DeferredListPage / DeferredContentPage。
//
// SysMonMainPage 的构造函数不再 new 8 个详情页，只创建这些轻量壳。真实详情页在宿主
// 首次要求页面数据时(ListPage.GetItems / LoadMore / ContentPage.GetContent)
// 才通过 Lazy<T>(ExecutionAndPublication 线程安全默认模式)创建，之后复用同一实例。
//
// 宿主契约桥接 —— 必做（SDK 实读 ListPage.cs:11 / ContentPage.cs:11：ItemsChanged 与
// PropChanged 都是普通字段事件，只触发订阅在“被调用对象”上的处理器）：
// 宿主订阅的是壳，而 SensorListPage.OnRefresh / SensorCategoryPage.RequestRefresh 调用的
// RaiseItemsChanged() 发生在内层真实页上。若壳不桥接，传感器列表每秒刷新会静默失效
// (无异常、无日志、单测结构上抓不到)。因此在 Lazy 工厂内、返回内层实例之前立即桥接
// 这两个事件（方向恒为 内层 → 壳）—— 必须先于任何 GetItems()/GetContent() 调用，
// 因为 EnsureSubscribed 发生在内层 GetItems() 内部，桥接晚一步就永久错过首次订阅时机。
//
// 成员转发 —— 壳覆盖宿主会读取的全部成员：
//   Command: Name/Id/Icon；Page: Title/IsLoading/AccentColor；
//   ListPage: SearchText/PlaceholderText/ShowDetails/HasMoreItems/Filters/
//             GridProperties/EmptyContent/GetItems()/LoadMore()/ItemsChanged；
//   ContentPage: Details/Commands/GetContent()/ItemsChanged（ContentPage 无 SearchText，
//             该成员只在 ListPage 侧转发）。
// 未创建时读写落在壳自身：Name/Title/Icon 由 SysMonMainPage 预置为与内层详情页 ctor
// 完全一致的镜像值（宿主进入页面前就会读取页头），其余成员内层 ctor 不设非默认值，
// 壳默认值即等价。创建后读写直达内层；创建前宿主写入的 SearchText 在创建瞬间补灌给内层。
//
// 壳内不引入任何 Timer/Thread，也不新增订阅；刷新只经内层页自身的
// RefreshingContentPage.StartTimer / SensorListPage.EnsureSubscribed
// → DockBandRefreshCoordinator。Dispose 只释放已创建的实例(Lazy.IsValueCreated)，
// 未创建的不触发实例化 —— 退出路径不会把惰性又变回预创建。

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>惰性 ListPage 壳（磁盘 / GPU / 传感器列表详情页）。</summary>
internal sealed partial class DeferredListPage : ListPage, IDisposable
{
    private readonly Lazy<ListPage> _lazy;
    private bool _disposed;

    public DeferredListPage(Func<ListPage> factory, string name, string title, string icon)
    {
        _lazy = new Lazy<ListPage>(() => CreateAndBridge(factory()));
        Name = name;
        Title = title;
        Icon = new IconInfo(icon);
    }

    private ListPage CreateAndBridge(ListPage inner)
    {
        // 先桥接（方向 内层 → 壳）再返回：内层 GetItems() 里 EnsureSubscribed →
        // coordinator 回调 → RaiseItemsChanged 才不会打到没有订阅者的对象上。
        inner.ItemsChanged += (_, e) => RaiseItemsChanged(e?.TotalItems ?? -1);
        inner.PropChanged += (_, e) => OnPropertyChanged(e?.PropertyName);

        // 创建前宿主写入的 SearchText 补灌给内层（宿主在用户改搜索词时写入；
        // 不转发则按词过滤失效）。
        string pending = base.SearchText;
        if (!string.IsNullOrEmpty(pending) && inner.SearchText != pending)
            inner.SearchText = pending;

        return inner;
    }

    /// <summary>内层实例是否已创建（绝不触发创建）。</summary>
    internal bool IsCreated => _lazy.IsValueCreated;

    /// <summary>已创建时返回内层实例，否则 null —— 绝不触发创建（测试/诊断缝隙）。</summary>
    internal ListPage? CreatedPageOrNull => _lazy.IsValueCreated ? _lazy.Value : null;

    /// <summary>壳在页头预置的镜像值（守护用例用它比对内层详情页 ctor 的赋值）。</summary>
    internal string PresetName => base.Name;
    internal string PresetTitle => base.Title;
    internal string? PresetIconGlyph => base.Icon?.Light?.Icon;

    public override IListItem[] GetItems()
    {
        var inner = EnsureCreatedAndNotify();
        return inner.GetItems();
    }

    public override void LoadMore()
    {
        var inner = EnsureCreatedAndNotify();
        inner.LoadMore();
    }

    /// <summary>
    /// 首次创建发生在宿主真正进入本页时。工厂返回瞬间 Lazy.IsValueCreated 仍为 false
    /// （执行中），内层 ctor 里的 Title/Commands 赋值经桥接通知壳时读到的还是壳镜像值；
    /// 因此创建完成后在工厂之外统一重发 PropertyChanged，促使宿主重读已转发到内层的真实值。
    /// </summary>
    private ListPage EnsureCreatedAndNotify()
    {
        bool first = !IsCreated;
        var inner = _lazy.Value;
        if (first) NotifyForwarded();
        return inner;
    }

    private void NotifyForwarded()
    {
        foreach (string prop in ForwardedProps)
            OnPropertyChanged(prop);
    }

    private static readonly string[] ForwardedProps =
    [
        nameof(Name), nameof(Id), nameof(Icon), nameof(Title), nameof(IsLoading),
        nameof(AccentColor), nameof(SearchText), nameof(PlaceholderText),
        nameof(ShowDetails), nameof(HasMoreItems), nameof(Filters),
        nameof(GridProperties), nameof(EmptyContent),
    ];

    public override string Name
    {
        get => IsCreated ? _lazy.Value.Name : base.Name;
        set { if (IsCreated) _lazy.Value.Name = value; else base.Name = value; }
    }

    public override string Id
    {
        get => IsCreated ? _lazy.Value.Id : base.Id;
        set { if (IsCreated) _lazy.Value.Id = value; else base.Id = value; }
    }

    public override IconInfo Icon
    {
        get => IsCreated ? _lazy.Value.Icon : base.Icon;
        set { if (IsCreated) _lazy.Value.Icon = value; else base.Icon = value; }
    }

    public override string Title
    {
        get => IsCreated ? _lazy.Value.Title : base.Title;
        set { if (IsCreated) _lazy.Value.Title = value; else base.Title = value; }
    }

    public override bool IsLoading
    {
        get => IsCreated ? _lazy.Value.IsLoading : base.IsLoading;
        set { if (IsCreated) _lazy.Value.IsLoading = value; else base.IsLoading = value; }
    }

    public override OptionalColor AccentColor
    {
        get => IsCreated ? _lazy.Value.AccentColor : base.AccentColor;
        set { if (IsCreated) _lazy.Value.AccentColor = value; else base.AccentColor = value; }
    }

    public override string SearchText
    {
        get => IsCreated ? _lazy.Value.SearchText : base.SearchText;
        set { if (IsCreated) _lazy.Value.SearchText = value; else base.SearchText = value; }
    }

    public override string PlaceholderText
    {
        get => IsCreated ? _lazy.Value.PlaceholderText : base.PlaceholderText;
        set { if (IsCreated) _lazy.Value.PlaceholderText = value; else base.PlaceholderText = value; }
    }

    public override bool ShowDetails
    {
        get => IsCreated ? _lazy.Value.ShowDetails : base.ShowDetails;
        set { if (IsCreated) _lazy.Value.ShowDetails = value; else base.ShowDetails = value; }
    }

    public override bool HasMoreItems
    {
        get => IsCreated ? _lazy.Value.HasMoreItems : base.HasMoreItems;
        set { if (IsCreated) _lazy.Value.HasMoreItems = value; else base.HasMoreItems = value; }
    }

    public override IFilters? Filters
    {
        get => IsCreated ? _lazy.Value.Filters : base.Filters;
        set { if (IsCreated) _lazy.Value.Filters = value; else base.Filters = value; }
    }

    public override IGridProperties? GridProperties
    {
        get => IsCreated ? _lazy.Value.GridProperties : base.GridProperties;
        set { if (IsCreated) _lazy.Value.GridProperties = value; else base.GridProperties = value; }
    }

    public override ICommandItem? EmptyContent
    {
        get => IsCreated ? _lazy.Value.EmptyContent : base.EmptyContent;
        set { if (IsCreated) _lazy.Value.EmptyContent = value; else base.EmptyContent = value; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 只释放已创建实例；未创建的不触发 Lazy。
        if (IsCreated && _lazy.Value is IDisposable d)
            d.Dispose();
    }
}

/// <summary>惰性 ContentPage 壳（CPU / 内存 / 网络 / 电池 / Broker 诊断详情页）。</summary>
internal sealed partial class DeferredContentPage : ContentPage, IDisposable
{
    private readonly Lazy<ContentPage> _lazy;
    private bool _disposed;

    public DeferredContentPage(Func<ContentPage> factory, string name, string title, string icon)
    {
        _lazy = new Lazy<ContentPage>(() => CreateAndBridge(factory()));
        Name = name;
        Title = title;
        Icon = new IconInfo(icon);
    }

    private ContentPage CreateAndBridge(ContentPage inner)
    {
        inner.ItemsChanged += (_, e) => RaiseItemsChanged(e?.TotalItems ?? -1);
        inner.PropChanged += (_, e) => OnPropertyChanged(e?.PropertyName);
        return inner;
    }

    internal bool IsCreated => _lazy.IsValueCreated;

    internal ContentPage? CreatedPageOrNull => _lazy.IsValueCreated ? _lazy.Value : null;

    internal string PresetName => base.Name;
    internal string PresetTitle => base.Title;
    internal string? PresetIconGlyph => base.Icon?.Light?.Icon;

    private void NotifyForwarded()
    {
        foreach (string prop in ForwardedProps)
            OnPropertyChanged(prop);
    }

    private static readonly string[] ForwardedProps =
    [
        nameof(Name), nameof(Id), nameof(Icon), nameof(Title), nameof(IsLoading),
        nameof(AccentColor), nameof(Details), nameof(Commands),
    ];

    public override IContent[] GetContent()
    {
        bool first = !IsCreated;
        var inner = _lazy.Value;
        if (first) NotifyForwarded();
        return inner.GetContent();
    }

    /// <summary>
    /// 上下文命令（返回 / 复制当前指标）保持惰性读取语义：未创建时读壳值（空），
    /// 绝不因宿主预读而创建页面。内层页 ctor 里对 Commands 的赋值经 PropChanged 桥接，
    /// 而 GetContent() 创建完成后会统一重发 PropertyChanged —— 宿主据此重读，
    /// 届时读到的就是转发到内层的真实命令集。
    /// </summary>
    public override IContextItem[] Commands
    {
        get => IsCreated ? _lazy.Value.Commands : base.Commands;
        set { if (IsCreated) _lazy.Value.Commands = value; else base.Commands = value; }
    }

    public override IDetails? Details
    {
        get => IsCreated ? _lazy.Value.Details : base.Details;
        set { if (IsCreated) _lazy.Value.Details = value; else base.Details = value; }
    }

    public override string Name
    {
        get => IsCreated ? _lazy.Value.Name : base.Name;
        set { if (IsCreated) _lazy.Value.Name = value; else base.Name = value; }
    }

    public override string Id
    {
        get => IsCreated ? _lazy.Value.Id : base.Id;
        set { if (IsCreated) _lazy.Value.Id = value; else base.Id = value; }
    }

    public override IconInfo Icon
    {
        get => IsCreated ? _lazy.Value.Icon : base.Icon;
        set { if (IsCreated) _lazy.Value.Icon = value; else base.Icon = value; }
    }

    public override string Title
    {
        get => IsCreated ? _lazy.Value.Title : base.Title;
        set { if (IsCreated) _lazy.Value.Title = value; else base.Title = value; }
    }

    public override bool IsLoading
    {
        get => IsCreated ? _lazy.Value.IsLoading : base.IsLoading;
        set { if (IsCreated) _lazy.Value.IsLoading = value; else base.IsLoading = value; }
    }

    public override OptionalColor AccentColor
    {
        get => IsCreated ? _lazy.Value.AccentColor : base.AccentColor;
        set { if (IsCreated) _lazy.Value.AccentColor = value; else base.AccentColor = value; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsCreated && _lazy.Value is IDisposable d)
            d.Dispose();
    }
}
