using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace ImageApi.E2E;

public sealed class ImageRoundTripTests
{
    private static readonly byte[] TestPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    [Fact]
    public async Task ImageEndpoints_WithoutLocalDevelopmentUser_ReturnUnauthorized()
    {
        var baseUrl = GetBaseUrl();
        var baseUri = new Uri(baseUrl);
        if (!baseUri.IsLoopback)
        {
            return;
        }

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        await WaitForHealthAsync(client, baseUrl);

        using var response = await client.GetAsync(new Uri($"{baseUrl}/images"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UploadThenDownload_ReturnsSameSha256Hash()
    {
        var baseUrl = GetBaseUrl();

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        await WaitForHealthAsync(client, baseUrl);

        var expectedHash = Sha256Hex(TestPng);

        using var uploadContent = new ByteArrayContent(TestPng);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var uploadRequest = CreateRequest(HttpMethod.Post, new Uri($"{baseUrl}/images"));
        uploadRequest.Content = uploadContent;

        using var uploadResponse = await client.SendAsync(uploadRequest);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        Assert.True(uploadResponse.IsSuccessStatusCode, uploadBody);

        var upload = JsonSerializer.Deserialize<UploadImageResponse>(
            uploadBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(upload);
        Assert.Equal(expectedHash, upload.Sha256);

        using var downloadRequest = CreateRequest(
            HttpMethod.Get,
            new Uri($"{baseUrl}/images/{Uri.EscapeDataString(upload.ImageId)}"));

        using var downloadResponse = await client.SendAsync(downloadRequest);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);

        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        var actualHash = Sha256Hex(downloadedBytes);

        Assert.Equal(expectedHash, actualHash);

        if (downloadResponse.Headers.TryGetValues("x-content-sha256", out var values))
        {
            Assert.Equal(expectedHash, Assert.Single(values));
        }
    }

    private static string GetBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("API_BASE_URL");
        return string.IsNullOrWhiteSpace(configured)
            ? "http://localhost:8080/api"
            : configured.TrimEnd('/');
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);

        var token = Environment.GetEnvironmentVariable("API_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        if (uri.IsLoopback)
        {
            request.Headers.Add("X-Dev-User", "e2e-user");
        }

        return request;
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
                // The container may still be starting.
            }
            catch (TaskCanceledException)
            {
                // The container may still be starting.
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"API did not become healthy at {healthUri}.");
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record UploadImageResponse(
        string ImageId,
        long SizeBytes,
        string ContentType,
        string Sha256);
}
