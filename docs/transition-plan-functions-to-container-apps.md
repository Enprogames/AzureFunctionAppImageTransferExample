# Completed Transition: Azure Functions To Container Apps

Date: 2026-07-03

Status: completed

This repository began as an Azure Functions isolated-worker prototype. It now
uses ASP.NET Core Minimal API, Azure Container Apps, Blob Storage, Microsoft
Entra ID authentication, Bicep, and Docker.

The Functions project and Functions-specific deployment files have been removed.
Their history remains available in Git.

Use the [Container Apps tutorial](../END_TO_END_CONTAINER_APP_TUTORIAL.md) for
the current implementation and the
[architecture decision](architecture-decision-container-apps.md) for the reason
behind the change.
