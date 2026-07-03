using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Xunit;

namespace ImageFunctions.E2E;

public sealed class ImageRoundTripTests
{
    private const string LocalDockerFunctionKey = "local-dev-function-key";

    // A tiny deterministic PNG. The test hashes bytes before upload and after download.
    private static readonly byte[] TestPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    [Fact]
    public async Task UploadThenDownload_ReturnsTheSameSha256Hash()
    {
        var baseUrl = GetBaseUrl();
        var functionKey = GetFunctionKey(baseUrl);

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        await WaitForHealthAsync(client, baseUrl);

        var imageName = $"e2e-{Guid.NewGuid():N}.png";
        var expectedHash = Sha256Hex(TestPng);

        using var content = new ByteArrayContent(TestPng);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var uploadResponse = await client.PostAsync(
            BuildEndpoint(baseUrl, $"images/{Uri.EscapeDataString(imageName)}", functionKey),
            content);

        var uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        Assert.True(uploadResponse.IsSuccessStatusCode, uploadBody);

        var downloadResponse = await client.GetAsync(
            BuildEndpoint(baseUrl, $"images/{Uri.EscapeDataString(imageName)}", functionKey));

        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);

        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        var actualHash = Sha256Hex(downloadedBytes);

        Assert.Equal(expectedHash, actualHash);

        if (downloadResponse.Headers.TryGetValues("x-content-sha256", out var headerValues))
        {
            Assert.Equal(expectedHash, Assert.Single(headerValues));
        }
    }

    private static string GetBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("FUNCTION_BASE_URL");
        return string.IsNullOrWhiteSpace(configured)
            ? "http://localhost:7071/api"
            : configured.TrimEnd('/');
    }

    private static string? GetFunctionKey(string baseUrl)
    {
        var configured = Environment.GetEnvironmentVariable("FUNCTION_KEY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var uri = new Uri(baseUrl);
        return uri.IsLoopback ? LocalDockerFunctionKey : null;
    }

    private static Uri BuildEndpoint(string baseUrl, string relativePath, string? functionKey)
    {
        var url = $"{baseUrl}/{relativePath}";

        if (!string.IsNullOrWhiteSpace(functionKey))
        {
            url += url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            url += "code=" + Uri.EscapeDataString(functionKey);
        }

        return new Uri(url);
    }

    private static async Task WaitForHealthAsync(HttpClient client, string baseUrl)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var healthUri = new Uri($"{baseUrl}/health");

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(healthUri);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Host may still be starting.
            }
            catch (TaskCanceledException)
            {
                // Host may still be starting.
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"Function host did not become healthy at {healthUri}.");
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
