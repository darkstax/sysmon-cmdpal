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
    private static readonly ResourceLoader _loader = ResourceLoader.GetForViewIndependentUse();

    /// <summary>Get a localized string by dot-separated resource key. Returns the key itself if not found.</summary>
    public static string Get(string key)
    {
        var priKey = key.Replace('.', '/');
        return _loader.GetString(priKey) is { Length: > 0 } s ? s : key;
    }

    /// <summary>Get a localized string and format it with arguments.</summary>
    public static string Format(string key, params object[] args)
        => string.Format(Get(key), args);
}
