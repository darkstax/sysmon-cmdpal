// Copyright (c) 2026 SysMonCmdPal
// BtopLauncherCommand tests — settings path precedence and directory expansion.

using Xunit;

namespace SysMonCmdPal.Tests;

public class BtopLauncherCommandTests
{
    [Fact]
    public void FindBtopExe_PrefersConfiguredBtopPathOverOtherLocations()
    {
        string settingsPath = WriteTempSettings(@"{""btopPath"":""C:\\custom\\btop.exe""}");
        try
        {
            var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                settingsPath,
                @"C:\custom\btop.exe",
                @"C:\scoop\btop.exe",
                @"C:\path\btop.exe",
                @"C:\Program Files\btop4win\btop.exe",
            };

            string? found = BtopLauncherCommand.FindBtopExe(
                settingsPath,
                [@"C:\scoop\btop.exe"],
                [@"C:\path"],
                [@"C:\Program Files\btop4win\btop.exe"],
                existingFiles.Contains,
                _ => false);

            Assert.Equal(@"C:\custom\btop.exe", found);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public void GetConfiguredBtopCandidates_ExpandsConfiguredDirectory()
    {
        string settingsPath = WriteTempSettings(@"{""btopPath"":""C:\\tools\\btop4win""}");
        try
        {
            var candidates = BtopLauncherCommand.GetConfiguredBtopCandidates(
                settingsPath,
                path => string.Equals(path, settingsPath, StringComparison.OrdinalIgnoreCase),
                path => string.Equals(path, @"C:\tools\btop4win", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.Equal(
                [@"C:\tools\btop4win\btop.exe", @"C:\tools\btop4win\btop4win.exe"],
                candidates);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public void FindBtopExe_FallsBackToPathAfterMissingConfiguredPath()
    {
        string settingsPath = WriteTempSettings(@"{""btopPath"":""C:\\missing\\btop.exe""}");
        try
        {
            var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                settingsPath,
                @"C:\path\btop4win.exe",
            };

            string? found = BtopLauncherCommand.FindBtopExe(
                settingsPath,
                [],
                [@"C:\path"],
                [],
                existingFiles.Contains,
                _ => false);

            Assert.Equal(@"C:\path\btop4win.exe", found);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    private static string WriteTempSettings(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sysmon_btop_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
