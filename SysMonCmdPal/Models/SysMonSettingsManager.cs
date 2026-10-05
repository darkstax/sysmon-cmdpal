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
        Settings.SettingsChanged += (_, _) => SaveSettings();

        _settingsPage = new SysMonSettingsContentPage(
            Settings,
            new BrokerInstallController(),
            RefreshNicChoices);
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
            Loc.Get("Settings.NicAutoChoice"));

        // 已选网卡全部失效（拔出/卸载驱动）⇒ 收敛回 auto，避免速度恒为 0。
        var resolved = NetworkInterfaceClassifier.ResolveNicSelection(
            _nicSelectionSetting.Value,
            choices,
            NetworkInterfaceClassifier.AutoSelectionValue);

        _nicSelectionSetting.Choices = choices;
        _nicSelectionSetting.Value = resolved;
    }

    /// <summary>枚举全部接口并分类（不触碰真实网络流量，只读属性 + 注册表绑定表）。</summary>
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

            result.Add(new NicChoiceSource(
                ni.Id,
                ni.Name,
                ni.Description,
                NetworkInterfaceClassifier.ClassifyPnpInstanceId(pnp)));
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
