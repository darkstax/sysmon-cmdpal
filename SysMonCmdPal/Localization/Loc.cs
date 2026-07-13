// Copyright (c) 2026 SysMonCmdPal
// Localization helper — wraps Windows.ApplicationModel.Resources.ResourceLoader.
//
// Uses GetForViewIndependentUse() because this extension runs as an out-of-process
// COM server with no UI thread / view context.
//
// IMPORTANT: PRI generation uses convertDotsToSlashes="true", so a resource key
// like "Dock.Cpu" in the .resw is stored as "Resources/Dock/Cpu" in the PRI.
// ResourceLoader.GetString expects the slash-separated path relative to the
// "Resources" subtree, so we convert "Dock.Cpu" → "Dock/Cpu" before lookup.
// Callers use dot notation (e.g. Loc.Get("Dock.Cpu")) for readability.

using Windows.ApplicationModel.Resources;

namespace SysMonCmdPal;

/// <summary>
/// Centralized resource loader for localized strings.
/// Thread-safe singleton; safe to call from any thread (COM server, timer, UI).
/// </summary>
internal static class Loc
{
    private static readonly Lazy<ResourceLoader?> s_loader = new(() =>
    {
        try { return ResourceLoader.GetForViewIndependentUse(); }
        catch { return null; }
    });

    /// <summary>Get a localized string by dot-separated resource key. Returns the key itself if not found.</summary>
    public static string Get(string key)
    {
        if (IsTestHost())
            return GetFallback(key, forceChinese: true);

        var priKey = key.Replace('.', '/');
        try
        {
            return s_loader.Value?.GetString(priKey) is { Length: > 0 } s ? s : GetFallback(key);
        }
        catch
        {
            return GetFallback(key);
        }
    }

    /// <summary>Get a localized string and format it with arguments.</summary>
    public static string Format(string key, params object[] args)
        => string.Format(Get(key), args);

    private static bool IsTestHost()
    {
        string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "";
        return processName.Equals("testhost", StringComparison.OrdinalIgnoreCase) ||
            AppContext.BaseDirectory.Contains("SysMonCmdPal.Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFallback(string key, bool forceChinese = false)
    {
        bool zh = forceChinese ||
            Thread.CurrentThread.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        return key switch
        {
            "BatteryStatus.Charging" => zh ? "充电中" : "Charging",
            "BatteryStatus.Discharging" => zh ? "放电中" : "Discharging",
            "BatteryStatus.Dual" => zh ? "双重供电" : "Dual power",
            "BatteryStatus.Full" => zh ? "已充满" : "Full",
            "BatteryStatus.NoBattery" => zh ? "无电池" : "No battery",
            _ => key,
        };
    }
}
