# Transition Plan: Functions Prototype To Container Apps API

Date: 2026-07-03

Status: planned

This document is for the current repository state. It explains how to move from
the existing Azure Functions prototype to the Container Apps + Minimal API setup
described in the canonical tutorial.

The canonical tutorial is:

- [End-to-end Azure Container Apps tutorial](../END_TO_END_CONTAINER_APP_TUTORIAL.md)

Use this transition plan only when changing this existing repo. Do not mix
transition notes into the canonical tutorial.

## 1. Current State

The repository currently contains a local Azure Functions prototype:

```text
src/ImageFunctions/
  Dockerfile
  ImageFunctions.csproj
  Program.cs
  host.json
  local.settings.json.example
  Functions/
    ImageHttpFunctions.cs
  Http/
    HttpJson.cs
  Models/
    ApiModels.cs
  Storage/
    BlobServiceClientFactory.cs
    ImageBlobStore.cs

tests/ImageFunctions.E2E/
  ImageFunctions.E2E.csproj
  ImageRoundTripTests.cs
```

The local Functions container path works without Native AOT. The true Native
AOT Functions path is the reason for the pivot: the app code can be made
AOT-friendly, but the Functions worker/runtime path is not a good fit.

## 2. Target State

The target application should match the fresh-start tutorial:

```text
src/ImageApi/
  Dockerfile
  ImageApi.csproj
  Program.cs
  Auth/
    CurrentUser.cs
  Models/
    ApiModels.cs
  Storage/
    BlobServiceClientFactory.cs
    ImageBlobStore.cs

tests/ImageApi.E2E/
  ImageApi.E2E.csproj
  ImageRoundTripTests.cs
```

The target runtime is:

- ASP.NET Core Minimal API;
- Azure Container Apps;
- Blob Storage;
- Entra bearer-token auth in Azure;
- development-only `X-Dev-User` auth locally;
- Bicep-managed Azure resources;
- Docker Compose with API + Azurite locally;
- optional Native AOT container target.

## 3. Keep, Rewrite, Delete

Keep the ideas from:

- `src/ImageFunctions/Storage/BlobServiceClientFactory.cs`
- `src/ImageFunctions/Storage/ImageBlobStore.cs`
- `src/ImageFunctions/Models/ApiModels.cs`
- `tests/ImageFunctions.E2E/ImageRoundTripTests.cs`

Rewrite:

- the HTTP adapter;
- the project file;
- Dockerfile runtime stages;
- Docker Compose service names and environment variables;
- infrastructure;
- deployment scripts;
- tests that assume Function keys or `/api/images/{name}`.

Delete after replacement:

- `src/ImageFunctions/Functions/ImageHttpFunctions.cs`
- `src/ImageFunctions/Http/HttpJson.cs`
- `src/ImageFunctions/host.json`
- `src/ImageFunctions/local.settings.json.example`
- `local-secrets/`
- Functions-specific package references;
- Functions-specific scripts such as `get-function-key.sh` and
  `publish-aot-zip.sh`.

Do not delete the old project until the new local E2E path is green.

## 4. Stage 1: Add `ImageApi`

Create the new project beside the existing one:

```text
src/ImageApi/
tests/ImageApi.E2E/
```

Do not rename the old project in place. Keeping both projects temporarily makes
the migration easier to inspect and easier to abandon if a surprise appears.

Verification gate:

```bash
dotnet restore src/ImageApi/ImageApi.csproj
```

## 5. Stage 2: Port Shared Models And Storage

Port these files into the new namespace:

```text
src/ImageApi/Models/ApiModels.cs
src/ImageApi/Storage/BlobServiceClientFactory.cs
src/ImageApi/Storage/ImageBlobStore.cs
```

Change behavior while porting:

- generate server-side image IDs;
- use owner-based blob prefixes;
- add list support;
- store metadata for owner hash, SHA-256, and created time;
- keep source-generated JSON.

Do not carry over Function request/response types.

Verification gate:

```bash
dotnet build src/ImageApi/ImageApi.csproj
```

## 6. Stage 3: Replace Function Routes With Minimal API Routes

Implement these routes:

```http
GET  /api/health
GET  /api/images
POST /api/images
GET  /api/images/{imageId}
```

Differences from the Functions prototype:

- upload no longer uses a caller-provided blob name;
- upload returns a generated `imageId`;
- list is now part of the basic product shape;
- ownership is enforced by auth identity;
- Function keys disappear.

Verification gate:

```bash
dotnet run --project src/ImageApi/ImageApi.csproj
curl -i http://localhost:8080/api/health
```

## 7. Stage 4: Add Auth Shape

Add:

```text
src/ImageApi/Auth/CurrentUser.cs
```

Local behavior:

- `ASPNETCORE_ENVIRONMENT=Development`
- `Authentication__UseDevelopmentUser=true`
- image endpoints require `X-Dev-User`

Azure behavior:

- validate Entra bearer tokens;
- require `Authentication__TenantId`;
- require `Authentication__Audience`;
- derive ownership from tenant ID and object ID claims.

Verification gate:

```bash
curl -i http://localhost:8080/api/images
curl -i -H "X-Dev-User: alice" http://localhost:8080/api/images
```

Expected:

- first command returns 401 locally;
- second command returns 200 locally.

## 8. Stage 5: Replace Docker Compose

Change the Compose app service from `functions` to `api`.

Remove:

- `FUNCTIONS_WORKER_RUNTIME`;
- `AzureWebJobsStorage`;
- Function secret file mounts;
- Function host port mapping.

Keep:

- Azurite;
- the local custom storage account, with its key generated into ignored `.env`;
- `--skipApiVersionCheck`;
- persisted Azurite volume.

Verification gate:

```powershell
.\scripts\init-local-env.ps1
docker compose up -d --build
curl.exe -i http://localhost:8080/api/health
docker compose logs api
```

## 9. Stage 6: Create New E2E Test Project

Create:

```text
tests/ImageApi.E2E/
```

Update the test behavior:

- default base URL becomes `http://localhost:8080/api`;
- local requests send `X-Dev-User`;
- cloud requests send `Authorization: Bearer <token>`;
- upload endpoint changes from `POST /images/{name}` to `POST /images`;
- test reads `imageId` from the upload response;
- test downloads from `GET /images/{imageId}`.

Verification gate:

```bash
dotnet test tests/ImageApi.E2E/ImageApi.E2E.csproj
```

## 10. Stage 7: Prove Normal Container Then AOT Container

First make the normal container green:

```bash
docker compose up -d --build
dotnet test tests/ImageApi.E2E/ImageApi.E2E.csproj
```

Then test the AOT target:

```bash
docker build \
  -f src/ImageApi/Dockerfile \
  --target aot-runtime \
  -t image-api:aot \
  .
```

Run the AOT container against Azurite and run the same E2E test.

Decision gate:

- if AOT is green, keep it as the default Docker target for cloud images;
- if AOT blocks useful app behavior, use the normal `runtime` target.

Do not spend disproportionate time saving AOT.

## 11. Stage 8: Replace Infrastructure

Replace the old Functions-oriented Bicep with the Container Apps layout from the
canonical tutorial:

```text
infra/
  registry.bicep
  main.bicep
```

Remove Functions concepts:

- Function App;
- Flex Consumption plan;
- host storage;
- Function app settings;
- ZIP/package deployment.

Add Container Apps concepts:

- Azure Container Registry;
- Container Apps environment;
- Container App;
- app managed identity;
- ACR pull role assignment;
- Blob data role assignment;
- external HTTPS ingress;
- scale-to-zero settings.

Verification gate:

```bash
az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --template-file infra/main.bicep \
  --parameters \
    location="$LOCATION" \
    resourcePrefix="$RESOURCE_PREFIX" \
    acrName="$ACR_NAME" \
    containerImage="$CONTAINER_IMAGE" \
    authenticationTenantId="$AUTHENTICATION_TENANT_ID" \
    authenticationAudience="$AUTHENTICATION_AUDIENCE"
```

## 12. Stage 9: Replace Scripts

Replace Functions scripts:

```text
scripts/get-function-key.sh
scripts/publish-aot-zip.sh
```

With Container Apps scripts:

```text
scripts/deploy-registry.sh
scripts/build-image.sh
scripts/deploy-infra.sh
scripts/smoke-test.sh
scripts/teardown-cloud.sh
```

Keep scripts thin. They should reveal the Azure CLI and Docker commands instead
of hiding them behind a private deployment framework.

Verification gate:

```bash
./scripts/preflight.sh
./scripts/deploy-registry.sh
./scripts/build-image.sh
./scripts/deploy-infra.sh
./scripts/smoke-test.sh
```

## 13. Stage 10: Retire The Functions Project

Only after the new local E2E test is green:

- remove `src/ImageFunctions/`;
- remove `tests/ImageFunctions.E2E/`;
- remove `local-secrets/`;
- remove Functions-specific script files;
- update any stale README links;
- search for old names.

Search:

```bash
rg -n "ImageFunctions|Azure Functions|Function App|FUNCTIONS_WORKER_RUNTIME|AzureWebJobsStorage|function key|host.json|local.settings"
```

Some remaining mentions are acceptable in historical docs:

- `END_TO_END_FUNCTION_DEPLOYMENT_TUTORIAL.md`;
- `docs/architecture-decision-container-apps.md`;
- this transition plan.

They should not remain in the canonical tutorial or active app docs.

## 14. Final Verification Checklist

Local:

- `docker compose up -d --build` starts `api` and `azurite`;
- `GET /api/health` returns 200;
- unauthenticated local image request returns 401;
- local image request with `X-Dev-User` works;
- E2E test passes locally;
- normal container target passes;
- AOT container target either passes or is deliberately disabled.

Azure:

- ACR exists with admin user disabled;
- image is pushed with an immutable tag;
- Container App revision is healthy;
- Container App has `minReplicas = 0`;
- app identity can pull from ACR;
- app identity can access the image blob container;
- health endpoint works without auth;
- image endpoints require Entra bearer tokens;
- cloud E2E passes with `API_ACCESS_TOKEN`.

Docs:

- README points to the canonical tutorial;
- transition details live only in this document;
- stale Functions tutorial is a superseded notice;
- canonical tutorial reads as a fresh-start guide.

## 15. Rollback And Fallback

After the new API passes local E2E tests, remove the old Functions project from
the active tree. Keep the old implementation in Git history rather than carrying
two live app models.

Fallback options:

- keep Container Apps but deploy the normal non-AOT image;
- keep Blob-only storage until SQL is actually needed;
- delay Entra-auth E2E automation if token acquisition becomes a separate
  desktop-client concern;
- preserve the old Functions code in Git history rather than keeping duplicate
  active projects.

The goal is not to preserve every original idea. The goal is to end with a
smaller, clearer API that matches the platform choice.
