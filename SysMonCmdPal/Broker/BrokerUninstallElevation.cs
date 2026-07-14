// Copyright (c) 2026 SysMonCmdPal
// Launches the independently installed Broker uninstaller and owns its script source.

using System.ComponentModel;
using System.Diagnostics;

namespace SysMonCmdPal;

internal enum BrokerUninstallElevationResult
{
    Succeeded,
    Canceled,
    Failed,
}

internal static partial class BrokerUninstallElevation
{
    private static readonly TimeSpan ElevationTimeout = TimeSpan.FromMinutes(2);

    internal static string BrokerDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "SysMonCmdPal",
        "Broker");

    internal static string BrokerPath => Path.Combine(BrokerDirectory, "SysMonBroker.exe");

    internal static string UninstallScriptPath => Path.Combine(
        BrokerDirectory,
        "Uninstall-SysMonBroker.ps1");

    internal static bool IsInstalled => File.Exists(BrokerPath);

    public static async Task<BrokerUninstallElevationResult> UninstallAsync(Action elevationAccepted)
    {
        string scriptPath = UninstallScriptPath;
        if (!IsSafeInstalledScriptPath(scriptPath))
            return BrokerUninstallElevationResult.Failed;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FindPowerShell(),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (string argument in BuildPowerShellArguments(scriptPath))
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return BrokerUninstallElevationResult.Failed;

            try
            {
                elevationAccepted();
            }
            catch
            {
                // UI notification failures must not detach the elevated uninstall.
            }

            using var timeout = new CancellationTokenSource(ElevationTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                TryTerminate(process);
                SensorLogger.ForceLog("[BrokerUninstaller] Elevated uninstall timed out.");
                return BrokerUninstallElevationResult.Failed;
            }

            return process.ExitCode == 0
                ? BrokerUninstallElevationResult.Succeeded
                : BrokerUninstallElevationResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return BrokerUninstallElevationResult.Canceled;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[BrokerUninstaller] Elevation failure: {ex.GetType().Name}");
            return BrokerUninstallElevationResult.Failed;
        }
    }

    internal static string[] BuildPowerShellArguments(string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        if (!Path.IsPathFullyQualified(scriptPath))
            throw new ArgumentException("The uninstall script path must be absolute.", nameof(scriptPath));

        return
        [
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-Elevated",
        ];
    }

    internal static bool IsSafeInstalledScriptPath(string scriptPath)
    {
        try
        {
            string expected = Path.GetFullPath(UninstallScriptPath);
            if (!string.Equals(Path.GetFullPath(scriptPath), expected, StringComparison.OrdinalIgnoreCase))
                return false;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string targetRoot = Path.Combine(programFiles, "SysMonCmdPal");
            foreach (string path in new[] { targetRoot, BrokerDirectory, expected })
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return false;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string BuildUninstallScript() => UninstallScript;

    private static string FindPowerShell()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh))
            return pwsh;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The elevated process may reject termination; the caller still leaves Busy.
        }
    }

}
