// Copyright (c) 2026 SysMonCmdPal
// Broker release asset and executable validation helpers.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SysMonCmdPal;

internal sealed partial class BrokerInstaller
{
    internal const long MinimumAssetBytes = 64 * 1024;
    internal const long MaximumAssetBytes = 256L * 1024 * 1024;

    internal static bool TryParseGitHubSha256Digest(string? digest, out string sha256)
    {
        const string Prefix = "sha256:";
        sha256 = string.Empty;
        if (digest is null ||
            digest.Length != Prefix.Length + 64 ||
            !digest.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> hex = digest.AsSpan(Prefix.Length);
        foreach (char character in hex)
        {
            if (!IsAsciiHexDigit(character))
                return false;
        }

        sha256 = hex.ToString().ToUpperInvariant();
        return true;
    }

    internal static bool IsTrustedReleaseAssetUri(Uri uri)
    {
        if (!IsHttpsUri(uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath.StartsWith(
            "/darkstax/sysmon-cmdpal/releases/download/",
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTrustedRedirectUri(Uri uri)
    {
        if (!IsHttpsUri(uri))
            return false;

        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return IsTrustedReleaseAssetUri(uri);

        return uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ValidatePortableExecutable(string filePath, Architecture architecture)
    {
        ushort expectedMachine = architecture switch
        {
            Architecture.X64 => 0x8664,
            Architecture.Arm64 => 0xAA64,
            _ => 0,
        };
        if (expectedMachine == 0)
            return false;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 0x80 || stream.Length > MaximumAssetBytes)
                return false;

            Span<byte> dosHeader = stackalloc byte[0x40];
            if (stream.Read(dosHeader) != dosHeader.Length ||
                BinaryPrimitives.ReadUInt16LittleEndian(dosHeader) != 0x5A4D)
            {
                return false;
            }

            int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3C..]);
            if (peOffset < 0x40 || peOffset > stream.Length - 26)
                return false;

            stream.Position = peOffset;
            Span<byte> peHeader = stackalloc byte[26];
            if (stream.Read(peHeader) != peHeader.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(peHeader) != 0x00004550)
            {
                return false;
            }

            ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(peHeader[4..]);
            ushort optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(peHeader[20..]);
            ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(peHeader[22..]);
            ushort optionalHeaderMagic = BinaryPrimitives.ReadUInt16LittleEndian(peHeader[24..]);

            const ushort ExecutableImage = 0x0002;
            const ushort Dll = 0x2000;
            const ushort Pe32Plus = 0x020B;
            return machine == expectedMachine
                && optionalHeaderSize >= 2
                && optionalHeaderMagic == Pe32Plus
                && (characteristics & ExecutableImage) != 0
                && (characteristics & Dll) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<bool> VerifySha256Async(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Sha256MatchesExpected(hash, expectedSha256);
    }

    internal static bool Sha256MatchesExpected(ReadOnlySpan<byte> actualHash, string expectedSha256)
    {
        if (actualHash.Length != SHA256.HashSizeInBytes ||
            expectedSha256.Length != SHA256.HashSizeInBytes * 2 ||
            !expectedSha256.All(IsAsciiHexDigit))
        {
            return false;
        }

        byte[] expectedHash = Convert.FromHexString(expectedSha256);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static bool IsAsciiHexDigit(char character)
        => character is >= '0' and <= '9'
            or >= 'A' and <= 'F'
            or >= 'a' and <= 'f';

    private static bool IsSupportedArchitecture(Architecture architecture)
        => architecture is Architecture.X64 or Architecture.Arm64;

    private static bool IsHttpsUri(Uri uri)
        => uri.IsAbsoluteUri
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && (uri.IsDefaultPort || uri.Port == 443);
}
