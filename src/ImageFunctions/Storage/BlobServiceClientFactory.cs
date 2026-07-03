using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;

namespace ImageFunctions.Storage;

internal static class BlobServiceClientFactory
{
    public static BlobServiceClient CreateFromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable("ImageStorageConnectionString");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return new BlobServiceClient(connectionString);
        }

        var serviceUri = Environment.GetEnvironmentVariable("ImageStorageBlobServiceUri");
        if (string.IsNullOrWhiteSpace(serviceUri))
        {
            throw new InvalidOperationException(
                "Set either ImageStorageConnectionString for local development or " +
                "ImageStorageBlobServiceUri for Azure managed-identity access.");
        }

        var managedIdentityClientId =
            Environment.GetEnvironmentVariable("ImageStorageManagedIdentityClientId") ??
            Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");

        TokenCredential credential = new DefaultAzureCredential(
            new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(managedIdentityClientId)
                    ? null
                    : managedIdentityClientId,
                ExcludeInteractiveBrowserCredential = true
            });

        return new BlobServiceClient(new Uri(serviceUri), credential);
    }
}
