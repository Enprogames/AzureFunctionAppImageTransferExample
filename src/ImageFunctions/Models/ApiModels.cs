using System.Text.Json.Serialization;

namespace ImageFunctions.Models;

public sealed record UploadResult(string Name, long SizeBytes, string Sha256);

public sealed record ErrorResponse(string Error);

public sealed record HealthResponse(string Status, string Utc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UploadResult))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class AppJsonSerializerContext : JsonSerializerContext;
