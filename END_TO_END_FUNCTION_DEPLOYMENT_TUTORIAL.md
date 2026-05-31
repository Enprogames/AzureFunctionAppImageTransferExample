# End-to-end Azure Functions deployment tutorial: .NET 10, C# 14, Native AOT, Bicep, Docker, Azurite, and cloud verification

> Status: written for the Azure Functions and .NET ecosystem as of **May 1, 2026**. I could not run this tutorial against a live Azure subscription from this environment, so treat the code as a complete starter implementation plus deployment guide that you should validate in your Azure tenant. The commands are intentionally explicit so failures are easy to diagnose.

## What you will build

You will build a small but production-shaped starter application:

1. **Upload function**: `POST /api/images/{name}` uploads an image into Azure Blob Storage.
2. **Download function**: `GET /api/images/{name}` downloads the same image.
3. **Health function**: `GET /api/health` helps local Docker and E2E tests wait for the host.
4. **End-to-end test**: generates a deterministic PNG, hashes it with SHA-256, uploads it, downloads it, hashes the result, and verifies both hashes match.
5. **Local Docker path**: runs the Azure Functions host in a .NET 10 isolated Functions container and uses Azurite for Blob/Queue/Table storage.
6. **Cloud path**: provisions Azure infrastructure with Bicep from your local machine, deploys the Native AOT function package, then runs the same E2E test against the deployed endpoint.

The key design decisions are:

- Use **Azure Functions runtime v4** with the **.NET isolated worker model**. .NET 10 is supported in the isolated worker model, while the in-process model is legacy and approaching end of support.
- Use **Flex Consumption** for Linux hosting. Modern .NET versions beyond the legacy Linux Consumption support path should use Flex Consumption.
- Use **Native AOT** for the function worker process. The Docker build produces a `linux-x64` Native AOT publish output.
- Use **Bicep + Azure CLI** for infrastructure-as-code. This is the most direct local deployment path for Azure-native projects, avoids a separate state file, and works well for least-privilege resource-group scoped deployments.
- Use **managed identity + Azure RBAC** in Azure, not storage keys or connection strings.
- Use **Azurite + connection strings only for local development/testing**.
- Use **separate host storage and application image storage**. Host storage contains Functions runtime/deployment/key material; image storage contains your uploaded test images. This separation makes least privilege easier to reason about.
- Keep production infrastructure explicit. Bicep is verbose, but it makes identity, RBAC scope, storage separation, deployment storage, and scale settings reviewable.
- Treat .NET Aspire as an optional developer harness, not the production source of truth for this starter.
- Preserve adapter boundaries so future AWS Lambda/S3 support can be added without rewriting application behavior.

## Research-backed platform notes

The tutorial is based on current Microsoft guidance and package information:

- Azure Functions v4 supports .NET 10 in the isolated worker model. The isolated model gives your app its own process and lets it target .NET versions independently from the Functions host runtime.
- .NET 10 Functions apps cannot run on the old Linux Consumption plan; use Flex Consumption for Linux serverless hosting.
- The .NET isolated worker guide lists `Microsoft.Azure.Functions.Worker` `2.50.0+` and `Microsoft.Azure.Functions.Worker.Sdk` `2.0.5+` as the minimum versions for .NET 10. This tutorial uses newer package versions from the Azure SDK package index.
- Native AOT compiles IL to native code at publish time and targets a specific runtime environment such as Linux x64. Linux Native AOT publishing is easiest and most reproducible from Linux, so this tutorial uses Docker for the publish step.
- Azure Storage recommends Microsoft Entra ID and managed identities over Shared Key authorization whenever possible.
- Azure Functions supports identity-based `AzureWebJobsStorage` settings for the Functions host storage account.
- Bicep can be deployed locally with Azure CLI using `az deployment group create`; `what-if` previews the changes before applying them.
- Resource-group deletion is the cleanest teardown model for isolated starter environments.

See the **References** section at the end for the source links.

## Architecture stance

This tutorial intentionally uses Azure Functions Flex Consumption, Bicep, managed identity, and explicit RBAC as the production deployment blueprint.

.NET Aspire is valuable for local orchestration, dashboards, logs, traces, emulator wiring, and developer experience. It is not the primary deployment mechanism in this tutorial because the current Azure Functions + Aspire deployment path is container-oriented, while this tutorial targets Flex Consumption package deployment.

AWS Lambda parity is not a goal of this tutorial. The code should still preserve clean application boundaries so an AWS adapter can be added later, but Azure correctness comes first.

---

## 1. Recommended answer to your architecture questions

### 1.1 Best way to connect to the cloud setup and deploy infrastructure

For this starter, use **Azure CLI + Bicep** from your local machine:

```bash
az login
az account set --subscription "<subscription-id>"
az group create --name "rg-image-fn-dev" --location "canadacentral"
az deployment group what-if \
  --resource-group "rg-image-fn-dev" \
  --template-file infra/main.bicep \
  --parameters location="canadacentral" resourcePrefix="imgfn"
az deployment group create \
  --resource-group "rg-image-fn-dev" \
  --template-file infra/main.bicep \
  --parameters location="canadacentral" resourcePrefix="imgfn"
```

Why this is the best starter path:

- **Bicep is Azure-native**: strong typing, first-class Azure resource support, no state file to protect, and easy inspection through Azure Resource Manager deployment history.
- **Azure CLI is enough locally**: no CI/CD system is required for the starter, but the same commands are easy to move to GitHub Actions or Azure DevOps later.
- **Least privilege is straightforward**: scope the deployment identity to a single resource group instead of a whole subscription.
- **Teardown is easy**: put every resource in one dedicated resource group and delete the resource group.

Use **Azure Developer CLI (`azd`)** later if you want a one-command Azure workflow over the same infrastructure concepts. Use **.NET Aspire** later if you want local orchestration, dashboarding, service discovery, and emulator/container coordination. For this tutorial, Aspire should be an optional developer harness rather than the production deployment source of truth. Use **Terraform** if your organization standardizes on Terraform state/governance workflows. For this Azure-first starter, Bicep remains the most direct production infrastructure contract.

### 1.2 How to ensure local deployment only has necessary permissions

Use a dedicated deployment identity scoped to one resource group. Do not use your daily human account as subscription Owner for normal deployments.

The Bicep file in this tutorial creates Azure RBAC role assignments for the Function App's managed identity. A deployment principal therefore needs:

1. Resource write permissions for the resources being created.
2. `Microsoft.Resources/deployments/*` permissions for ARM/Bicep deployments.
3. Role-assignment write/delete permissions at the scope where Bicep assigns roles.

A practical least-privilege approach is:

- A subscription Owner or RBAC administrator performs a **one-time bootstrap**.
- That bootstrap creates the resource group.
- That bootstrap creates a dedicated service principal or workload identity.
- The deployer gets **Contributor** only on the resource group.
- The deployer also gets a role that can create the needed Azure RBAC assignments at that same resource-group scope, commonly **Role Based Access Control Administrator** or **User Access Administrator**. If your organization supports custom roles, use a custom role restricted to the role-assignment actions and role definitions you actually need.

Example bootstrap commands, run by an administrator:

```bash
SUBSCRIPTION_ID="<subscription-id>"
RESOURCE_GROUP="rg-image-fn-dev"
LOCATION="canadacentral"
DEPLOYER_NAME="sp-imgfn-dev-deployer"

az account set --subscription "$SUBSCRIPTION_ID"
az group create --name "$RESOURCE_GROUP" --location "$LOCATION"
RG_ID="$(az group show --name "$RESOURCE_GROUP" --query id -o tsv)"

# Creates an app registration/service principal and assigns Contributor at the RG scope.
az ad sp create-for-rbac \
  --name "$DEPLOYER_NAME" \
  --role "Contributor" \
  --scopes "$RG_ID" \
  --years 1

# Look up the service principal object ID.
APP_ID="<appId-from-previous-output>"
SP_OBJECT_ID="$(az ad sp show --id "$APP_ID" --query id -o tsv)"

# Allow Bicep to create the managed-identity role assignments, scoped only to this RG.
az role assignment create \
  --assignee-object-id "$SP_OBJECT_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "User Access Administrator" \
  --scope "$RG_ID"
```

Then normal local deployment uses the service principal:

```bash
az login --service-principal \
  --username "<appId>" \
  --password "<password>" \
  --tenant "<tenantId>"
az account set --subscription "<subscription-id>"
```

Security notes:

- Keep the service-principal secret out of source control. Prefer workload identity federation in CI/CD.
- Scope deployment access to the resource group, not the subscription.
- Do not grant the local deployer storage account key access. In Azure, this tutorial disables shared key access for cloud storage accounts and uses managed identity.
- Do not publish `local.settings.json`; use `local.settings.json.example` in source control.

### 1.3 How to teardown all infrastructure

Because everything is in a dedicated resource group, teardown is one command:

```bash
az group delete --name "rg-image-fn-dev" --yes --no-wait
```

Also remove local resources:

```bash
docker compose down -v
rm -rf .artifacts
```

If you created a temporary deployment service principal, remove it too:

```bash
az ad sp delete --id "<appId>"
```

For production environments, you may want deployment stacks, locks, or policy-controlled cleanup. For this isolated starter, a dedicated resource group is the most reliable teardown boundary.

---

## 2. Prerequisites

Install these locally:

- **.NET 10 SDK**
- **Azure Functions Core Tools v4**
- **Azure CLI**
- **Docker Desktop** or Docker Engine with BuildKit/buildx
- **zip** on Linux/macOS, or PowerShell `Compress-Archive` on Windows
- An Azure subscription
- Permission to create a resource group and deploy Bicep into it

Verify:

```bash
dotnet --info
func --version
az --version
docker version
docker buildx version
```

Native AOT note: If you publish directly on Linux, install the native compiler dependencies:

```bash
sudo apt-get update
sudo apt-get install -y clang zlib1g-dev zip
```

This tutorial uses Docker for Native AOT publishing so that Windows, macOS, and Linux developers all produce the same `linux-x64` output.

---

## 3. Repository layout

Create this layout:

```text
AzureFunctionAppImageTransferExample/
  .gitignore
  Directory.Packages.props
  global.json
  docker-compose.yml
  infra/
    main.bicep
  scripts/
    preflight.sh
    deploy-infra.sh
    publish-aot-zip.sh
    get-function-key.sh
    teardown-cloud.sh
  src/
    ImageFunctions/
      Dockerfile
      ImageFunctions.csproj
      Program.cs
      host.json
      local.settings.json.example
      Functions/
        ImageHttpFunctions.cs
      Storage/
        BlobServiceClientFactory.cs
        ImageBlobStore.cs
      Http/
        HttpJson.cs
      Models/
        ApiModels.cs
  tests/
    ImageFunctions.E2E/
      ImageFunctions.E2E.csproj
      ImageRoundTripTests.cs
```

Use the existing `AzureFunctionAppImageTransferExample` directory as the root directory. From the repository root, create the tutorial subdirectories:

```bash
cd AzureFunctionAppImageTransferExample
mkdir -p infra scripts src/ImageFunctions/{Functions,Storage,Http,Models} tests/ImageFunctions.E2E
```

---

## 4. Root files

### 4.1 `global.json`

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

### 4.2 `Directory.Packages.props`

Central package management makes versions explicit and consistent.

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <PackageVersion Include="Microsoft.Azure.Functions.Worker" Version="2.52.0" />
    <PackageVersion Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.0.7" />
    <PackageVersion Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.3.0" />
    <PackageVersion Include="Azure.Identity" Version="1.21.0" />
    <PackageVersion Include="Azure.Storage.Blobs" Version="12.27.0" />

    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
</Project>
```

Package notes:

- `Microsoft.Azure.Functions.Worker` and `Microsoft.Azure.Functions.Worker.Sdk` are the .NET isolated worker core packages.
- `Microsoft.Azure.Functions.Worker.Extensions.Http` enables HTTP triggers for isolated Functions.
- `Azure.Identity` and `Azure.Storage.Blobs` let the app use managed identity in Azure and connection strings locally.

### 4.3 `.gitignore`

```gitignore
bin/
obj/
.artifacts/
local.settings.json
*.user
*.suo
.vs/
.vscode/
TestResults/
```

---

## 5. Function App project

### 5.1 `src/ImageFunctions/ImageFunctions.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>14.0</LangVersion>

    <!-- Native AOT. The actual target RID is supplied at publish time. -->
    <PublishAot>true</PublishAot>
    <SelfContained>true</SelfContained>
    <InvariantGlobalization>true</InvariantGlobalization>
    <IsAotCompatible>true</IsAotCompatible>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>

    <!-- Helpful for smaller Release AOT output. Do not set these for Debug. -->
    <DebuggerSupport Condition="'$(Configuration)' == 'Release'">false</DebuggerSupport>
    <EventSourceSupport Condition="'$(Configuration)' == 'Release'">false</EventSourceSupport>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" OutputItemType="Analyzer" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" />
    <PackageReference Include="Azure.Identity" />
    <PackageReference Include="Azure.Storage.Blobs" />
  </ItemGroup>

  <ItemGroup>
    <None Update="host.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="local.settings.json" CopyToOutputDirectory="Never" CopyToPublishDirectory="Never" />
  </ItemGroup>
</Project>
```

AOT guidance:

- Keep dependencies modest.
- Avoid reflection-heavy frameworks for the first version.
- Use source-generated JSON metadata.
- Treat AOT/trim warnings seriously. After your first successful baseline, consider turning important IL warnings into errors in CI.

### 5.2 `src/ImageFunctions/host.json`

```json
{
  "version": "2.0",
  "logging": {
    "applicationInsights": {
      "samplingSettings": {
        "isEnabled": true,
        "excludedTypes": "Request"
      }
    }
  },
  "extensions": {
    "http": {
      "routePrefix": "api"
    }
  }
}
```

### 5.3 `src/ImageFunctions/local.settings.json.example`

Do not commit the real `local.settings.json`. Use this example for local non-Docker development.

```json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureWebJobsStorage": "DefaultEndpointsProtocol=http;AccountName=localstore;AccountKey=<key here>;BlobEndpoint=http://127.0.0.1:10000/localstore;QueueEndpoint=http://127.0.0.1:10001/localstore;TableEndpoint=http://127.0.0.1:10002/localstore;",
    "ImageStorageConnectionString": "DefaultEndpointsProtocol=http;AccountName=localstore;AccountKey=<key here>;BlobEndpoint=http://127.0.0.1:10000/localstore;QueueEndpoint=http://127.0.0.1:10001/localstore;TableEndpoint=http://127.0.0.1:10002/localstore;",
    "ImageStorageContainerName": "images"
  }
}
```

Create your local file when needed:

```bash
cp src/ImageFunctions/local.settings.json.example src/ImageFunctions/local.settings.json
```

---

## 6. Function code

### 6.1 `src/ImageFunctions/Program.cs`

```csharp
using ImageFunctions.Storage;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWorkerDefaults();

builder.Services.AddSingleton(static _ => BlobServiceClientFactory.CreateFromEnvironment());
builder.Services.AddSingleton<ImageBlobStore>();

builder.Build().Run();
```

### 6.2 `src/ImageFunctions/Models/ApiModels.cs`

```csharp
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
```

### 6.3 `src/ImageFunctions/Http/HttpJson.cs`

```csharp
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
```

This avoids reflection-based JSON serialization, which is important for Native AOT.

### 6.4 `src/ImageFunctions/Storage/BlobServiceClientFactory.cs`

```csharp
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
```

Local development uses a connection string pointing at Azurite. Cloud uses managed identity and an HTTPS Blob service URI.

### 6.5 `src/ImageFunctions/Storage/ImageBlobStore.cs`

```csharp
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace ImageFunctions.Storage;

internal sealed class ImageBlobStore
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

internal sealed record StoredImage(string Name, long SizeBytes, string Sha256, string ContentType);

internal sealed record DownloadedImage(byte[] Bytes, string ContentType, string Sha256);
```

### 6.6 `src/ImageFunctions/Functions/ImageHttpFunctions.cs`

```csharp
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
```

---

## 7. Local Docker runtime

### 7.1 `src/ImageFunctions/Dockerfile`

```dockerfile
# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /repo

COPY global.json Directory.Packages.props ./
COPY src/ImageFunctions/ImageFunctions.csproj src/ImageFunctions/
RUN dotnet restore src/ImageFunctions/ImageFunctions.csproj -r linux-x64

COPY src/ImageFunctions/ src/ImageFunctions/

RUN dotnet publish src/ImageFunctions/ImageFunctions.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o /out \
    /p:PublishAot=true \
    /p:InvariantGlobalization=true

# Export target used by scripts/publish-aot-zip.sh to copy the publish directory
# out of Docker without running the Functions container.
FROM scratch AS export
COPY --from=build /out /

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0 AS runtime

ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true

COPY --from=build /out /home/site/wwwroot
```

This Dockerfile has two jobs:

1. Build a Native AOT `linux-x64` publish output.
2. Run that output inside the official Azure Functions .NET isolated runtime image.

### 7.2 `docker-compose.yml`

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite:latest
    command: >
      azurite
      --blobHost 0.0.0.0
      --queueHost 0.0.0.0
      --tableHost 0.0.0.0
      --disableTelemetry
    environment:
      AZURITE_ACCOUNTS: "localstore:obaZPyQhWYVTNDKYRcjb8O5HQMEfyIbbb6jOzaUSkYQmYCe18bzXILx3gkN4J4BSdIaZpdB+FFyzu19tpaj5pw=="
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"
    volumes:
      - azurite-data:/data

  functions:
    build:
      context: .
      dockerfile: src/ImageFunctions/Dockerfile
      target: runtime
    ports:
      - "7071:80"
    environment:
      FUNCTIONS_WORKER_RUNTIME: "dotnet-isolated"
      AzureWebJobsStorage: "DefaultEndpointsProtocol=http;AccountName=localstore;AccountKey=<key here>;BlobEndpoint=http://azurite:10000/localstore;QueueEndpoint=http://azurite:10001/localstore;TableEndpoint=http://azurite:10002/localstore;"
      ImageStorageConnectionString: "DefaultEndpointsProtocol=http;AccountName=localstore;AccountKey=<key here>;BlobEndpoint=http://azurite:10000/localstore;QueueEndpoint=http://azurite:10001/localstore;TableEndpoint=http://azurite:10002/localstore;"
      ImageStorageContainerName: "images"
      AzureFunctionsJobHost__Logging__Console__IsEnabled: "true"
    depends_on:
      - azurite

volumes:
  azurite-data:
```

Start locally:

```bash
docker compose up -d --build
curl -i http://localhost:7071/api/health
```

View logs:

```bash
docker compose logs -f functions
```

Stop and remove local data:

```bash
docker compose down -v
```

---

## 8. End-to-end test project

### 8.1 `tests/ImageFunctions.E2E/ImageFunctions.E2E.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>14.0</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

### 8.2 `tests/ImageFunctions.E2E/ImageRoundTripTests.cs`

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Xunit;

namespace ImageFunctions.E2E;

public sealed class ImageRoundTripTests
{
    // A tiny deterministic PNG. The test hashes bytes before upload and after download.
    private static readonly byte[] TestPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    [Fact]
    public async Task UploadThenDownload_ReturnsTheSameSha256Hash()
    {
        var baseUrl = GetBaseUrl();
        var functionKey = Environment.GetEnvironmentVariable("FUNCTION_KEY");

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
```

### 8.3 Run the E2E test locally through Docker

```bash
docker compose up -d --build
FUNCTION_BASE_URL="http://localhost:7071/api" dotnet test tests/ImageFunctions.E2E/ImageFunctions.E2E.csproj
```

No `FUNCTION_KEY` is required for local Docker testing in this setup.

---

## 9. Infrastructure as code with Bicep

### 9.1 `infra/main.bicep`

```bicep
targetScope = 'resourceGroup'

@description('Azure region. Use a region that supports Azure Functions Flex Consumption.')
param location string = resourceGroup().location

@minLength(3)
@maxLength(16)
@description('Short lowercase prefix for Azure resource names. Use lowercase letters and digits for best results.')
param resourcePrefix string = 'imgfn'

@description('Azure Functions worker runtime name.')
param functionAppRuntime string = 'dotnet-isolated'

@description('Target .NET runtime version for the function worker.')
param functionAppRuntimeVersion string = '10'

@allowed([
  512
  2048
  4096
])
@description('Flex Consumption instance memory size in MB.')
param instanceMemoryMB int = 2048

@minValue(40)
@maxValue(1000)
@description('Maximum number of Flex Consumption instances.')
param maximumInstanceCount int = 100

@description('Blob container used by the application for uploaded images.')
param imageContainerName string = 'images'

var suffix = toLower(uniqueString(resourceGroup().id, resourcePrefix))
var functionAppName = '${resourcePrefix}-${suffix}'
var planName = '${resourcePrefix}-fc-${suffix}'
var identityName = '${resourcePrefix}-id-${suffix}'
var logAnalyticsName = '${resourcePrefix}-law-${suffix}'
var appInsightsName = '${resourcePrefix}-appi-${suffix}'

// Storage account names must be globally unique, lowercase, alphanumeric, and <= 24 chars.
var hostStorageAccountName = 'host${take(suffix, 20)}'
var imageStorageAccountName = 'img${take(suffix, 21)}'
var deploymentStorageContainerName = 'function-releases'

var storageBlobDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var storageBlobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var storageQueueDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var storageTableDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
var monitoringMetricsPublisherRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')

resource functionIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: hostStorageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

resource hostBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: hostStorage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: hostBlobService
  name: deploymentStorageContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource imageStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: imageStorageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

resource imageBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: imageStorage
  name: 'default'
}

resource imageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: imageBlobService
  name: imageContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// Host storage is separate because it contains Functions runtime/deployment/key data.
// The current Microsoft Flex Consumption identity-based samples grant Blob Data Owner for
// the deployment storage path. Keeping this role isolated to host storage prevents it from
// granting broad rights over application image data.
resource hostStorageBlobOwnerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionIdentity.id, storageBlobDataOwnerRoleId)
  scope: hostStorage
  properties: {
    roleDefinitionId: storageBlobDataOwnerRoleId
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hostStorageBlobContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionIdentity.id, storageBlobDataContributorRoleId)
  scope: hostStorage
  properties: {
    roleDefinitionId: storageBlobDataContributorRoleId
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hostStorageQueueContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionIdentity.id, storageQueueDataContributorRoleId)
  scope: hostStorage
  properties: {
    roleDefinitionId: storageQueueDataContributorRoleId
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hostStorageTableContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, functionIdentity.id, storageTableDataContributorRoleId)
  scope: hostStorage
  properties: {
    roleDefinitionId: storageTableDataContributorRoleId
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Application data storage: only Blob Data Contributor, scoped to the images container.
resource imageContainerBlobContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(imageContainer.id, functionIdentity.id, storageBlobDataContributorRoleId)
  scope: imageContainer
  properties: {
    roleDefinitionId: storageBlobDataContributorRoleId
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource appInsightsMetricsPublisherRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, functionIdentity.id, monitoringMetricsPublisherRoleId)
  scope: appInsights
  properties: {
    roleDefinitionId: monitoringMetricsPublisherRoleId
    principalId: functionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource flexPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'functionapp'
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${functionIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: flexPlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: functionAppRuntime
        }
        {
          name: 'AzureWebJobsStorage__accountName'
          value: hostStorage.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: functionIdentity.properties.clientId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'ImageStorageBlobServiceUri'
          value: imageStorage.properties.primaryEndpoints.blob
        }
        {
          name: 'ImageStorageContainerName'
          value: imageContainerName
        }
        {
          name: 'ImageStorageManagedIdentityClientId'
          value: functionIdentity.properties.clientId
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: functionIdentity.properties.clientId
        }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${hostStorage.properties.primaryEndpoints.blob}${deploymentContainer.name}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: functionIdentity.id
          }
        }
      }
      runtime: {
        name: functionAppRuntime
        version: functionAppRuntimeVersion
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
    }
  }
  dependsOn: [
    hostStorageBlobOwnerRole
    hostStorageBlobContributorRole
    hostStorageQueueContributorRole
    hostStorageTableContributorRole
    imageContainerBlobContributorRole
    appInsightsMetricsPublisherRole
  ]
}

output functionAppName string = functionApp.name
output functionBaseUrl string = 'https://${functionApp.properties.defaultHostName}/api'
output hostStorageAccountName string = hostStorage.name
output imageStorageAccountName string = imageStorage.name
output managedIdentityClientId string = functionIdentity.properties.clientId
```

### 9.2 Why the IaC looks this way

Important security and quality choices:

- **Dedicated user-assigned managed identity**: deterministic client ID and role assignments.
- **Identity-based host storage**: no cloud `AzureWebJobsStorage` connection string.
- **Shared Key disabled** on both cloud storage accounts.
- **Separate host and image storage**: the Functions host/deployment storage permissions do not grant broad rights over user image data.
- **Image storage RBAC is scoped to the image container**, not the whole storage account.
- **Application Insights is workspace-based**, which is the modern monitoring shape.
- **All resources are in one resource group** for clean teardown.

---

## 10. Deployment scripts

Make scripts executable:

```bash
chmod +x scripts/*.sh
```

### 10.1 `scripts/preflight.sh`

This script validates local tools, Bicep syntax, `RESOURCE_PREFIX`, and Flex Consumption region support before any Azure resources are created.

```bash
#!/usr/bin/env bash
set -euo pipefail

: "${SUBSCRIPTION_ID:?Set SUBSCRIPTION_ID}"
: "${RESOURCE_GROUP:=rg-image-fn-dev}"
: "${LOCATION:=canadacentral}"
: "${RESOURCE_PREFIX:=imgfn}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

require_command() {
  local command_name="$1"

  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command not found: $command_name" >&2
    exit 1
  fi
}

if [[ ! "$RESOURCE_PREFIX" =~ ^[a-z0-9]{3,10}$ ]]; then
  echo "RESOURCE_PREFIX must be 3-10 lowercase letters/numbers only." >&2
  exit 1
fi

if [[ ! "$LOCATION" =~ ^[a-z0-9]+$ ]]; then
  echo "LOCATION must use the Azure CLI location name, such as canadacentral or eastus2." >&2
  exit 1
fi

require_command az
require_command docker
require_command dotnet
require_command func
require_command zip

az account set --subscription "$SUBSCRIPTION_ID"

az bicep build \
  --file "$PROJECT_ROOT/infra/main.bicep" \
  --stdout >/dev/null

docker buildx version >/dev/null
dotnet --info >/dev/null
func --version >/dev/null

if ! az functionapp list-flexconsumption-locations \
  --query "[?name=='$LOCATION'].name" \
  -o tsv | grep -Fxq "$LOCATION"; then
  echo "LOCATION '$LOCATION' was not returned by az functionapp list-flexconsumption-locations." >&2
  echo "Run: az functionapp list-flexconsumption-locations --output table" >&2
  exit 1
fi

echo "Preflight passed."
```

Run it directly before your first deployment:

```bash
export SUBSCRIPTION_ID="<subscription-id>"
export RESOURCE_GROUP="rg-image-fn-dev"
export LOCATION="canadacentral"
export RESOURCE_PREFIX="imgfn"

./scripts/preflight.sh
```

### 10.2 `scripts/deploy-infra.sh`

```bash
#!/usr/bin/env bash
set -euo pipefail

: "${SUBSCRIPTION_ID:?Set SUBSCRIPTION_ID}"
: "${RESOURCE_GROUP:=rg-image-fn-dev}"
: "${LOCATION:=canadacentral}"
: "${RESOURCE_PREFIX:=imgfn}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

export SUBSCRIPTION_ID
export RESOURCE_GROUP
export LOCATION
export RESOURCE_PREFIX

"$SCRIPT_DIR/preflight.sh"

cd "$PROJECT_ROOT"

az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags project=azure-function-app-image-transfer environment=dev

az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --template-file infra/main.bicep \
  --parameters location="$LOCATION" resourcePrefix="$RESOURCE_PREFIX"

az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file infra/main.bicep \
  --parameters location="$LOCATION" resourcePrefix="$RESOURCE_PREFIX"

FUNCTION_APP_NAME="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name main \
  --query properties.outputs.functionAppName.value \
  -o tsv)"

FUNCTION_BASE_URL="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name main \
  --query properties.outputs.functionBaseUrl.value \
  -o tsv)"

echo "Function App: $FUNCTION_APP_NAME"
echo "Function base URL: $FUNCTION_BASE_URL"
```

Run it:

```bash
export SUBSCRIPTION_ID="<subscription-id>"
export RESOURCE_GROUP="rg-image-fn-dev"
export LOCATION="canadacentral"
export RESOURCE_PREFIX="imgfn"

./scripts/deploy-infra.sh
```

If your chosen region does not support Flex Consumption, list supported locations:

```bash
az functionapp list-flexconsumption-locations --output table
```

Then rerun with a supported `LOCATION`.

### 10.3 `scripts/publish-aot-zip.sh`

This script uses Docker to build the Linux Native AOT publish directory, zips the publish output, and deploys the ZIP to the existing Function App.

```bash
#!/usr/bin/env bash
set -euo pipefail

: "${RESOURCE_GROUP:=rg-image-fn-dev}"
: "${FUNCTION_APP_NAME:?Set FUNCTION_APP_NAME}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$PROJECT_ROOT"

ARTIFACT_ROOT=".artifacts"
PUBLISH_DIR="$ARTIFACT_ROOT/publish/ImageFunctions"
ZIP_PATH="$ARTIFACT_ROOT/ImageFunctions.zip"

rm -rf "$PUBLISH_DIR" "$ZIP_PATH"
mkdir -p "$PUBLISH_DIR"

# Build linux-x64 Native AOT output in Docker and copy publish output to PUBLISH_DIR.
docker buildx build \
  --file src/ImageFunctions/Dockerfile \
  --target export \
  --output "type=local,dest=$PUBLISH_DIR" \
  .

# Zip the contents of the publish directory, not the parent directory.
# This preserves the Functions-required root layout: host.json, worker.config.json,
# functions.metadata, extensions.json, and .azurefunctions/ at the ZIP root.
(
  cd "$PUBLISH_DIR"
  zip -r "../../ImageFunctions.zip" .
)

az functionapp deployment source config-zip \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --src "$ZIP_PATH"

echo "Published $ZIP_PATH to $FUNCTION_APP_NAME"
```

Run it:

```bash
export RESOURCE_GROUP="rg-image-fn-dev"
export FUNCTION_APP_NAME="<function-app-name-from-deploy-infra>"
./scripts/publish-aot-zip.sh
```

Why this uses Docker instead of `dotnet publish` directly:

- Native AOT targets a specific runtime/OS.
- The Azure-hosted Function App is Linux x64.
- Building inside the .NET SDK Linux container makes the publish output consistent from Windows, macOS, or Linux developer machines.

### 10.4 `scripts/get-function-key.sh`

Cloud endpoints use `AuthorizationLevel.Function`, so E2E cloud tests need a function key.

```bash
#!/usr/bin/env bash
set -euo pipefail

: "${RESOURCE_GROUP:=rg-image-fn-dev}"
: "${FUNCTION_APP_NAME:?Set FUNCTION_APP_NAME}"

az functionapp keys list \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --query "functionKeys.default" \
  -o tsv
```

Usage:

```bash
export RESOURCE_GROUP="rg-image-fn-dev"
export FUNCTION_APP_NAME="<function-app-name>"
export FUNCTION_KEY="$(./scripts/get-function-key.sh)"
```

### 10.5 `scripts/teardown-cloud.sh`

```bash
#!/usr/bin/env bash
set -euo pipefail

: "${RESOURCE_GROUP:=rg-image-fn-dev}"

az group delete \
  --name "$RESOURCE_GROUP" \
  --yes \
  --no-wait

echo "Deletion started for resource group $RESOURCE_GROUP"
```

---

## 11. Run the full flow locally

### 11.1 Build and run through Docker

```bash
docker compose up -d --build
```

### 11.2 Confirm health

```bash
curl -i http://localhost:7071/api/health
```

Expected:

```text
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
...
{"status":"ok","utc":"..."}
```

### 11.3 Run E2E test locally

```bash
FUNCTION_BASE_URL="http://localhost:7071/api" \
  dotnet test tests/ImageFunctions.E2E/ImageFunctions.E2E.csproj
```

### 11.4 Tear down local Docker

```bash
docker compose down -v
```

---

## 12. Run the full flow in Azure

### 12.1 Login

```bash
az login
az account set --subscription "<subscription-id>"
```

Or use your dedicated deployment service principal:

```bash
az login --service-principal \
  --username "<appId>" \
  --password "<password>" \
  --tenant "<tenantId>"
az account set --subscription "<subscription-id>"
```

### 12.2 Deploy infrastructure

```bash
export SUBSCRIPTION_ID="<subscription-id>"
export RESOURCE_GROUP="rg-image-fn-dev"
export LOCATION="canadacentral"
export RESOURCE_PREFIX="imgfn"

./scripts/deploy-infra.sh
```

Capture outputs:

```bash
export FUNCTION_APP_NAME="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name main \
  --query properties.outputs.functionAppName.value \
  -o tsv)"

export FUNCTION_BASE_URL="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name main \
  --query properties.outputs.functionBaseUrl.value \
  -o tsv)"

echo "$FUNCTION_APP_NAME"
echo "$FUNCTION_BASE_URL"
```

### 12.3 Publish the Native AOT package

```bash
./scripts/publish-aot-zip.sh
```

### 12.4 Wait for health

The deployed app may take a little time to restart after ZIP deployment.

```bash
curl -i "$FUNCTION_BASE_URL/health"
```

### 12.5 Run the E2E test against Azure

```bash
export FUNCTION_KEY="$(./scripts/get-function-key.sh)"

FUNCTION_BASE_URL="$FUNCTION_BASE_URL" \
FUNCTION_KEY="$FUNCTION_KEY" \
  dotnet test tests/ImageFunctions.E2E/ImageFunctions.E2E.csproj
```

The same test now validates the cloud path:

1. Generate deterministic PNG bytes.
2. Calculate local SHA-256.
3. Upload to the cloud Function endpoint.
4. Function stores the image in Azure Blob Storage through managed identity.
5. Download from the cloud Function endpoint.
6. Calculate SHA-256 again.
7. Assert the hashes match.

---

## 13. Manual smoke test with curl

Create a tiny image file:

```bash
printf '%s' 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=' | base64 --decode > tiny.png
sha256sum tiny.png
```

Local upload/download:

```bash
curl -i -X POST \
  -H "Content-Type: image/png" \
  --data-binary @tiny.png \
  "http://localhost:7071/api/images/tiny.png"

curl -i \
  "http://localhost:7071/api/images/tiny.png" \
  --output downloaded.png

sha256sum downloaded.png
```

Cloud upload/download:

```bash
curl -i -X POST \
  -H "Content-Type: image/png" \
  --data-binary @tiny.png \
  "$FUNCTION_BASE_URL/images/tiny.png?code=$FUNCTION_KEY"

curl -i \
  "$FUNCTION_BASE_URL/images/tiny.png?code=$FUNCTION_KEY" \
  --output downloaded-cloud.png

sha256sum downloaded-cloud.png
```

---

## 14. Troubleshooting

### 14.1 `az deployment group create` fails with role assignment errors

Symptoms:

- `AuthorizationFailed`
- `Microsoft.Authorization/roleAssignments/write` denied

Cause:

- Contributor alone cannot create Azure RBAC role assignments.

Fix:

- Have an administrator grant your deployment principal `User Access Administrator` or `Role Based Access Control Administrator` at the resource-group scope, or use a custom role that only permits the required role-assignment operations.

### 14.2 Function App starts but Blob access fails in Azure

Symptoms:

- 403 from Azure Storage
- `AuthorizationPermissionMismatch`
- Upload/download errors after deployment

Causes and fixes:

- RBAC propagation can take time. Wait and retry.
- Confirm the Function App has the user-assigned identity attached.
- Confirm `ImageStorageManagedIdentityClientId` matches the user-assigned identity client ID.
- Confirm the image container role assignment exists and is scoped to the `images` container.

Commands:

```bash
az functionapp identity show -g "$RESOURCE_GROUP" -n "$FUNCTION_APP_NAME"
az role assignment list -g "$RESOURCE_GROUP" --all -o table
```

### 14.3 Cloud function key does not work

Use the host default key:

```bash
az functionapp keys list \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --query "functionKeys.default" \
  -o tsv
```

Then call:

```bash
curl -i "$FUNCTION_BASE_URL/health"
curl -i "$FUNCTION_BASE_URL/images/example.png?code=$FUNCTION_KEY"
```

The health endpoint is anonymous, but upload/download require the key.

### 14.4 Docker build fails during Native AOT publish

Common causes:

- Docker cannot access the internet to restore NuGet packages.
- The .NET 10 SDK image is unavailable locally and cannot be pulled.
- A dependency introduced reflection or dynamic code that is incompatible with AOT.

First diagnostics:

```bash
docker buildx build --progress=plain -f src/ImageFunctions/Dockerfile --target export --output type=local,dest=.artifacts/publish .
```

If an IL warning identifies a package or API, remove it, configure source generation, or add a narrowly-scoped AOT descriptor only after understanding the warning.

### 14.5 ZIP deployment succeeds but Functions are missing

Make sure you zipped the **contents** of the publish folder, not the folder itself. At the root of the ZIP, you should see files like:

```text
host.json
worker.config.json
functions.metadata
extensions.json
.azurefunctions/
ImageFunctions
```

Inspect:

```bash
unzip -l .artifacts/ImageFunctions.zip | head -50
```

If you see `ImageFunctions/host.json` instead of `host.json`, you zipped the parent directory by mistake.

### 14.6 Azurite errors locally

Restart local services and remove persisted emulator data:

```bash
docker compose down -v
docker compose up -d --build
```

The Docker Compose file uses a custom Azurite account named `localstore`. If you run Azurite outside Docker, keep the same `AZURITE_ACCOUNTS` value and use the `127.0.0.1` endpoints in `local.settings.json.example`.

---

## 15. Optional future Aspire developer harness

A future version of this starter can add an Aspire AppHost to orchestrate local dependencies, expose dashboard logs/traces, and reduce manual local setup.

The AppHost must remain a composition harness only. It must not contain image-transfer business logic. Production deployment in this tutorial remains Bicep + Azure Functions Flex Consumption.

Do not switch this tutorial to Aspire-driven deployment unless you intentionally accept a container-oriented Azure Functions or Azure Container Apps deployment model instead of Flex Consumption package deployment.

---

## 16. Production hardening checklist

This starter is intentionally small. Before using the pattern for production, consider:

- Put Azure resources behind private networking if the application requires it.
- Add rate limiting or front the Functions app with API Management.
- Use App Service Authentication / Easy Auth, OAuth/JWT validation, or API Management instead of Function keys for user-facing APIs.
- Add malware scanning or image decoding/validation if untrusted users upload files.
- Increase observability: dashboards, alerts, failure-rate alerts, and storage metrics.
- Add CI/CD with workload identity federation instead of long-lived service-principal secrets.
- Split dev/test/prod into separate subscriptions or at least separate resource groups.
- Add Azure Policy to deny storage Shared Key access and public blob access globally.
- Add lifecycle policies for old uploaded blobs.
- Add tests for invalid files, oversized bodies, missing names, and 404 downloads.
- Consider blue/green or deployment slots where supported by your hosting plan and operational needs.

---

## 17. Complete command summary

Local Docker test:

```bash
docker compose up -d --build
FUNCTION_BASE_URL="http://localhost:7071/api" dotnet test tests/ImageFunctions.E2E/ImageFunctions.E2E.csproj
docker compose down -v
```

Cloud deploy and test:

```bash
az login
export SUBSCRIPTION_ID="<subscription-id>"
export RESOURCE_GROUP="rg-image-fn-dev"
export LOCATION="canadacentral"
export RESOURCE_PREFIX="imgfn"

./scripts/deploy-infra.sh

export FUNCTION_APP_NAME="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name main \
  --query properties.outputs.functionAppName.value \
  -o tsv)"

export FUNCTION_BASE_URL="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name main \
  --query properties.outputs.functionBaseUrl.value \
  -o tsv)"

./scripts/publish-aot-zip.sh

export FUNCTION_KEY="$(./scripts/get-function-key.sh)"

FUNCTION_BASE_URL="$FUNCTION_BASE_URL" \
FUNCTION_KEY="$FUNCTION_KEY" \
  dotnet test tests/ImageFunctions.E2E/ImageFunctions.E2E.csproj
```

Teardown:

```bash
./scripts/teardown-cloud.sh
docker compose down -v
rm -rf .artifacts
```

---

## 18. Open questions

- Is Native AOT mandatory, or should the project use it only while trim/AOT warnings remain manageable?
- Should production uploads pass through the Function body, or should the API issue short-lived direct-to-blob upload URLs?
- What authentication model should replace Function keys for user-facing APIs: Entra ID, Easy Auth, API Management, or application JWT validation?
- What scale target defines "massive": request rate, image size, storage volume, latency, region count, or concurrency?
- Should private networking be required from the first production version?
- Should dev/test/prod live in separate subscriptions or separate resource groups?
- Should CI/CD with workload identity federation be part of the starter or a follow-up guide?
- Should the future portability target be AWS Lambda/S3, Azure Container Apps, Kubernetes, or only clean application-layer ports?
- What observability baseline is required: Application Insights only, dashboards and alerts, distributed tracing, or cost/quota alerts?
- What validation is required for uploaded files: MIME sniffing, image decoding, malware scanning, size limits, or content moderation?

---

## 19. References

- Azure Functions isolated worker guide: https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide
- Azure Functions runtime versions overview: https://learn.microsoft.com/en-us/azure/azure-functions/functions-versions
- Azure Functions Flex Consumption plan: https://learn.microsoft.com/en-us/azure/azure-functions/flex-consumption-plan
- Create a Flex Consumption app with Bicep: https://learn.microsoft.com/en-us/azure/azure-functions/functions-create-first-function-bicep
- Create/manage Flex Consumption apps: https://learn.microsoft.com/en-us/azure/azure-functions/flex-consumption-how-to
- Azure Functions app settings reference, including identity-based `AzureWebJobsStorage`: https://learn.microsoft.com/en-us/azure/azure-functions/functions-app-settings
- Develop Azure Functions locally: https://learn.microsoft.com/en-us/azure/azure-functions/functions-develop-local
- Azure Functions Core Tools reference: https://learn.microsoft.com/en-us/azure/azure-functions/functions-core-tools-reference
- Zip deployment for Azure Functions: https://learn.microsoft.com/en-us/azure/azure-functions/deployment-zip-push
- Deployment technologies in Azure Functions: https://learn.microsoft.com/en-us/azure/azure-functions/functions-deployment-technologies
- Deploy Bicep with Azure CLI: https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-cli
- Azure Resource Group management with Azure CLI: https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/manage-resource-groups-cli
- Authorize access to blobs with Microsoft Entra ID: https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-access-azure-active-directory
- Assign Azure roles for Blob data access: https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access
- Azurite local storage emulator: https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite
- Native AOT deployment overview: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- Azure Functions with Aspire: https://learn.microsoft.com/en-au/azure/azure-functions/dotnet-aspire-integration
- Aspire local Azure provisioning: https://aspire.dev/integrations/cloud/azure/local-provisioning/
- Azure Functions Linux container support: https://learn.microsoft.com/en-us/azure/azure-functions/container-concepts
- AWS .NET Aspire integration: https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/aspire-integrations.html
- Azure SDK for .NET package index: https://learn.microsoft.com/en-us/dotnet/azure/sdk/packages
- Azure Functions .NET isolated Docker image tags: https://hub.docker.com/r/microsoft/azure-functions-dotnet-isolated
