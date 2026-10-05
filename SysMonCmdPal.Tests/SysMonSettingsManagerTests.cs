// Copyright (c) 2026 SysMonCmdPal
// SysMonSettingsManager tests - CmdPal settings save must preserve shared settings.json keys.

using System.Text.Json.Nodes;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SysMonSettingsManagerTests
{
    [Fact]
    public void PreserveUnmanagedSettings_RestoresExistingNonManagedKeys()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_settings_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, @"{""btopPath"":""C:\\tools\\btop.exe""}");
            var existing = JsonNode.Parse(@"{""version"":""4"",""precisionModeStr"":""Broker"",""btopPath"":""C:\\old\\btop.exe"",""future"":{""enabled"":true}}")!.AsObject();

            SysMonSettingsManager.PreserveUnmanagedSettings(
                path,
                existing,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SysMonSettingsManager.BtopPathKey });

            var updated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            Assert.Equal(@"C:\tools\btop.exe", (string?)updated["btopPath"]);
            Assert.Equal("4", (string?)updated["version"]);
            Assert.Equal("Broker", (string?)updated["precisionModeStr"]);
            Assert.True((bool?)updated["future"]?["enabled"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ================================================================
    // selectedNicGuids — 网卡手动选择键的持久化
    // ================================================================

    [Fact]
    public void SelectedNicsKey_IsManagedSoSaveDoesNotDropIt()
    {
        // ManagedKeys 必须含 selectedNicGuids，否则 SaveSettings 的
        // PreserveUnmanagedSettings 会把它当"非托管键"处理，语义错位。
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_nic_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, @"{""selectedNicGuids"":""{A844F74B-BAB2-459B-9EC8-922D56E146EC}""}");
            var existing = JsonNode.Parse(
                @"{""version"":""4"",""selectedNicGuids"":""auto"",""btopPath"":""C:\\btop.exe""}")!.AsObject();

            SysMonSettingsManager.PreserveUnmanagedSettings(
                path,
                existing,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    SysMonSettingsManager.BtopPathKey,
                    SysMonSettingsManager.SelectedNicsKey,
                });

            var updated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            // 文件里的（用户刚保存的）选择值不被 existing 覆盖
            Assert.Equal("{A844F74B-BAB2-459B-9EC8-922D56E146EC}", (string?)updated["selectedNicGuids"]);
            // 非托管键仍被保留
            Assert.Equal("4", (string?)updated["version"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SelectedNicsKey_MatchesClassifierConstant()
    {
        Assert.Equal(NetworkInterfaceClassifier.SelectedNicsKey, SysMonSettingsManager.SelectedNicsKey);
        Assert.Equal("selectedNicGuids", SysMonSettingsManager.SelectedNicsKey);
    }

    [Fact]
    public void PreserveUnmanagedSettings_StillRestoresOtherKeysWithNicKeyManaged()
    {
        // 回归：把 selectedNicGuids 加入托管键后，其他未知键的保留行为不得改变
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_nic2_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, @"{""btopPath"":""C:\\tools\\btop.exe"",""selectedNicGuids"":""auto""}");
            var existing = JsonNode.Parse(
                @"{""version"":""4"",""precisionModeStr"":""Broker"",""future"":{""enabled"":true}}")!.AsObject();

            SysMonSettingsManager.PreserveUnmanagedSettings(
                path,
                existing,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    SysMonSettingsManager.BtopPathKey,
                    SysMonSettingsManager.SelectedNicsKey,
                });

            var updated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            Assert.Equal("4", (string?)updated["version"]);
            Assert.Equal("Broker", (string?)updated["precisionModeStr"]);
            Assert.True((bool?)updated["future"]?["enabled"]);
            Assert.Equal("auto", (string?)updated["selectedNicGuids"]);
            Assert.Equal(@"C:\tools\btop.exe", (string?)updated["btopPath"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ================================================================
    // NetworkMonitor 直读 settings.json（与设置管理器解耦）
    // ================================================================

    [Fact]
    public void ReadSelectedNicsFromSettings_ReadsKeyViaConfigPath()
    {
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_nic3_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;

            // 文件不存在 ⇒ null（= 自动模式）
            Assert.Null(NetworkMonitor.ReadSelectedNicsFromSettings());

            File.WriteAllText(path, @"{""btopPath"":"""",""selectedNicGuids"":""{A844F74B-BAB2-459B-9EC8-922D56E146EC}""}");
            Assert.Equal("{A844F74B-BAB2-459B-9EC8-922D56E146EC}",
                NetworkMonitor.ReadSelectedNicsFromSettings());

            // 键缺失 ⇒ null
            File.WriteAllText(path, @"{""btopPath"":""""}");
            Assert.Null(NetworkMonitor.ReadSelectedNicsFromSettings());

            // 非法 JSON ⇒ null（绝不抛给采集循环）
            File.WriteAllText(path, "{ not json ");
            Assert.Null(NetworkMonitor.ReadSelectedNicsFromSettings());
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            File.Delete(path);
        }
    }

    // ================================================================
    // 端到端：选中网卡 → 保存 → 重启 → 仍然生效
    // ================================================================

    /// <summary>
    /// 回归守卫：LoadSettings() 在 ctor 里执行，此时 ChoiceSetSetting.Choices 只含占位 "auto"。
    /// ChoiceSetSetting.Update 在值不在 Choices 里且 IgnoreUnknownValue=true 时**静默丢弃**该值，
    /// 因此若不在加载后从文件恢复，用户选好的网卡每次启动都会被重置成 auto。
    /// </summary>
    [Fact]
    public void NicSelection_SurvivesReloadFromSettingsFile()
    {
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_nic_reload_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;

            const string nicGuid = "{A844F74B-BAB2-459B-9EC8-922D56E146EC}";
            File.WriteAllText(path, $@"{{""btopPath"":"""",""selectedNicGuids"":""{nicGuid}""}}");

            var manager = new SysMonSettingsManager();

            // 关键断言：重启后持久值必须存活（不能被 IgnoreUnknownValue 丢掉）
            Assert.Equal(nicGuid, manager.NicSelectionSetting.Value);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void NicSelection_FreshInstallDefaultsToAuto()
    {
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_nic_fresh_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;   // 文件不存在

            var manager = new SysMonSettingsManager();

            Assert.Equal(NetworkInterfaceClassifier.AutoSelectionValue, manager.NicSelectionSetting.Value);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void NicSelection_AutoLiteralStaysAuto()
    {
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_nic_auto_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;
            File.WriteAllText(path, @"{""selectedNicGuids"":""auto""}");

            var manager = new SysMonSettingsManager();

            Assert.Equal("auto", manager.NicSelectionSetting.Value);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void NicSelection_SavePersistsValueAndPreservesOtherKeys()
    {
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_nic_save_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;
            File.WriteAllText(path, @"{""version"":""4"",""btopPath"":""C:\\btop.exe""}");

            var manager = new SysMonSettingsManager();
            const string nicGuid = "{5B4FBC47-6C51-48AC-8D79-E6E057816F16}";
            manager.NicSelectionSetting.Value = nicGuid;
            manager.SaveSettings();

            var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            Assert.Equal(nicGuid, (string?)saved[SysMonSettingsManager.SelectedNicsKey]);
            // 非托管键不得丢失
            Assert.Equal("4", (string?)saved["version"]);
            Assert.Equal(@"C:\btop.exe", (string?)saved["btopPath"]);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// 回归守卫：IgnoreUnknownValue 的既有语义必须保留 ——
    /// 回传一个不在选项里的失效 GUID 时忽略该次更新，而不是把悬空值写进去。
    /// </summary>
    [Fact]
    public void NicSelection_IgnoresUpdateWithUnknownValue()
    {
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_nic_unknown_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;
            const string nicGuid = "{A844F74B-BAB2-459B-9EC8-922D56E146EC}";
            File.WriteAllText(path, $@"{{""selectedNicGuids"":""{nicGuid}""}}");

            var manager = new SysMonSettingsManager();
            Assert.Equal(nicGuid, manager.NicSelectionSetting.Value);

            var stale = JsonNode.Parse(
                @"{""selectedNicGuids"":""{11111111-2222-3333-4444-555555555555}""}")!.AsObject();
            manager.NicSelectionSetting.Update(stale);

            Assert.Equal(nicGuid, manager.NicSelectionSetting.Value);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }

    // ================================================================
    // F3 回归（t3 审查）：无关设置变更不得触发网卡失效
    // ================================================================

    /// <summary>触发 Settings 的 SettingsChanged（CmdPal 在表单提交时调用）。</summary>
    private static void RaiseSettingsChanged(SysMonSettingsManager manager)
    {
        var method = manager.Settings.GetType().GetMethod(
            "RaiseSettingsChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(manager.Settings, null);
    }

    private static void ApplySetting(SysMonSettingsManager manager, string json)
        => manager.Settings.Update(json);

    [Fact]
    public void F3_UnrelatedSettingChange_DoesNotFireNicSelectionChanged()
    {
        // 回归（t3 F3）：修复前订阅 Settings.SettingsChanged，保存 btopPath 等无关设置
        // 也会触发 InvalidateNicSelection() → Seed()，无谓重置网络基线。
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_f3a_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;
            var manager = new SysMonSettingsManager();
            manager.RefreshNicChoices();

            int fired = 0;
            manager.NicSelectionChanged += (_, _) => fired++;

            ApplySetting(manager, @"{""btopPath"":""C:\\btop.exe""}");
            RaiseSettingsChanged(manager);

            Assert.Equal(0, fired);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void F3_ActualNicSelectionChange_DoesFireNicSelectionChanged()
    {
        // 反向守卫：真正改了网卡选择必须触发失效（否则新选择不生效）
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_f3b_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;
            var manager = new SysMonSettingsManager();
            manager.RefreshNicChoices();

            int fired = 0;
            manager.NicSelectionChanged += (_, _) => fired++;

            // 直接改设置项值后触发（模拟表单提交）
            const string nicGuid = "{A844F74B-BAB2-459B-9EC8-922D56E146EC}";
            manager.NicSelectionSetting.Value = nicGuid;
            RaiseSettingsChanged(manager);

            Assert.Equal(1, fired);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void F3_SameNicValueAgain_DoesNotRefire()
    {
        // 幂等：反复保存同一个网卡值不得重复重置基线
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_f3c_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;
            var manager = new SysMonSettingsManager();
            manager.RefreshNicChoices();

            int fired = 0;
            manager.NicSelectionChanged += (_, _) => fired++;

            const string nicGuid = "{A844F74B-BAB2-459B-9EC8-922D56E146EC}";
            manager.NicSelectionSetting.Value = nicGuid;
            RaiseSettingsChanged(manager);
            RaiseSettingsChanged(manager);
            RaiseSettingsChanged(manager);

            Assert.Equal(1, fired);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void F3_RefreshNicChoicesSyncsBaseline_NoSpuriousEventAfterwards()
    {
        // RefreshNicChoices 在网卡消失时会把值收敛回 auto。
        // 若不同步"上次已通知值"，之后保存任意无关设置都会误判为网卡变化而重置基线。
        var realPath = SensorChainConfig.ConfigPath;
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_f3d_{Guid.NewGuid():N}.json");
        try
        {
            SensorChainConfig.ConfigPath = path;
            // 持久一个不存在的 GUID（真实接口枚举里没有它）
            File.WriteAllText(path,
                @"{""selectedNicGuids"":""{11111111-2222-3333-4444-555555555555}""}");

            var manager = new SysMonSettingsManager();
            manager.RefreshNicChoices();   // 收敛回 auto，并同步基线

            int fired = 0;
            manager.NicSelectionChanged += (_, _) => fired++;

            ApplySetting(manager, @"{""btopPath"":""C:\\btop.exe""}");
            RaiseSettingsChanged(manager);

            Assert.Equal(0, fired);
        }
        finally
        {
            SensorChainConfig.ConfigPath = realPath;
            try { File.Delete(path); } catch { }
        }
    }
}
