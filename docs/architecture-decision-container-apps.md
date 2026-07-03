# Architecture Decision: Move To Azure Container Apps

Date: 2026-07-03

Status: accepted as the target direction

## Context

The project started as an Azure Functions tutorial for uploading and downloading
images with .NET 10, C# 14, Bicep, Docker, managed identity, and Native AOT.

Local testing showed that the Azure Functions isolated worker path is not a
good fit for Native AOT in this project. The failure was not in the image
business logic. It came from the Functions worker/runtime layer.

The application itself is simple HTTP API work:

- health check;
- upload image;
- list images;
- download image;
- verify image bytes with a hash.

That shape fits ASP.NET Core Minimal API well.

## Decision

Move the target architecture to:

- ASP.NET Core Minimal API;
- Azure Container Apps;
- Azure Blob Storage;
- Microsoft Entra ID bearer-token authentication;
- Bicep;
- Docker;
- optional Native AOT.

The project should no longer present itself as an Azure Functions tutorial.

## Why Container Apps?

Container Apps keeps the parts of serverless Azure that matter for this PoC:

- scale-to-zero;
- managed identity;
- HTTPS ingress;
- logs;
- revisions;
- containerized local and cloud parity.

It also lets the API run as a normal ASP.NET Core application instead of going
through the Azure Functions host and worker.

## Why Not Azure Functions On Container Apps?

Azure Functions on Container Apps is a valid platform choice when the goal is
the Functions programming model: triggers, bindings, and function-style event
handlers.

That is not the main need here. This project is an HTTP API with an optional
Native AOT goal. Running Functions inside Container Apps would keep the worker
layer that caused the AOT problem.

## Why Keep Bicep?

Bicep is first-party Azure infrastructure as code. It maps directly to the
resources in this tutorial:

- Azure Container Registry;
- Container Apps environment;
- Container App;
- Blob Storage;
- managed identity;
- role assignments;
- Log Analytics.

SST is not the right default for this project. It would add another abstraction
before the Azure model is understood, and it is not needed for this .NET-focused
Azure tutorial.

## Authentication Choice

Use Microsoft Entra ID bearer tokens.

The expected client is a corporate desktop application. The desktop app can sign
users in with MSAL, request an access token for the API, and send it in the
`Authorization` header.

The API should authorize by stable Entra identifiers:

- tenant ID;
- user object ID.

Do not use email addresses as durable identity keys.

Container Apps built-in auth may still be useful later, especially for browser
workloads, but in-app JWT bearer validation is clearer for a desktop API and
more portable across local and cloud environments.

## Data Choice

Start with Blob Storage only.

Each user's images should be stored under a user-owned prefix, with simple blob
metadata for content type, SHA-256, owner, and created time.

Do not add SQL until the application needs richer sharing, search, auditing, or
business rules.

When SQL is added, prefer explicit SQL through Dapper.AOT or careful ADO.NET if
Native AOT still matters.

## Native AOT Choice

Native AOT is a supported experiment, not a hard requirement.

The project should:

- keep the app AOT-friendly;
- use source-generated JSON;
- avoid reflection-heavy framework features;
- verify the published AOT container with the same E2E tests;
- keep a non-AOT container path available.

If Native AOT causes the application to become awkward, drop AOT and keep the
Container Apps architecture.

## Cost And Scaling

Use Container Apps Consumption with:

- `minReplicas = 0`;
- a small `maxReplicas` value, such as `3`;
- HTTP-based scaling;
- small CPU and memory settings until measured.

Scale-to-zero is appropriate for the PoC because low idle cost matters more
than avoiding the first-request cold start.

## Consequences

Positive consequences:

- the runtime model becomes easier to reason about;
- local Docker and cloud deployment align better;
- Native AOT becomes practical to test;
- the app is less coupled to Azure Functions-specific files and conventions;
- future database and auth choices are normal ASP.NET Core choices.

Tradeoffs:

- the project loses Azure Functions triggers and bindings;
- function keys are replaced by explicit auth;
- image build, push, tagging, and registry access become part of deployment;
- the tutorial must teach Container Apps concepts honestly;
- Native AOT still requires discipline and verification.

## Documentation Rule

The canonical tutorial should read as a fresh start-from-scratch guide for the
Container Apps design.

Repository-specific migration work belongs in
[the transition plan](transition-plan-functions-to-container-apps.md). Delete
stale Azure Functions terminology from active app docs as the implementation
moves over to Container Apps.
