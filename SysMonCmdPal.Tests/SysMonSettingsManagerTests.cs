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
}
