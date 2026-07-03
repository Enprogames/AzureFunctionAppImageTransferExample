using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Azure.Functions.Worker.Http;

namespace ImageFunctions.Http;

internal static class HttpJson
{
    public static async Task<HttpResponseData> CreateJsonResponseAsync<T>(
        this HttpRequestData request,
        HttpStatusCode statusCode,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        var response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await JsonSerializer.SerializeAsync(response.Body, value, jsonTypeInfo, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        return response;
    }
}
