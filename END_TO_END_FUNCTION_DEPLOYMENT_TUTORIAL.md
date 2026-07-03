# Superseded: Azure Functions Deployment Tutorial

This tutorial has been superseded.

The project originally targeted Azure Functions with the .NET isolated worker
and Native AOT. During local validation, that path proved to be a poor fit for
Native AOT because the Functions worker/runtime layer caused issues that were
outside the image-transfer application code.

The project is now moving toward:

- ASP.NET Core Minimal API;
- Azure Container Apps;
- Azure Blob Storage;
- Microsoft Entra ID bearer-token authentication;
- Bicep;
- Docker;
- optional Native AOT with a non-AOT fallback.

Use these documents instead:

- [End-to-end Container Apps tutorial](END_TO_END_CONTAINER_APP_TUTORIAL.md)
  for the fresh start-from-scratch guide.
- [Transition plan](docs/transition-plan-functions-to-container-apps.md)
  for migrating this repository from the old prototype.
- [Architecture decision](docs/architecture-decision-container-apps.md)
  for the reasoning behind the platform choice.

This file remains only to prevent stale links from silently sending readers down
the old Azure Functions path.
