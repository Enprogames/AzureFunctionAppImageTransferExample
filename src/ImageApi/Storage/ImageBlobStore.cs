using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ImageApi.Auth;
using ImageApi.Models;

namespace ImageApi.Storage;

public sealed class ImageBlobStore
{
    private readonly BlobContainerClient _container;

    public ImageBlobStore(BlobServiceClient blobServiceClient)
    {
        var containerName = Environment.GetEnvironmentVariable("ImageStorageContainerName");
        if (string.IsNullOrWhiteSpace(containerName))
        {
            containerName = "images";
        }

        _container = blobServiceClient.GetBlobContainerClient(containerName);
    }

    public async Task<ImageSummary[]> ListAsync(CurrentUser owner, CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var results = new List<ImageSummary>();
        var prefix = OwnerPrefix(owner);

        await foreach (var blob in _container.GetBlobsAsync(
            traits: BlobTraits.Metadata,
            states: BlobStates.None,
            prefix: prefix,
            cancellationToken: cancellationToken))
        {
            var imageId = blob.Name[prefix.Length..];
            var contentType = blob.Properties.ContentType ?? "application/octet-stream";
            var size = blob.Properties.ContentLength ?? 0;
            var created = blob.Properties.CreatedOn ?? DateTimeOffset.UnixEpoch;

            blob.Metadata.TryGetValue("sha256", out var sha256);

            results.Add(new ImageSummary(
                imageId,
                size,
                contentType,
                sha256 ?? string.Empty,
                created.ToString("O")));
        }

        return results
            .OrderByDescending(static image => image.CreatedUtc, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<StoredImage> UploadAsync(
        CurrentUser owner,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var imageId = $"{Guid.NewGuid():N}{ExtensionForContentType(contentType)}";
        var blobName = BlobNameFor(owner, imageId);
        var sha256 = Sha256Hex(bytes);
        var createdUtc = DateTimeOffset.UtcNow;

        await using var stream = new MemoryStream(bytes, writable: false);
        await _container.GetBlobClient(blobName).UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType
                },
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["owner_hash"] = owner.OwnerHash,
                    ["sha256"] = sha256,
                    ["created_utc"] = createdUtc.ToString("O")
                }
            },
            cancellationToken);

        return new StoredImage(imageId, bytes.Length, contentType, sha256);
    }

    public async Task<DownloadedImage?> DownloadAsync(
        CurrentUser owner,
        string imageId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeImageId(imageId))
        {
            return null;
        }

        var blob = _container.GetBlobClient(BlobNameFor(owner, imageId));

        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken);
            var bytes = response.Value.Content.ToArray();
            var contentType = string.IsNullOrWhiteSpace(response.Value.Details.ContentType)
                ? "application/octet-stream"
                : response.Value.Details.ContentType;

            return new DownloadedImage(imageId, bytes, contentType, Sha256Hex(bytes));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static string OwnerPrefix(CurrentUser owner) => $"users/{owner.OwnerHash}/";

    private static string BlobNameFor(CurrentUser owner, string imageId) => OwnerPrefix(owner) + imageId;

    private static bool IsSafeImageId(string imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId) || imageId.Length > 160)
        {
            return false;
        }

        if (imageId.Contains("..", StringComparison.Ordinal) ||
            imageId.Contains('/', StringComparison.Ordinal) ||
            imageId.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return imageId.All(static ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    private static string ExtensionForContentType(string contentType) =>
        contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".bin"
        };

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record StoredImage(string ImageId, long SizeBytes, string ContentType, string Sha256);

public sealed record DownloadedImage(string ImageId, byte[] Bytes, string ContentType, string Sha256);
