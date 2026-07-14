// Copyright (c) 2026 SysMonCmdPal
// Dedicated tests for GitHub Broker asset selection, validation, and install state.

using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SysMonCmdPal.Tests;

public class BrokerInstallerTests
{
    [Fact]
    public void SelectAsset_UsesOnlyExactArchitectureSpecificExecutable()
    {
        BrokerReleaseAsset[] assets =
        [
            Asset("SysMonBroker.exe"),
            Asset("SysMonBroker-win-arm64.exe"),
            Asset("SysMonBroker-win-x64.zip"),
            Asset("SysMonBroker-win-x64.exe"),
        ];

        BrokerReleaseAsset? selected = BrokerInstaller.SelectAsset(assets, Architecture.X64);

        Assert.Equal("SysMonBroker-win-x64.exe", selected?.Name);
    }

    [Fact]
    public void SelectAsset_RejectsAmbiguousExactMatches()
    {
        BrokerReleaseAsset[] assets =
        [
            Asset("SysMonBroker-win-x64.exe"),
            Asset("sysmonbroker-WIN-X64.exe"),
        ];

        Assert.Null(BrokerInstaller.SelectAsset(assets, Architecture.X64));
    }

    [Fact]
    public void ParseLatestReleaseResponse_StoresNormalizedMetadataDigest()
    {
        string digest = $"sha256:{new string('a', 64)}";

        BrokerReleaseAsset? asset = BrokerInstaller.ParseLatestReleaseResponse(
            ReleaseBody(digest, includeDigest: true),
            Architecture.X64);

        Assert.NotNull(asset);
        Assert.Equal(new string('A', 64), asset.Value.ExpectedSha256);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("SHA256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaZ", true)]
    public void ParseLatestReleaseResponse_RejectsMissingOrMalformedDigest(
        string? digest,
        bool includeDigest)
    {
        BrokerInstaller.BrokerInstallException exception = Assert.Throws<BrokerInstaller.BrokerInstallException>(
            () => BrokerInstaller.ParseLatestReleaseResponse(
                ReleaseBody(digest, includeDigest),
                Architecture.X64));

        Assert.Equal(BrokerInstallFailure.DownloadInvalid, exception.Failure);
    }

    [Theory]
    [InlineData("https://github.com/darkstax/sysmon-cmdpal/releases/download/v2.3.0/SysMonBroker-win-x64.exe", true)]
    [InlineData("http://github.com/darkstax/sysmon-cmdpal/releases/download/v2.3.0/SysMonBroker-win-x64.exe", false)]
    [InlineData("https://github.com.evil.example/darkstax/sysmon-cmdpal/releases/download/v2.3.0/SysMonBroker-win-x64.exe", false)]
    [InlineData("https://github.com/another/sysmon-cmdpal/releases/download/v2.3.0/SysMonBroker-win-x64.exe", false)]
    public void IsTrustedReleaseAssetUri_RestrictsSchemeHostAndRepository(string rawUri, bool expected)
    {
        Assert.Equal(expected, BrokerInstaller.IsTrustedReleaseAssetUri(new Uri(rawUri)));
    }

    [Fact]
    public void LatestRelease404_IsReportedAsNoRelease()
    {
        Assert.Equal(
            BrokerInstallFailure.NoRelease,
            BrokerInstaller.ClassifyLatestReleaseStatus(HttpStatusCode.NotFound));
        Assert.Null(BrokerInstaller.ClassifyLatestReleaseStatus(HttpStatusCode.OK));
    }

    [Fact]
    public void ValidatePortableExecutable_RequiresExpectedMachineAndExecutableFlags()
    {
        string x64Path = WritePortableExecutable(0x8664, 0x0002);
        string arm64Path = WritePortableExecutable(0xAA64, 0x0002);
        string dllPath = WritePortableExecutable(0x8664, 0x2002);
        try
        {
            Assert.True(BrokerInstaller.ValidatePortableExecutable(x64Path, Architecture.X64));
            Assert.True(BrokerInstaller.ValidatePortableExecutable(arm64Path, Architecture.Arm64));
            Assert.False(BrokerInstaller.ValidatePortableExecutable(x64Path, Architecture.Arm64));
            Assert.False(BrokerInstaller.ValidatePortableExecutable(dllPath, Architecture.X64));
        }
        finally
        {
            File.Delete(x64Path);
            File.Delete(arm64Path);
            File.Delete(dllPath);
        }
    }

    [Fact]
    public async Task VerifySha256Async_RejectsDigestMismatch()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "trusted broker bytes");
            string expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("trusted broker bytes")));
            string mismatched = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("different broker bytes")));

            Assert.True(await BrokerInstaller.VerifySha256Async(
                path,
                expected,
                CancellationToken.None));
            Assert.False(await BrokerInstaller.VerifySha256Async(
                path,
                mismatched,
                CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifySha256Async_ObservesCancellation()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => BrokerInstaller.VerifySha256Async(
                    path,
                    new string('A', 64),
                    cancellation.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadBoundedContentAsync_EnforcesTotalBodyDeadline()
    {
        using var content = new StreamContent(new BlockingReadStream());

        await Assert.ThrowsAsync<TimeoutException>(
            () => BrokerInstaller.ReadBoundedContentAsync(
                content,
                1024,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadBoundedContentAsync_PreservesCallerCancellation()
    {
        using var content = new StreamContent(new BlockingReadStream());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BrokerInstaller.ReadBoundedContentAsync(
                content,
                1024,
                TimeSpan.FromSeconds(5),
                cancellation.Token));
    }

    [Fact]
    public async Task WriteAssetBodyAsync_EnforcesTotalBodyDeadline()
    {
        string path = Path.Combine(Path.GetTempPath(), $"broker_body_{Guid.NewGuid():N}.exe");
        try
        {
            using var content = new StreamContent(new BlockingReadStream());

            await Assert.ThrowsAsync<TimeoutException>(
                () => BrokerInstaller.WriteAssetBodyAsync(
                    content,
                    path,
                    expectedSize: 1,
                    TimeSpan.FromMilliseconds(50),
                    CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildEncodedCommand_EscapesSourceAndBindsValidationValues()
    {
        const string source = @"C:\Temp\O'Brien\SysMonBroker.exe";
        string encoded = BrokerInstallElevation.BuildEncodedCommand(
            source,
            new string('A', 64),
            Architecture.X64,
            "S-1-5-21-1000");

        string script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Contains(@"$source = 'C:\Temp\O''Brien\SysMonBroker.exe'", script);
        Assert.Contains("$expectedHash = 'AAAAAAAA", script);
        Assert.Contains("$expectedMachine = [UInt16]34404", script);
        Assert.Contains("$userSid = 'S-1-5-21-1000'", script);
        Assert.DoesNotContain("__SOURCE__", script);
    }

    [Fact]
    public async Task Controller_PreventsConcurrentInstallsAndPublishesSuccess()
    {
        var installer = new BlockingInstaller();
        var controller = new BrokerInstallController(installer);
        var succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StatusChanged += (_, _) =>
        {
            if (controller.Snapshot.Phase == BrokerInstallPhase.Succeeded)
                succeeded.TrySetResult();
        };

        Assert.True(controller.TryStart());
        await installer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(controller.TryStart());
        Assert.True(controller.Snapshot.IsBusy);

        installer.Complete();
        await installer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BrokerInstallPhase.Succeeded, controller.Snapshot.Phase);
        Assert.True(controller.Snapshot.IsInstalled);
    }

    [Fact]
    public async Task Controller_CancellationPublishesNonBusyFailedState()
    {
        var installer = new CancelableInstaller();
        var controller = new BrokerInstallController(installer);
        using var cancellation = new CancellationTokenSource();
        Task failed = WaitForPhaseAsync(controller, BrokerInstallPhase.Failed);

        Assert.True(controller.TryStart(cancellation.Token));
        await installer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await failed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(controller.Snapshot.IsBusy);
        Assert.Equal(BrokerInstallFailure.Canceled, controller.Snapshot.Failure);
    }

    [Fact]
    public async Task Controller_InstallerExceptionPublishesNonBusyFailedState()
    {
        var controller = new BrokerInstallController(new ThrowingInstaller());
        Task failed = WaitForPhaseAsync(controller, BrokerInstallPhase.Failed);

        Assert.True(controller.TryStart());
        await failed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(controller.Snapshot.IsBusy);
        Assert.Equal(BrokerInstallFailure.Unexpected, controller.Snapshot.Failure);
    }

    [Theory]
    [InlineData("{\"action\":\"installBroker\"}", true)]
    [InlineData("{\"action\":\"other\"}", false)]
    [InlineData("not-json", false)]
    public void SettingsForm_RecognizesOnlyInstallAction(string data, bool expected)
    {
        Assert.Equal(expected, BrokerInstallSettingsForm.ContainsInstallAction(data));
    }

    private static BrokerReleaseAsset Asset(string name)
        => new(
            name,
            new Uri($"https://github.com/darkstax/sysmon-cmdpal/releases/download/v1/{name}"),
            BrokerInstaller.MinimumAssetBytes,
            new string('A', 64));

    private static byte[] ReleaseBody(string? digest, bool includeDigest)
    {
        var asset = new Dictionary<string, object?>
        {
            ["name"] = "SysMonBroker-win-x64.exe",
            ["browser_download_url"] =
                "https://github.com/darkstax/sysmon-cmdpal/releases/download/v1/SysMonBroker-win-x64.exe",
            ["size"] = BrokerInstaller.MinimumAssetBytes,
            ["state"] = "uploaded",
        };
        if (includeDigest)
            asset["digest"] = digest;

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            draft = false,
            assets = new[] { asset },
        });
    }

    private static Task WaitForPhaseAsync(
        BrokerInstallController controller,
        BrokerInstallPhase expectedPhase)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StatusChanged += (_, _) =>
        {
            if (controller.Snapshot.Phase == expectedPhase)
                reached.TrySetResult();
        };
        return reached.Task;
    }

    private static string WritePortableExecutable(ushort machine, ushort characteristics)
    {
        byte[] image = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(image, 0x5A4D);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x80), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x94), 0x00F0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x96), characteristics);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x98), 0x020B);

        string path = Path.Combine(Path.GetTempPath(), $"broker_pe_{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, image);
        return path;
    }

    private sealed class BlockingInstaller : IBrokerInstaller
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsInstalled { get; private set; }

        public async Task<BrokerInstallFailure> InstallAsync(
            Action<BrokerInstallPhase> reportProgress,
            CancellationToken cancellationToken)
        {
            reportProgress(BrokerInstallPhase.Downloading);
            Started.SetResult();
            await _release.Task.WaitAsync(cancellationToken);
            IsInstalled = true;
            Finished.SetResult();
            return BrokerInstallFailure.None;
        }

        public void Complete() => _release.SetResult();
    }

    private sealed class CancelableInstaller : IBrokerInstaller
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsInstalled => false;

        public async Task<BrokerInstallFailure> InstallAsync(
            Action<BrokerInstallPhase> reportProgress,
            CancellationToken cancellationToken)
        {
            reportProgress(BrokerInstallPhase.Downloading);
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return BrokerInstallFailure.None;
        }
    }

    private sealed class ThrowingInstaller : IBrokerInstaller
    {
        public bool IsInstalled => false;

        public Task<BrokerInstallFailure> InstallAsync(
            Action<BrokerInstallPhase> reportProgress,
            CancellationToken cancellationToken)
        {
            reportProgress(BrokerInstallPhase.Downloading);
            throw new InvalidOperationException("test failure");
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(WaitForCancellationAsync(cancellationToken));

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
