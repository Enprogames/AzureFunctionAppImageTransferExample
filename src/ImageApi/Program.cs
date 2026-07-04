using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ImageApi.Auth;
using ImageApi.Models;
using ImageApi.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(static options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddSingleton(static _ => BlobServiceClientFactory.CreateFromEnvironment());
builder.Services.AddSingleton<ImageBlobStore>();

var useDevelopmentAuth =
    builder.Environment.IsDevelopment() &&
    string.Equals(
        builder.Configuration["Authentication:UseDevelopmentUser"],
        "true",
        StringComparison.OrdinalIgnoreCase);

if (!useDevelopmentAuth)
{
    var tenantId = builder.Configuration["Authentication:TenantId"];
    var audience = builder.Configuration["Authentication:Audience"];

    if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(audience))
    {
        throw new InvalidOperationException(
            "Authentication:TenantId and Authentication:Audience are required outside development auth mode.");
    }

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            options.Audience = audience;
            options.MapInboundClaims = false;
        });

    builder.Services.AddAuthorization();
}

var app = builder.Build();

if (useDevelopmentAuth)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/images"))
        {
            var devUser = context.Request.Headers["X-Dev-User"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(devUser))
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Set X-Dev-User for local development requests.");
                return;
            }

            context.Items[CurrentUser.HttpContextItemKey] = CurrentUser.FromDevelopmentUser(devUser);
        }

        await next(context);
    });
}
else
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapGet("/api/health", (RequestDelegate)HealthAsync);

var images = app.MapGroup("/api/images");
if (!useDevelopmentAuth)
{
    images.RequireAuthorization();
}

images.MapGet("", (RequestDelegate)ListImagesAsync);
images.MapPost("", (RequestDelegate)UploadImageAsync);
images.MapGet("/{imageId}", (RequestDelegate)DownloadImageAsync);

app.Run();

static Task HealthAsync(HttpContext context)
{
    return WriteJsonAsync(
        context,
        StatusCodes.Status200OK,
        new HealthResponse("ok", DateTimeOffset.UtcNow.ToString("O")),
        AppJsonSerializerContext.Default.HealthResponse);
}

static async Task ListImagesAsync(HttpContext context)
{
    var store = context.RequestServices.GetRequiredService<ImageBlobStore>();
    var user = CurrentUser.FromHttpContext(context);
    var userImages = await store.ListAsync(user, context.RequestAborted);

    await WriteJsonAsync(
        context,
        StatusCodes.Status200OK,
        userImages,
        AppJsonSerializerContext.Default.ImageSummaryArray);
}

static async Task UploadImageAsync(HttpContext context)
{
    const long maxImageBytes = 10 * 1024 * 1024;

    byte[] bytes;
    try
    {
        bytes = await ReadBodyWithLimitAsync(context.Request.Body, maxImageBytes, context.RequestAborted);
    }
    catch (PayloadTooLargeException)
    {
        await WriteErrorAsync(
            context,
            StatusCodes.Status413PayloadTooLarge,
            $"Image must be at most {maxImageBytes} bytes.");
        return;
    }

    if (bytes.Length == 0)
    {
        await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Request body is empty.");
        return;
    }

    if (!TryDetectImageContentType(bytes, out var detectedContentType))
    {
        await WriteErrorAsync(
            context,
            StatusCodes.Status415UnsupportedMediaType,
            "Only PNG, JPEG, GIF, and WebP images are accepted.");
        return;
    }

    var requestContentType = context.Request.ContentType;
    var contentType = IsImageContentType(requestContentType) ? requestContentType! : detectedContentType;
    var user = CurrentUser.FromHttpContext(context);
    var store = context.RequestServices.GetRequiredService<ImageBlobStore>();
    var stored = await store.UploadAsync(user, bytes, contentType, context.RequestAborted);

    context.Response.Headers.Location = $"/api/images/{Uri.EscapeDataString(stored.ImageId)}";
    await WriteJsonAsync(
        context,
        StatusCodes.Status201Created,
        new UploadImageResponse(stored.ImageId, stored.SizeBytes, stored.ContentType, stored.Sha256),
        AppJsonSerializerContext.Default.UploadImageResponse);
}

static async Task DownloadImageAsync(HttpContext context)
{
    var imageId = context.Request.RouteValues["imageId"]?.ToString();
    if (string.IsNullOrWhiteSpace(imageId))
    {
        await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Image ID is required.");
        return;
    }

    var user = CurrentUser.FromHttpContext(context);
    var store = context.RequestServices.GetRequiredService<ImageBlobStore>();
    var image = await store.DownloadAsync(user, imageId, context.RequestAborted);
    if (image is null)
    {
        await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Image was not found.");
        return;
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = image.ContentType;
    context.Response.ContentLength = image.Bytes.Length;
    context.Response.Headers["x-content-sha256"] = image.Sha256;
    await context.Response.Body.WriteAsync(image.Bytes, context.RequestAborted);
}

static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
{
    return WriteJsonAsync(
        context,
        statusCode,
        new ErrorResponse(message),
        AppJsonSerializerContext.Default.ErrorResponse);
}

static async Task WriteJsonAsync<T>(
    HttpContext context,
    int statusCode,
    T response,
    JsonTypeInfo<T> jsonTypeInfo)
{
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "application/json; charset=utf-8";
    await JsonSerializer.SerializeAsync(
        context.Response.Body,
        response,
        jsonTypeInfo,
        context.RequestAborted);
}

static async Task<byte[]> ReadBodyWithLimitAsync(
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

static bool IsImageContentType(string? contentType) =>
    contentType is not null &&
    contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

static bool TryDetectImageContentType(ReadOnlySpan<byte> bytes, out string contentType)
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

sealed class PayloadTooLargeException : Exception;
