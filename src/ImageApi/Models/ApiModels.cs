using System.Text.Json.Serialization;

namespace ImageApi.Models;

public sealed record HealthResponse(string Status, string Utc);

public sealed record ErrorResponse(string Error);

public sealed record ImageSummary(
    string ImageId,
    long SizeBytes,
    string ContentType,
    string Sha256,
    string CreatedUtc);

public sealed record UploadImageResponse(
    string ImageId,
    long SizeBytes,
    string ContentType,
    string Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ImageSummary))]
[JsonSerializable(typeof(ImageSummary[]))]
[JsonSerializable(typeof(UploadImageResponse))]
internal sealed partial class AppJsonSerializerContext : JsonSerializerContext;
