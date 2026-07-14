// Copyright (c) 2026 SysMonCmdPal
// CmdPal settings surface backed by the existing settings.json file.

using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Text.Json.Nodes;

namespace SysMonCmdPal;

internal sealed class SysMonSettingsManager : JsonSettingsManager
{
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
    }

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
