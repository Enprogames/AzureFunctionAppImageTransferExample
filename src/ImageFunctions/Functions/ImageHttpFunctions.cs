using System.Buffers;
using System.Net;
using ImageFunctions.Http;
using ImageFunctions.Models;
using ImageFunctions.Storage;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ImageFunctions.Functions;

public sealed class ImageHttpFunctions
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly ImageBlobStore _store;
    private readonly ILogger<ImageHttpFunctions> _logger;

    public ImageHttpFunctions(ImageBlobStore store, ILogger<ImageHttpFunctions> logger)
    {
        _store = store;
        _logger = logger;
    }

    [Function("Health")]
    public Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        return request.CreateJsonResponseAsync(
            HttpStatusCode.OK,
            new HealthResponse("ok", DateTimeOffset.UtcNow.ToString("O")),
            AppJsonSerializerContext.Default.HealthResponse,
            cancellationToken);
    }

    [Function("UploadImage")]
    public async Task<HttpResponseData> UploadImage(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "images/{name}")] HttpRequestData request,
        string name,
        CancellationToken cancellationToken)
    {
        if (!TryValidateImageName(name, out var safeName, out var nameError))
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, nameError, cancellationToken);
        }

        byte[] bytes;
        try
        {
            bytes = await ReadBodyWithLimitAsync(request.Body, MaxImageBytes, cancellationToken);
        }
        catch (PayloadTooLargeException)
        {
            return await ErrorAsync(
                request,
                HttpStatusCode.RequestEntityTooLarge,
                $"Image must be at most {MaxImageBytes} bytes.",
                cancellationToken);
        }

        if (bytes.Length == 0)
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, "Request body is empty.", cancellationToken);
        }

        if (!TryDetectImageContentType(bytes, out var detectedContentType))
        {
            return await ErrorAsync(
                request,
                HttpStatusCode.UnsupportedMediaType,
                "Only PNG, JPEG, GIF, and WebP images are accepted by this starter.",
                cancellationToken);
        }

        var requestContentType = TryGetRequestContentType(request);
        var contentType = IsImageContentType(requestContentType) ? requestContentType! : detectedContentType;

        var stored = await _store.UploadAsync(safeName, bytes, contentType, cancellationToken);

        _logger.LogInformation(
            "Uploaded image {Name} with {SizeBytes} bytes and SHA-256 {Sha256}.",
            stored.Name,
            stored.SizeBytes,
            stored.Sha256);

        return await request.CreateJsonResponseAsync(
            HttpStatusCode.Created,
            new UploadResult(stored.Name, stored.SizeBytes, stored.Sha256),
            AppJsonSerializerContext.Default.UploadResult,
            cancellationToken);
    }

    [Function("DownloadImage")]
    public async Task<HttpResponseData> DownloadImage(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "images/{name}")] HttpRequestData request,
        string name,
        CancellationToken cancellationToken)
    {
        if (!TryValidateImageName(name, out var safeName, out var nameError))
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, nameError, cancellationToken);
        }

        var downloaded = await _store.DownloadAsync(safeName, cancellationToken);
        if (downloaded is null)
        {
            return await ErrorAsync(request, HttpStatusCode.NotFound, "Image was not found.", cancellationToken);
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", downloaded.ContentType);
        response.Headers.Add("x-content-sha256", downloaded.Sha256);
        await response.Body.WriteAsync(downloaded.Bytes, cancellationToken);
        return response;
    }

    private static Task<HttpResponseData> ErrorAsync(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string message,
        CancellationToken cancellationToken) =>
        request.CreateJsonResponseAsync(
            statusCode,
            new ErrorResponse(message),
            AppJsonSerializerContext.Default.ErrorResponse,
            cancellationToken);

    private static async Task<byte[]> ReadBodyWithLimitAsync(
        Stream body,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            long total = 0;
            while (true)
            {
                var read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBytes)
                {
                    throw new PayloadTooLargeException();
                }

                memory.Write(buffer, 0, read);
            }

            return memory.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryValidateImageName(string? candidate, out string safeName, out string error)
    {
        safeName = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Image name is required.";
            return false;
        }

        if (candidate.Length > 128)
        {
            error = "Image name must be at most 128 characters.";
            return false;
        }

        if (candidate.Contains("..", StringComparison.Ordinal) ||
            candidate.Contains('/', StringComparison.Ordinal) ||
            candidate.Contains('\\', StringComparison.Ordinal))
        {
            error = "Image name must not contain path separators or '..'.";
            return false;
        }

        foreach (var ch in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '_' and not '-')
            {
                error = "Image name may contain only ASCII letters, digits, '.', '_', and '-'.";
                return false;
            }
        }

        safeName = candidate;
        return true;
    }

    private static string? TryGetRequestContentType(HttpRequestData request)
    {
        return request.Headers.TryGetValues("Content-Type", out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static bool IsImageContentType(string? contentType) =>
        contentType is not null &&
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool TryDetectImageContentType(ReadOnlySpan<byte> bytes, out string contentType)
    {
        contentType = string.Empty;

        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> jpg = [255, 216, 255];
        ReadOnlySpan<byte> gif87 = [71, 73, 70, 56, 55, 97];
        ReadOnlySpan<byte> gif89 = [71, 73, 70, 56, 57, 97];
        ReadOnlySpan<byte> riff = [82, 73, 70, 70];
        ReadOnlySpan<byte> webp = [87, 69, 66, 80];

        if (bytes.StartsWith(png))
        {
            contentType = "image/png";
            return true;
        }

        if (bytes.StartsWith(jpg))
        {
            contentType = "image/jpeg";
            return true;
        }

        if (bytes.StartsWith(gif87) || bytes.StartsWith(gif89))
        {
            contentType = "image/gif";
            return true;
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual(riff) && bytes[8..12].SequenceEqual(webp))
        {
            contentType = "image/webp";
            return true;
        }

        return false;
    }

    private sealed class PayloadTooLargeException : Exception;
}
