// Copyright (c) 2026 SysMonCmdPal
// Release discovery and Broker asset download helpers.

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SysMonCmdPal;

internal sealed partial class BrokerInstaller
{
    private const int MaximumApiResponseBytes = 2 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan ResponseBodyTimeout = TimeSpan.FromSeconds(45);
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/darkstax/sysmon-cmdpal/releases/latest");
    private static readonly HttpClient HttpClient = CreateHttpClient();

    internal static BrokerReleaseAsset? SelectAsset(
        IEnumerable<BrokerReleaseAsset> assets,
        Architecture architecture)
    {
        string? expectedName = GetExpectedAssetName(architecture);
        if (expectedName is null)
            return null;

        BrokerReleaseAsset[] matches = assets
            .Where(asset => string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    internal static BrokerInstallFailure? ClassifyLatestReleaseStatus(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.NotFound)
            return BrokerInstallFailure.NoRelease;

        return (int)statusCode is >= 200 and <= 299
            ? null
            : BrokerInstallFailure.Network;
    }

    private static async Task<BrokerReleaseAsset?> GetLatestAssetAsync(
        Architecture architecture,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        BrokerInstallFailure? responseFailure = ClassifyLatestReleaseStatus(response.StatusCode);
        if (responseFailure is not null)
            throw new BrokerInstallException(responseFailure.Value);

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);

        byte[] body = await ReadBoundedContentAsync(
            response.Content,
            MaximumApiResponseBytes,
            ResponseBodyTimeout,
            cancellationToken)
            .ConfigureAwait(false);
        return ParseLatestReleaseResponse(body, architecture);
    }

    internal static BrokerReleaseAsset? ParseLatestReleaseResponse(
        ReadOnlyMemory<byte> body,
        Architecture architecture)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("draft", out JsonElement draft))
        {
            if (draft.ValueKind == JsonValueKind.True)
                throw new BrokerInstallException(BrokerInstallFailure.NoRelease);
            if (draft.ValueKind != JsonValueKind.False)
                throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);
        }
        if (!root.TryGetProperty("assets", out JsonElement assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);
        }

        string? expectedName = GetExpectedAssetName(architecture);
        if (expectedName is null)
            return null;

        var assets = new List<BrokerReleaseAsset>();
        foreach (JsonElement item in assetsElement.EnumerateArray())
        {
            if (item.TryGetProperty("state", out JsonElement stateElement) &&
                (stateElement.ValueKind != JsonValueKind.String ||
                 !string.Equals(stateElement.GetString(), "uploaded", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!item.TryGetProperty("name", out JsonElement nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            bool isExpectedAsset = string.Equals(
                name,
                expectedName,
                StringComparison.OrdinalIgnoreCase);
            if (!item.TryGetProperty("digest", out JsonElement digestElement) ||
                digestElement.ValueKind != JsonValueKind.String ||
                !TryParseGitHubSha256Digest(digestElement.GetString(), out string expectedSha256))
            {
                if (isExpectedAsset)
                    throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);

                continue;
            }

            if (!item.TryGetProperty("browser_download_url", out JsonElement urlElement) ||
                urlElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("size", out JsonElement sizeElement) ||
                sizeElement.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            string? rawUri = urlElement.GetString();
            if (!Uri.TryCreate(rawUri, UriKind.Absolute, out Uri? downloadUri) ||
                !sizeElement.TryGetInt64(out long size))
            {
                continue;
            }

            assets.Add(new BrokerReleaseAsset(name, downloadUri, size, expectedSha256));
        }

        BrokerReleaseAsset? selected = SelectAsset(assets, architecture);
        if (selected is null)
            return null;
        if (selected.Value.Size < MinimumAssetBytes || selected.Value.Size > MaximumAssetBytes ||
            !IsTrustedReleaseAssetUri(selected.Value.DownloadUri))
        {
            throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);
        }

        return selected;
    }

    private static async Task DownloadAssetAsync(
        BrokerReleaseAsset asset,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Uri currentUri = asset.DownloadUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            using HttpResponseMessage response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                Uri? location = response.Headers.Location;
                if (location is null || redirect == MaximumRedirects)
                    throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);

                Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!IsTrustedRedirectUri(nextUri))
                    throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);

                currentUri = nextUri;
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new BrokerInstallException(BrokerInstallFailure.Network);

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is not null && contentLength.Value != asset.Size)
                throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith(
                "text/",
                StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);
            }

            await WriteAssetBodyAsync(
                response.Content,
                destinationPath,
                asset.Size,
                ResponseBodyTimeout,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);
    }

    internal static async Task WriteAssetBodyAsync(
        HttpContent content,
        string destinationPath,
        long expectedSize,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSize);
        if (expectedSize > MaximumAssetBytes)
            throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);

        using CancellationTokenSource bodyDeadline = CreateBodyDeadline(
            cancellationToken,
            timeout);
        try
        {
            CancellationToken bodyToken = bodyDeadline.Token;
            await using Stream input = await content.ReadAsStreamAsync(bodyToken).ConfigureAwait(false);
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[64 * 1024];
            long totalBytes = 0;
            while (true)
            {
                int bytesRead = await input.ReadAsync(buffer.AsMemory(), bodyToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                totalBytes += bytesRead;
                if (totalBytes > MaximumAssetBytes || totalBytes > expectedSize)
                    throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), bodyToken)
                    .ConfigureAwait(false);
            }

            await output.FlushAsync(bodyToken).ConfigureAwait(false);
            if (totalBytes != expectedSize)
                throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Broker asset response body deadline exceeded.", ex);
        }
    }

    internal static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        int maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (content.Headers.ContentLength > maximumBytes)
            throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);

        using CancellationTokenSource bodyDeadline = CreateBodyDeadline(cancellationToken, timeout);
        try
        {
            CancellationToken bodyToken = bodyDeadline.Token;
            await using Stream stream = await content.ReadAsStreamAsync(bodyToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(), bodyToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                    break;
                if (output.Length + bytesRead > maximumBytes)
                    throw new BrokerInstallException(BrokerInstallFailure.DownloadInvalid);

                output.Write(buffer, 0, bytesRead);
            }

            return output.ToArray();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub API response body deadline exceeded.", ex);
        }
    }

    private static CancellationTokenSource CreateBodyDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static string? GetExpectedAssetName(Architecture architecture)
    {
        string? runtimeIdentifier = architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => null,
        };
        return runtimeIdentifier is null
            ? null
            : $"SysMonBroker-{runtimeIdentifier}.exe";
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SysMonCmdPal", "1.5"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
