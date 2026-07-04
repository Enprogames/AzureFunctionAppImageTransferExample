#!/usr/bin/env bash
set -euo pipefail

: "${SUBSCRIPTION_ID:?Set SUBSCRIPTION_ID}"
: "${RESOURCE_GROUP:=rg-image-api-dev}"
: "${LOCATION:=canadacentral}"
: "${RESOURCE_PREFIX:=imgapi}"
: "${ACR_NAME:?Set ACR_NAME.}"
: "${CONTAINER_IMAGE:?Set CONTAINER_IMAGE, including registry and tag.}"
: "${AUTHENTICATION_TENANT_ID:?Set AUTHENTICATION_TENANT_ID.}"
: "${AUTHENTICATION_AUDIENCE:?Set AUTHENTICATION_AUDIENCE, usually api://<api-client-id>.}"
: "${MIN_REPLICAS:=0}"
: "${MAX_REPLICAS:=3}"
: "${DEPLOYMENT_NAME:=main}"

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
  --tags project=image-transfer-api environment=dev

az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --template-file infra/main.bicep \
  --parameters \
    location="$LOCATION" \
    resourcePrefix="$RESOURCE_PREFIX" \
    acrName="$ACR_NAME" \
    containerImage="$CONTAINER_IMAGE" \
    authenticationTenantId="$AUTHENTICATION_TENANT_ID" \
    authenticationAudience="$AUTHENTICATION_AUDIENCE" \
    minReplicas="$MIN_REPLICAS" \
    maxReplicas="$MAX_REPLICAS"

az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --template-file infra/main.bicep \
  --parameters \
    location="$LOCATION" \
    resourcePrefix="$RESOURCE_PREFIX" \
    acrName="$ACR_NAME" \
    containerImage="$CONTAINER_IMAGE" \
    authenticationTenantId="$AUTHENTICATION_TENANT_ID" \
    authenticationAudience="$AUTHENTICATION_AUDIENCE" \
    minReplicas="$MIN_REPLICAS" \
    maxReplicas="$MAX_REPLICAS"

CONTAINER_APP_NAME="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.containerAppName.value \
  -o tsv)"

API_BASE_URL="$(az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --query properties.outputs.apiBaseUrl.value \
  -o tsv)"

echo "Container App: $CONTAINER_APP_NAME"
echo "API_BASE_URL=$API_BASE_URL"
