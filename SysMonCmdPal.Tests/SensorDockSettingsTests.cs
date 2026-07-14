// Copyright (c) 2026 SysMonCmdPal
// Dynamic sensor Dock band settings tests.

using System.Text.Json.Nodes;
using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SensorDockSettingsTests
{
    [Fact]
    public void SensorDockKey_DockIdRoundTrips()
    {
        var key = new SensorDockKey(5, 257, "GPU Core", "°C");

        Assert.True(SensorDockKey.TryFromDockId(key.DockId, out var parsed));
        Assert.True(SensorDockKeyComparer.Instance.Equals(key, parsed));
    }

    [Fact]
    public void SensorDockKey_MatchesSensorByTagHardwareNameAndUnit()
    {
        var key = new SensorDockKey(5, 257, "GPU Core", "°C");

        Assert.True(key.Matches(new BrokerSensorEntry
        {
            Tag = 5,
            HardwareTag = 257,
            Name = "gpu core",
            Unit = "°C",
            Value = 62.5,
        }));

        Assert.False(key.Matches(new BrokerSensorEntry
        {
            Tag = 5,
            HardwareTag = 258,
            Name = "GPU Core",
            Unit = "°C",
        }));
    }

    [Fact]
    public void LoadFromPath_ReturnsValidDedupedSensorDockBands()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """
                {
                  "sensorDockBands": [
                    { "tag": 0, "hardwareTag": 0, "name": "CPU Package", "unit": "°C" },
                    { "tag": 0, "hardwareTag": 0, "name": "cpu package", "unit": "°C" },
                    { "tag": -1, "hardwareTag": 0, "name": "Bad", "unit": "%" },
                    { "tag": 6, "hardwareTag": 257, "name": "GPU Load", "unit": "%" }
                  ]
                }
                """);

            var keys = SensorDockSettings.LoadFromPath(path);

            Assert.Equal(2, keys.Count);
            Assert.Contains(keys, key => key.Tag == 0 && key.Name == "CPU Package");
            Assert.Contains(keys, key => key.Tag == 6 && key.HardwareTag == 257);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveToPath_WritesSensorDockBandsAndPreservesOtherSettings()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, @"{""version"":""4"",""precisionModeStr"":""Broker"",""btopPath"":""C:\\tools\\btop.exe"",""future"":{""enabled"":true}}");

            SensorDockSettings.SaveToPath(path,
            [
                new SensorDockKey(0, 0, "CPU Package", "°C"),
                new SensorDockKey(0, 0, "cpu package", "°C"),
                new SensorDockKey(6, 257, "GPU Load", "%"),
            ]);

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var bands = root[SensorDockSettings.JsonKey]!.AsArray();

            Assert.Equal("4", (string?)root["version"]);
            Assert.Equal("Broker", (string?)root["precisionModeStr"]);
            Assert.Equal(@"C:\tools\btop.exe", (string?)root["btopPath"]);
            Assert.True((bool?)root["future"]?["enabled"]);
            Assert.Equal(2, bands.Count);
            Assert.Equal("CPU Package", (string?)bands[0]?["name"]);
            Assert.Equal("%", (string?)bands[1]?["unit"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddRemove_UsesSharedConfigPath()
    {
        var realConfigPath = SensorChainConfig.ConfigPath;
        var path = TempPath();
        SensorChainConfig.ConfigPath = path;
        try
        {
            var key = new SensorDockKey(0, 0, "CPU Package", "°C");

            Assert.True(SensorDockSettings.Add(key));
            Assert.False(SensorDockSettings.Add(key));
            Assert.True(SensorDockSettings.Contains(key));

            Assert.True(SensorDockSettings.Remove(key));
            Assert.False(SensorDockSettings.Remove(key));
            Assert.False(SensorDockSettings.Contains(key));
        }
        finally
        {
            SensorChainConfig.ConfigPath = realConfigPath;
            File.Delete(path);
        }
    }

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"sysmon_sensor_dock_{Guid.NewGuid():N}.json");
}
