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
