# Image Transfer API Tutorial

This repository is a learning project for building and deploying a small image
transfer API on Azure.

The project is currently pivoting from an Azure Functions prototype to this
target architecture:

- .NET 10 and C# 14
- ASP.NET Core Minimal API
- Azure Container Apps
- Azure Blob Storage
- Microsoft Entra ID bearer-token authentication
- Bicep infrastructure as code
- Docker-based local development and publishing
- Optional Native AOT, with a non-AOT fallback
- End-to-end tests that upload an image, download it again, and compare hashes

## Why The Pivot?

The original tutorial targeted Azure Functions with .NET isolated worker and
Native AOT. Local testing showed that the Functions worker/runtime path is not a
good fit for Native AOT today.

The new direction keeps the serverless Azure shape, but moves the HTTP API to
Azure Container Apps. That lets the project use an AOT-friendly ASP.NET Core
Minimal API without fighting the Azure Functions host.

## Documentation

- [Container Apps tutorial](END_TO_END_CONTAINER_APP_TUTORIAL.md) is the new
  source of truth for the transition.
- [Architecture decision](docs/architecture-decision-container-apps.md)
  explains why the project is moving to Container Apps, keeping Bicep, and
  treating Native AOT as optional.
- [Superseded Functions tutorial](END_TO_END_FUNCTION_DEPLOYMENT_TUTORIAL.md)
  remains only as a pointer to the old direction.

## PoC Scope

The first Container Apps version should stay intentionally small:

- users sign in with Microsoft Entra ID;
- each user can upload, list, and download only their own images;
- image bytes and simple metadata live in Blob Storage;
- the Container App scales to zero to keep PoC costs low;
- SQL, sharing rules, API Management, private networking, and CI/CD come later.

## Guiding Principle

Native AOT is useful, but it is not allowed to distort the application. We will
write the API in an AOT-friendly style, verify the published container, and keep
a normal non-AOT publish path available if the ecosystem gets in the way.
