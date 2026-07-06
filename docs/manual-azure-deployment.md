# Manual Azure Deployment With `az`

This guide deploys the current image-transfer API to Azure from the command
line, without using any project deployment scripts.

It uses:

- Azure CLI for resource deployment;
- Bicep files already in this repository;
- Azure Container Registry remote builds through `az acr build`;
- Azure Container Apps for hosting;
- Azure Blob Storage for uploaded images;
- managed identity for registry pull and blob access.

Run the commands from the repository root.

## What This Deploys

The deployment creates:

- an Azure Container Registry;
- a Container Apps managed environment;
- a public Container App;
- a private Storage account and blob container;
- a user-assigned managed identity;
- an ACR Pull role assignment for the app identity;
- a Storage Blob Data Contributor role assignment for the app identity;
- production authentication settings for JWT bearer validation.

The API is deployed with `minReplicas=0` so it can scale to zero when idle.

## Prerequisites

Install or confirm:

- Azure CLI;
- an Azure subscription;
- permission to create resource groups, role assignments, app registrations,
  storage accounts, ACR, and Container Apps;
- Bicep support through Azure CLI;
- a local checkout of this repository.

Docker Desktop is not required for the image build in this flow. `az acr build`
uploads the Docker build context to Azure Container Registry and builds the
image there.

The repository `.dockerignore` must exclude local secret files before using
`az acr build`. In this project, `.env` and `.env.*` are ignored while
`.env.example` remains available.

## 1. Sign In And Set Variables

```powershell
az login
az account set --subscription "<subscription-id>"

$LOCATION = "canadacentral"
$RESOURCE_GROUP = "rg-image-api-dev"
$RESOURCE_PREFIX = "imgapi"
$TAG = Get-Date -Format "yyyyMMddHHmmss"
```

`RESOURCE_PREFIX` must be 3-10 lowercase letters or numbers. It becomes part of
the Azure resource names.

## 2. Register Resource Providers

```powershell
az provider register --namespace Microsoft.App --wait
az provider register --namespace Microsoft.ContainerRegistry --wait
az provider register --namespace Microsoft.ManagedIdentity --wait
az provider register --namespace Microsoft.OperationalInsights --wait
az provider register --namespace Microsoft.Storage --wait
```

These registrations are subscription-level. If they are already registered, the
commands are harmless.

## 3. Create The Resource Group

```powershell
az group create `
  --name $RESOURCE_GROUP `
  --location $LOCATION `
  --tags project=image-transfer-api environment=dev
```

## 4. Deploy Azure Container Registry

The registry is deployed first because the application deployment needs an
existing container image.

```powershell
az deployment group create `
  --resource-group $RESOURCE_GROUP `
  --name registry `
  --template-file infra/registry.bicep `
  --parameters location=$LOCATION resourcePrefix=$RESOURCE_PREFIX
```

Read the registry outputs:

```powershell
$ACR_NAME = az deployment group show `
  --resource-group $RESOURCE_GROUP `
  --name registry `
  --query properties.outputs.acrName.value `
  -o tsv

$ACR_LOGIN_SERVER = az deployment group show `
  --resource-group $RESOURCE_GROUP `
  --name registry `
  --query properties.outputs.acrLoginServer.value `
  -o tsv
```

## 5. Build The Container Image In ACR

Build the Native AOT image directly in Azure Container Registry:

```powershell
az acr build `
  --registry $ACR_NAME `
  --file src/ImageApi/Dockerfile `
  --target aot-runtime `
  --image "image-api:$TAG" `
  .

$CONTAINER_IMAGE = "${ACR_LOGIN_SERVER}/image-api:${TAG}"
```

This replaces local `docker build`, `docker tag`, `docker login`, and
`docker push`.

If the AOT build fails and you need to deploy a normal framework-dependent image
for diagnosis, use `--target runtime` instead of `--target aot-runtime`.

## 6. Configure Entra API Values

The production API requires a tenant ID and expected JWT audience. If you
already have an API app registration, use its values and skip the creation
commands below.

```powershell
$AUTHENTICATION_TENANT_ID = az account show --query tenantId -o tsv
```

For a new proof-of-concept API app registration:

```powershell
$API_APP_ID = az ad app create `
  --display-name "$RESOURCE_PREFIX-image-api-dev" `
  --sign-in-audience AzureADMyOrg `
  --query appId `
  -o tsv

az ad app update `
  --id $API_APP_ID `
  --identifier-uris "api://$API_APP_ID" `
  --set api.requestedAccessTokenVersion=2

$AUTHENTICATION_AUDIENCE = "api://$API_APP_ID"
```

This is enough to configure the API deployment. A real desktop client sign-in
flow should later use a separate client app registration and explicit delegated
API scopes.

## 7. Preview The Main Deployment

```powershell
az deployment group what-if `
  --resource-group $RESOURCE_GROUP `
  --name main `
  --template-file infra/main.bicep `
  --parameters `
    location=$LOCATION `
    resourcePrefix=$RESOURCE_PREFIX `
    acrName=$ACR_NAME `
    containerImage=$CONTAINER_IMAGE `
    authenticationTenantId=$AUTHENTICATION_TENANT_ID `
    authenticationAudience=$AUTHENTICATION_AUDIENCE `
    minReplicas=0 `
    maxReplicas=3
```

Review the output before continuing. The deployment should create the app
environment, app identity, storage account, role assignments, and Container App.

## 8. Deploy The Application

```powershell
az deployment group create `
  --resource-group $RESOURCE_GROUP `
  --name main `
  --template-file infra/main.bicep `
  --parameters `
    location=$LOCATION `
    resourcePrefix=$RESOURCE_PREFIX `
    acrName=$ACR_NAME `
    containerImage=$CONTAINER_IMAGE `
    authenticationTenantId=$AUTHENTICATION_TENANT_ID `
    authenticationAudience=$AUTHENTICATION_AUDIENCE `
    minReplicas=0 `
    maxReplicas=3
```

Read the outputs:

```powershell
$CONTAINER_APP_NAME = az deployment group show `
  --resource-group $RESOURCE_GROUP `
  --name main `
  --query properties.outputs.containerAppName.value `
  -o tsv

$API_BASE_URL = az deployment group show `
  --resource-group $RESOURCE_GROUP `
  --name main `
  --query properties.outputs.apiBaseUrl.value `
  -o tsv

$IMAGE_STORAGE_ACCOUNT_NAME = az deployment group show `
  --resource-group $RESOURCE_GROUP `
  --name main `
  --query properties.outputs.imageStorageAccountName.value `
  -o tsv
```

## 9. Smoke Test

The health endpoint is anonymous:

```powershell
curl.exe "$API_BASE_URL/health"
```

Expected result:

```json
{"status":"ok","timestampUtc":"..."}
```

The image endpoints are protected in Azure:

```powershell
curl.exe -i "$API_BASE_URL/images"
```

Expected result without a bearer token:

```text
HTTP/1.1 401 Unauthorized
```

## 10. Useful Operational Commands

Show the Container App:

```powershell
az containerapp show `
  --name $CONTAINER_APP_NAME `
  --resource-group $RESOURCE_GROUP `
  -o table
```

Get the app FQDN:

```powershell
az containerapp show `
  --name $CONTAINER_APP_NAME `
  --resource-group $RESOURCE_GROUP `
  --query properties.configuration.ingress.fqdn `
  -o tsv
```

Stream logs:

```powershell
az containerapp logs show `
  --name $CONTAINER_APP_NAME `
  --resource-group $RESOURCE_GROUP `
  --follow
```

List revisions:

```powershell
az containerapp revision list `
  --name $CONTAINER_APP_NAME `
  --resource-group $RESOURCE_GROUP `
  -o table
```

Build and deploy a new image tag:

```powershell
$TAG = Get-Date -Format "yyyyMMddHHmmss"

az acr build `
  --registry $ACR_NAME `
  --file src/ImageApi/Dockerfile `
  --target aot-runtime `
  --image "image-api:$TAG" `
  .

$CONTAINER_IMAGE = "${ACR_LOGIN_SERVER}/image-api:${TAG}"

az containerapp update `
  --name $CONTAINER_APP_NAME `
  --resource-group $RESOURCE_GROUP `
  --image $CONTAINER_IMAGE
```

Check the deployed storage account:

```powershell
az storage account show `
  --name $IMAGE_STORAGE_ACCOUNT_NAME `
  --resource-group $RESOURCE_GROUP `
  --query "{name:name,allowBlobPublicAccess:allowBlobPublicAccess,allowSharedKeyAccess:allowSharedKeyAccess}" `
  -o json
```

## 11. Tear Down

This removes the full proof-of-concept environment:

```powershell
az group delete `
  --name $RESOURCE_GROUP `
  --yes `
  --no-wait
```

Use this only when you no longer need the uploaded images or deployed resources.

## Troubleshooting

### `az acr build` uploads more than expected

`az acr build` sends the Docker build context to Azure. Keep `.dockerignore`
strict. Local secrets such as `.env`, `.env.*`, local settings, build outputs,
and test results should stay excluded.

### Role assignment deployment fails

The Bicep deployment creates role assignments for ACR pull and blob access. Your
Azure user needs permission to create role assignments, usually `Owner` or
`User Access Administrator`.

### The app deploys but cannot pull the image

Confirm that the `containerImage` value includes the registry login server,
repository, and tag:

```powershell
$CONTAINER_IMAGE
```

It should look like:

```text
<registry>.azurecr.io/image-api:<tag>
```

Also confirm that the ACR Pull role assignment exists on the registry for the
app's managed identity.

### Health does not respond

Check logs:

```powershell
az containerapp logs show `
  --name $CONTAINER_APP_NAME `
  --resource-group $RESOURCE_GROUP
```

Also remember that `minReplicas=0` allows scale-to-zero. The first request after
idle time can be slower.

### Image endpoints return `401`

That is expected without a valid Entra bearer token. `/api/health` is anonymous,
but `/api/images` requires authentication in Azure.

### Image endpoints return storage errors

Confirm the app identity has `Storage Blob Data Contributor` on the image blob
container and that the app has these environment variables:

- `ImageStorageBlobServiceUri`;
- `ImageStorageContainerName`;
- `ImageStorageManagedIdentityClientId`.

### AOT build fails

Native AOT is the preferred target for this proof of concept, but the app can be
deployed with the normal runtime image while investigating:

```powershell
az acr build `
  --registry $ACR_NAME `
  --file src/ImageApi/Dockerfile `
  --target runtime `
  --image "image-api:$TAG" `
  .
```
