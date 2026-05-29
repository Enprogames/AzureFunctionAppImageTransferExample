#!/usr/bin/env bash
set -euo pipefail

: "${RESOURCE_GROUP:=rg-image-fn-dev}"
: "${FUNCTION_APP_NAME:?Set FUNCTION_APP_NAME}"

az functionapp keys list \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --query "functionKeys.default" \
  -o tsv
