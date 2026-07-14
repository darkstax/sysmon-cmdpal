// Copyright (c) 2026 SysMonCmdPal
// Builds the one-shot elevated installer used after the release asset is verified.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SysMonCmdPal;

internal enum BrokerInstallElevationResult
{
    Succeeded,
    Canceled,
    ValidationFailed,
    Failed,
}

internal static partial class BrokerInstallElevation
{
    private const int HashValidationExitCode = 20;
    private const int ExecutableValidationExitCode = 21;
    private static readonly TimeSpan ElevationTimeout = TimeSpan.FromMinutes(5);

    public static async Task<BrokerInstallElevationResult> InstallAsync(
        string sourcePath,
        string sha256,
        Architecture architecture,
        string userSid,
        Action elevationAccepted)
    {
        string encodedCommand;
        try
        {
            encodedCommand = BuildEncodedCommand(sourcePath, sha256, architecture, userSid);
        }
        catch (ArgumentException)
        {
            return BrokerInstallElevationResult.ValidationFailed;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FindPowerShell(),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (string argument in BuildPowerShellArguments(encodedCommand))
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return BrokerInstallElevationResult.Failed;

            try
            {
                elevationAccepted();
            }
            catch
            {
                // UI notification failures must not detach the elevated transaction.
            }

            using var timeout = new CancellationTokenSource(ElevationTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                TryTerminate(process);
                SensorLogger.ForceLog("[BrokerInstaller] Elevated install timed out.");
                return BrokerInstallElevationResult.Failed;
            }

            return process.ExitCode switch
            {
                0 => BrokerInstallElevationResult.Succeeded,
                HashValidationExitCode or ExecutableValidationExitCode => BrokerInstallElevationResult.ValidationFailed,
                _ => BrokerInstallElevationResult.Failed,
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return BrokerInstallElevationResult.Canceled;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] Elevation failure: {ex.GetType().Name}");
            return BrokerInstallElevationResult.Failed;
        }
    }

    internal static string BuildEncodedCommand(
        string sourcePath,
        string sha256,
        Architecture architecture,
        string userSid)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(
            BuildInstallerScript(sourcePath, sha256, architecture, userSid)));

    internal static string BuildInstallerScript(
        string sourcePath,
        string sha256,
        Architecture architecture,
        string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!IsSha256(sha256))
            throw new ArgumentException("Invalid SHA-256 value.", nameof(sha256));
        if (!IsValidUserSid(userSid))
            throw new ArgumentException("Invalid user SID.", nameof(userSid));

        ushort machine = architecture switch
        {
            Architecture.X64 => 0x8664,
            Architecture.Arm64 => 0xAA64,
            _ => throw new ArgumentException("Unsupported architecture.", nameof(architecture)),
        };

        string uninstallScriptBase64 = Convert.ToBase64String(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
                BrokerUninstallElevation.BuildUninstallScript()));

        return InstallerScript
            .Replace("__SOURCE__", EscapePowerShellLiteral(sourcePath), StringComparison.Ordinal)
            .Replace("__SHA256__", sha256.ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("__MACHINE__", machine.ToString(), StringComparison.Ordinal)
            .Replace("__USER_SID__", userSid, StringComparison.Ordinal)
            .Replace("__UNINSTALL_SCRIPT_BASE64__", uninstallScriptBase64, StringComparison.Ordinal);
    }

    internal static string[] BuildPowerShellArguments(string encodedCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedCommand);
        return
        [
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-EncodedCommand",
            encodedCommand,
        ];
    }

    internal static bool IsValidUserSid(string? userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid) || !userSid.StartsWith("S-1-", StringComparison.Ordinal))
            return false;

        string[] parts = userSid.Split('-');
        return parts.Length >= 3
            && parts[0] == "S"
            && parts.Skip(1).All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

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

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string EscapePowerShellLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
