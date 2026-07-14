// Copyright (c) 2026 SysMonCmdPal
// CmdPal settings surface backed by the existing settings.json file.

using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class SysMonSettingsManager : JsonSettingsManager, ICommandSettings
{
    private readonly SysMonSettingsContentPage _settingsPage;

    public const string BtopPathKey = "btopPath";

    private static readonly HashSet<string> ManagedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        BtopPathKey,
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

        LoadSettings();
        Settings.SettingsChanged += (_, _) => SaveSettings();

        _settingsPage = new SysMonSettingsContentPage(Settings, new BrokerInstallController());
    }

    public IContentPage SettingsPage => _settingsPage;

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
