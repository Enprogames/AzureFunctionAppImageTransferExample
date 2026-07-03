# Image Transfer API Tutorial

This repository is a learning project for building and deploying a small
authenticated image-transfer API on Azure.

The canonical tutorial starts from a clean repository and builds this target
architecture:

- .NET 10 and C# 14;
- ASP.NET Core Minimal API;
- Azure Container Apps;
- Azure Blob Storage;
- Microsoft Entra ID bearer-token authentication;
- Bicep infrastructure as code;
- Docker-based local development and publishing;
- optional Native AOT with a non-AOT fallback;
- end-to-end tests that upload an image, download it again, and compare hashes.

## Start Here

- [End-to-end Container Apps tutorial](END_TO_END_CONTAINER_APP_TUTORIAL.md)
  is the canonical start-from-scratch guide.
- [Architecture decision](docs/architecture-decision-container-apps.md)
  explains the main platform choices.
- [Transition plan](docs/transition-plan-functions-to-container-apps.md)
  is only for migrating this repository from the earlier Azure Functions
  prototype.
- [Superseded Functions tutorial](END_TO_END_FUNCTION_DEPLOYMENT_TUTORIAL.md)
  remains only as a signpost away from the old direction.

## PoC Scope

The first version should stay intentionally small:

- users sign in with Microsoft Entra ID;
- each user can upload, list, and download only their own images;
- image bytes and simple metadata live in Blob Storage;
- the Container App scales to zero to keep idle cost low;
- SQL, sharing rules, API Management, private networking, and CI/CD come later.

## Native AOT Stance

Native AOT is useful, but it is not allowed to distort the application.

The tutorial writes the API in an AOT-friendly style, verifies the published
container, and keeps a normal non-AOT publish path available if the ecosystem
gets in the way.
