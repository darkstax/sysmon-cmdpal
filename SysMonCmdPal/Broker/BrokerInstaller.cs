// Copyright (c) 2026 SysMonCmdPal
// Downloads and validates the architecture-specific Broker release asset.

using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace SysMonCmdPal;

internal readonly record struct BrokerReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string ExpectedSha256);

internal sealed partial class BrokerInstaller : IBrokerInstaller
{
    internal static string BrokerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "SysMonCmdPal",
        "Broker",
        "SysMonBroker.exe");

    public bool IsInstalled => File.Exists(BrokerPath);

    public async Task<BrokerInstallFailure> InstallAsync(
        Action<BrokerInstallPhase> reportProgress,
        CancellationToken cancellationToken)
    {
        string? tempDirectory = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Architecture architecture = RuntimeInformation.OSArchitecture;
            if (!IsSupportedArchitecture(architecture))
                return BrokerInstallFailure.UnsupportedArchitecture;

            reportProgress(BrokerInstallPhase.CheckingRelease);
            BrokerReleaseAsset? asset = await GetLatestAssetAsync(architecture, cancellationToken)
                .ConfigureAwait(false);
            if (asset is null)
                return BrokerInstallFailure.NoCompatibleAsset;

            tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "SysMonCmdPal",
                "BrokerInstall",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            string downloadedPath = Path.Combine(tempDirectory, "SysMonBroker.exe");

            reportProgress(BrokerInstallPhase.Downloading);
            await DownloadAssetAsync(asset.Value, downloadedPath, cancellationToken).ConfigureAwait(false);

            if (!await VerifySha256Async(
                downloadedPath,
                asset.Value.ExpectedSha256,
                cancellationToken).ConfigureAwait(false))
            {
                return BrokerInstallFailure.DownloadInvalid;
            }

            if (!ValidatePortableExecutable(downloadedPath, architecture))
                return BrokerInstallFailure.DownloadInvalid;

            cancellationToken.ThrowIfCancellationRequested();
            string? userSid = WindowsIdentity.GetCurrent().User?.Value;
            if (!BrokerInstallElevation.IsValidUserSid(userSid))
                return BrokerInstallFailure.InstallationFailed;

            reportProgress(BrokerInstallPhase.AwaitingElevation);
            BrokerInstallElevationResult elevationResult = await BrokerInstallElevation.InstallAsync(
                downloadedPath,
                asset.Value.ExpectedSha256,
                architecture,
                userSid!,
                () => reportProgress(BrokerInstallPhase.Installing))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return elevationResult switch
            {
                BrokerInstallElevationResult.Succeeded => BrokerInstallFailure.None,
                BrokerInstallElevationResult.Canceled => BrokerInstallFailure.ElevationCanceled,
                BrokerInstallElevationResult.ValidationFailed => BrokerInstallFailure.DownloadInvalid,
                _ => BrokerInstallFailure.InstallationFailed,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BrokerInstallException ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] {ex.Failure}: {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
            return ex.Failure;
        }
        catch (HttpRequestException ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] Network failure: {ex.GetType().Name}");
            return BrokerInstallFailure.Network;
        }
        catch (TaskCanceledException ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] Network timeout: {ex.GetType().Name}");
            return BrokerInstallFailure.Network;
        }
        catch (TimeoutException ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] Response body timeout: {ex.GetType().Name}");
            return BrokerInstallFailure.Network;
        }
        catch (JsonException ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] Invalid release response: {ex.GetType().Name}");
            return BrokerInstallFailure.DownloadInvalid;
        }
        catch (IOException ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] File failure: {ex.GetType().Name}");
            return BrokerInstallFailure.DownloadInvalid;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] Unexpected failure: {ex.GetType().Name}");
            return BrokerInstallFailure.Unexpected;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempDirectory))
                TryDeleteDirectory(tempDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // The per-install random temp directory contains no secrets.
        }
    }

    internal sealed class BrokerInstallException : Exception
    {
        public BrokerInstallException(BrokerInstallFailure failure, Exception? innerException = null)
            : base(failure.ToString(), innerException)
        {
            Failure = failure;
        }

        public BrokerInstallFailure Failure { get; }
    }
}
