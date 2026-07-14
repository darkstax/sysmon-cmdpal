// Copyright (c) 2026 SysMonCmdPal
// Dynamic sensor Dock band selections stored in the shared settings.json file.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

internal readonly record struct SensorDockKey(int Tag, int HardwareTag, string Name, string Unit)
{
    private const string DockIdPrefix = "sysmon.dock.sensor.";
    private const char PayloadSeparator = '\u001f';

    public static SensorDockKey FromSensor(BrokerSensorEntry sensor)
        => new(
            sensor.Tag,
            sensor.HardwareTag,
            (sensor.Name ?? string.Empty).Trim(),
            (sensor.Unit ?? string.Empty).Trim());

    public string DockId => DockIdPrefix + Base64UrlEncode(Encoding.UTF8.GetBytes(ToPayload()));

    public bool IsValid => Tag >= 0 && !string.IsNullOrWhiteSpace(Name);

    public bool Matches(BrokerSensorEntry sensor)
    {
        if (sensor.Tag != Tag || sensor.HardwareTag != HardwareTag)
            return false;

        if (!string.Equals((sensor.Name ?? string.Empty).Trim(), Name, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(Unit) ||
            string.Equals((sensor.Unit ?? string.Empty).Trim(), Unit, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryFromDockId(string id, out SensorDockKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(id) ||
            !id.StartsWith(DockIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var token = id[DockIdPrefix.Length..];
            var payload = Encoding.UTF8.GetString(Base64UrlDecode(token));
            var parts = payload.Split(PayloadSeparator);
            if (parts.Length != 4 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tag) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hardwareTag))
            {
                return false;
            }

            var candidate = new SensorDockKey(tag, hardwareTag, parts[2], parts[3]);
            if (!candidate.IsValid)
                return false;

            key = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string ToPayload()
        => string.Join(
            PayloadSeparator,
            Tag.ToString(CultureInfo.InvariantCulture),
            HardwareTag.ToString(CultureInfo.InvariantCulture),
            Name,
            Unit);

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}

internal sealed class SensorDockKeyComparer : IEqualityComparer<SensorDockKey>
{
    public static SensorDockKeyComparer Instance { get; } = new();

    public bool Equals(SensorDockKey x, SensorDockKey y)
        => x.Tag == y.Tag &&
           x.HardwareTag == y.HardwareTag &&
           string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(x.Unit, y.Unit, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(SensorDockKey obj)
        => HashCode.Combine(
            obj.Tag,
            obj.HardwareTag,
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Unit ?? string.Empty));
}

internal static class SensorDockSettings
{
    public const string JsonKey = "sensorDockBands";

    public static event EventHandler? Changed;

    public static IReadOnlyList<SensorDockKey> Load()
        => LoadFromPath(SensorChainConfig.ConfigPath);

    public static bool Contains(BrokerSensorEntry sensor)
        => Contains(SensorDockKey.FromSensor(sensor));

    public static bool Contains(SensorDockKey key)
        => Load().Contains(key, SensorDockKeyComparer.Instance);

    public static bool Add(BrokerSensorEntry sensor)
        => Add(SensorDockKey.FromSensor(sensor));

    public static bool Add(SensorDockKey key)
    {
        if (!key.IsValid)
            return false;

        var keys = Load().ToList();
        if (keys.Contains(key, SensorDockKeyComparer.Instance))
            return false;

        keys.Add(key);
        Save(keys);
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    public static bool Remove(BrokerSensorEntry sensor)
        => Remove(SensorDockKey.FromSensor(sensor));

    public static bool Remove(SensorDockKey key)
    {
        var keys = Load().ToList();
        var removed = keys.RemoveAll(existing => SensorDockKeyComparer.Instance.Equals(existing, key)) > 0;
        if (!removed)
            return false;

        Save(keys);
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    internal static IReadOnlyList<SensorDockKey> LoadFromPath(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return [];

            if (JsonNode.Parse(File.ReadAllText(filePath)) is not JsonObject root ||
                root[JsonKey] is not JsonArray array)
            {
                return [];
            }

            var keys = new List<SensorDockKey>(array.Count);
            foreach (var node in array)
            {
                if (node is not JsonObject item ||
                    !TryReadInt(item, "tag", out var tag) ||
                    !TryReadInt(item, "hardwareTag", out var hardwareTag) ||
                    !TryReadString(item, "name", out var name))
                {
                    continue;
                }

                _ = TryReadString(item, "unit", out var unit);
                var key = new SensorDockKey(tag, hardwareTag, name.Trim(), unit.Trim());
                if (key.IsValid && !keys.Contains(key, SensorDockKeyComparer.Instance))
                    keys.Add(key);
            }

            return keys;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[SensorDockSettings] Load failed: {ex.Message}");
            return [];
        }
    }

    internal static void SaveToPath(string filePath, IEnumerable<SensorDockKey> keys)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var root = ReadSettingsObject(filePath) ?? [];
            var array = new JsonArray();
            foreach (var key in Normalize(keys))
            {
                var item = new JsonObject
                {
                    ["tag"] = key.Tag,
                    ["hardwareTag"] = key.HardwareTag,
                    ["name"] = key.Name,
                    ["unit"] = key.Unit,
                };
                array.Add((JsonNode)item);
            }

            root[JsonKey] = array;
            File.WriteAllText(filePath, root.ToJsonString(ConfigJsonContext.Default.Options));
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[SensorDockSettings] Save failed: {ex.Message}");
        }
    }

    private static void Save(IEnumerable<SensorDockKey> keys)
        => SaveToPath(SensorChainConfig.ConfigPath, keys);

    private static IEnumerable<SensorDockKey> Normalize(IEnumerable<SensorDockKey> keys)
    {
        var seen = new HashSet<SensorDockKey>(SensorDockKeyComparer.Instance);
        foreach (var key in keys)
        {
            var normalized = new SensorDockKey(
                key.Tag,
                key.HardwareTag,
                (key.Name ?? string.Empty).Trim(),
                (key.Unit ?? string.Empty).Trim());
            if (normalized.IsValid && seen.Add(normalized))
                yield return normalized;
        }
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

    private static bool TryReadInt(JsonObject obj, string key, out int value)
    {
        value = default;
        try
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is null)
                return false;

            if (node is JsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<int>(out value))
                    return true;

                if (jsonValue.TryGetValue<string>(out var str) &&
                    int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryReadString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        try
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is null)
                return false;

            if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var str))
            {
                value = str;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }
}
