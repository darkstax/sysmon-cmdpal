// Copyright (c) 2026 SysMonCmdPal
// CmdPal settings surface backed by the existing settings.json file.

using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class SysMonSettingsManager : JsonSettingsManager, ICommandSettings
{
    private readonly SysMonSettingsContentPage _settingsPage;
    private readonly ChoiceSetSetting _nicSelectionSetting;

    public const string BtopPathKey = "btopPath";

    /// <summary>「主网卡」选择键。值 = "auto" 或 NetworkInterface.Id（分号分隔可容多值）。</summary>
    public const string SelectedNicsKey = NetworkInterfaceClassifier.SelectedNicsKey;

    private static readonly HashSet<string> ManagedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        BtopPathKey,
        SelectedNicsKey,
    };

    public SysMonSettingsManager()
    {
        FilePath = SensorChainConfig.ConfigPath;

        Settings.Add(new TextSetting(
            BtopPathKey,
            Loc.Get("Settings.BtopPathLabel"),
            Loc.Get("Settings.BtopPathDescription"),
            string.Empty)
        {
            Placeholder = Loc.Get("Settings.BtopPathPlaceholder"),
        });

        // 占位注册：网卡是运行时动态集合，真实 Choices 由 RefreshNicChoices() 在每次
        // 进入设置页前重建（Settings.Add 只在 ctor 执行一次，故不能在此枚举）。
        _nicSelectionSetting = new ChoiceSetSetting(
            SelectedNicsKey,
            Loc.Get("Settings.NicSelectionLabel"),
            Loc.Get("Settings.NicSelectionDescription"),
            [new ChoiceSetSetting.Choice(
                Loc.Get("Settings.NicAutoChoice"),
                NetworkInterfaceClassifier.AutoSelectionValue)])
        {
            // 选中的网卡消失后值不在 Choices 里 ⇒ 忽略该次更新（不把失效 GUID 写回）
            IgnoreUnknownValue = true,
        };
        Settings.Add(_nicSelectionSetting);

        LoadSettings();
        RestorePersistedNicSelection();
        _lastNicSelection = _nicSelectionSetting.Value;
        Settings.SettingsChanged += OnSettingsChanged;

        _settingsPage = new SysMonSettingsContentPage(
            Settings,
            new BrokerInstallController(),
            RefreshNicChoices);
    }

    /// <summary>
    /// 「主网卡」选择**实际发生变化**时触发。
    ///
    /// 为什么不直接让消费方订阅 <c>Settings.SettingsChanged</c>：那个事件在保存任意设置项
    /// （如 btopPath）时都会触发，而失效处理会清空接口缓存并重新播种网络基线 ——
    /// 保存无关设置却重置网络基线，会让速率读数出现无谓的跳变。
    /// 这里只在本键值真的变了才通知。
    /// </summary>
    internal event EventHandler? NicSelectionChanged;

    private string? _lastNicSelection;

    private void OnSettingsChanged(object? sender, Microsoft.CommandPalette.Extensions.Toolkit.Settings e)
    {
        SaveSettings();
        NotifyNicSelectionIfChanged();
    }

    /// <summary>对比当前值与上次已通知值，只有真正变化才触发 <see cref="NicSelectionChanged"/>。</summary>
    private void NotifyNicSelectionIfChanged()
    {
        var current = _nicSelectionSetting.Value;
        if (string.Equals(current, _lastNicSelection, StringComparison.OrdinalIgnoreCase))
            return;

        _lastNicSelection = current;
        NicSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public IContentPage SettingsPage => _settingsPage;

    /// <summary>测试接缝：暴露「主网卡」设置项以便断言持久化/选项重建行为。</summary>
    internal ChoiceSetSetting NicSelectionSetting => _nicSelectionSetting;

    /// <summary>
    /// 恢复持久化的网卡选择。
    ///
    /// 必要性：<see cref="ChoiceSetSetting.Update"/> 在值不在 Choices 里且
    /// IgnoreUnknownValue=true 时**静默丢弃**该值。而 LoadSettings() 发生在 ctor 里、
    /// 此时 Choices 只含占位 "auto"（真实列表要等首次进入设置页才枚举）⇒
    /// 若不补这一步，每次启动都会把用户选好的网卡悄悄重置成 auto。
    ///
    /// 注意：必须**直接读文件**，不能读 _nicSelectionSetting.Value ——
    /// LoadSettings 已经把该值丢掉了（正是本方法要修的 bug）。
    /// </summary>
    private void RestorePersistedNicSelection()
    {
        string? saved;
        try
        {
            saved = (ReadSettingsObject(FilePath) as JsonObject)
                ?[SelectedNicsKey]?.GetValue<string>();
        }
        catch
        {
            saved = null;
        }

        var selected = NetworkInterfaceClassifier.ParseSelectedGuids(saved);
        if (selected.Count == 0)
            return;

        // 把持久值放进 Choices 使其能通过后续 Update 的合法性校验，
        // 否则用户一进设置页点保存就会被 IgnoreUnknownValue 清掉。
        var choices = new List<ChoiceSetSetting.Choice>
        {
            new(Loc.Get("Settings.NicAutoChoice"), NetworkInterfaceClassifier.AutoSelectionValue),
        };
        foreach (var guid in selected)
            choices.Add(new ChoiceSetSetting.Choice(guid, guid));

        _nicSelectionSetting.Choices = choices;
        _nicSelectionSetting.Value = saved;
    }

    /// <summary>
    /// 重建「主网卡」下拉选项：枚举**全部**接口（不只物理 —— 用户需要看到虚拟接口
    /// 才能纠正误判），标注硬件/虚拟/未识别，首项恒为「自动（推荐）」。
    ///
    /// 由 SysMonSettingsContentPage.GetContent() 每次进入设置页时调用一次：
    /// CmdPal 每次打开设置页都会取内容，天然就是刷新点，不引入后台监听。
    /// </summary>
    internal void RefreshNicChoices()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(
            EnumerateNicChoices(),
            GetNicKindLabel,
            Loc.Get("Settings.NicAutoChoice"),
            _nicSelectionSetting.Value,
            Loc.Get("Settings.NicUnavailableSuffix"));

        // 已选网卡在系统里彻底不存在（拔出/卸载驱动）⇒ 收敛回 auto，避免速度恒为 0。
        // 注意：临时 Down 的已选网卡被 BuildNicChoices 以"未连接"标注补回列表，
        // 故不会被误判为消失、不会丢掉用户的选择（见该方法的说明）。
        var resolved = NetworkInterfaceClassifier.ResolveNicSelection(
            _nicSelectionSetting.Value,
            choices,
            NetworkInterfaceClassifier.AutoSelectionValue);

        _nicSelectionSetting.Choices = choices;
        _nicSelectionSetting.Value = resolved;

        // 同步"上次已通知值"：本方法可能在网卡消失时把值收敛回 auto，
        // 若不更新，之后保存任意无关设置都会误判为"网卡选择变了"而重置网络基线
        // （正是 F3 要避免的那类无谓失效）。
        _lastNicSelection = resolved;
    }

    /// <summary>
    /// 枚举接口并分类（不触碰真实网络流量，只读属性 + 注册表绑定表）。
    /// Usability 决定该接口是否列入选项、已选时是否补回 —— 见 BuildNicChoices 的说明。
    /// </summary>
    private static List<NicChoiceSource> EnumerateNicChoices()
    {
        var result = new List<NicChoiceSource>();
        var provider = new RegistryNicBindingProvider();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            string? pnp;
            try
            {
                pnp = provider.GetPnpInstanceId(ni.Id);
            }
            catch
            {
                pnp = null;
            }

            // 可用性判据集中在 NetworkInterfaceClassifier.ClassifyUsability
            // （唯一来源，与 NetworkMonitor 的镜像闸一致）。
            var usability = NetworkInterfaceClassifier.ClassifyUsability(
                ni.OperationalStatus, ni.NetworkInterfaceType, ni.Speed,
                ni.Description, ni.Name);

            result.Add(new NicChoiceSource(
                ni.Id,
                ni.Name,
                ni.Description,
                NetworkInterfaceClassifier.ClassifyPnpInstanceId(pnp),
                usability));
        }

        return result;
    }

    private static string GetNicKindLabel(NicClassification kind) => kind switch
    {
        NicClassification.PhysicalHardware => Loc.Get("Settings.NicKindHardware"),
        NicClassification.Virtual => Loc.Get("Settings.NicKindVirtual"),
        _ => Loc.Get("Settings.NicKindUnknown"),
    };

    public override void SaveSettings()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var existing = ReadSettingsObject(FilePath);
        base.SaveSettings();
        PreserveUnmanagedSettings(FilePath, existing, ManagedKeys);
    }

    internal static void PreserveUnmanagedSettings(
        string filePath,
        JsonObject? existing,
        IReadOnlySet<string> managedKeys)
    {
        if (existing is null || !File.Exists(filePath))
            return;

        var updated = ReadSettingsObject(filePath) ?? [];
        var changed = false;
        foreach (var item in existing)
        {
            if (managedKeys.Contains(item.Key) || updated.ContainsKey(item.Key))
                continue;

            updated[item.Key] = item.Value?.DeepClone();
            changed = true;
        }

        if (changed)
            File.WriteAllText(filePath, updated.ToJsonString(ConfigJsonContext.Default.Options));
    }

    private static JsonObject? ReadSettingsObject(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            return JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }
}
