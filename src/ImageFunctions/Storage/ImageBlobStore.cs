using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace ImageFunctions.Storage;

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

    public async Task<StoredImage> UploadAsync(
        string name,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var sha256 = Sha256Hex(bytes);
        var blob = _container.GetBlobClient(name);

        await using var stream = new MemoryStream(bytes, writable: false);
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType
                },
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sha256"] = sha256
                }
            },
            cancellationToken);

        return new StoredImage(name, bytes.Length, sha256, contentType);
    }

    public async Task<DownloadedImage?> DownloadAsync(string name, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(name);

        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken);
            var bytes = response.Value.Content.ToArray();
            var contentType = string.IsNullOrWhiteSpace(response.Value.Details.ContentType)
                ? "application/octet-stream"
                : response.Value.Details.ContentType;

            return new DownloadedImage(bytes, contentType, Sha256Hex(bytes));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record StoredImage(string Name, long SizeBytes, string Sha256, string ContentType);

public sealed record DownloadedImage(byte[] Bytes, string ContentType, string Sha256);
