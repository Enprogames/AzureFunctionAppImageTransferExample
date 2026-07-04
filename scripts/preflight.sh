#!/usr/bin/env bash
set -euo pipefail

: "${SUBSCRIPTION_ID:?Set SUBSCRIPTION_ID}"
: "${RESOURCE_GROUP:=rg-image-api-dev}"
: "${LOCATION:=canadacentral}"
: "${RESOURCE_PREFIX:=imgapi}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

require_command() {
  local command_name="$1"

  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command not found: $command_name" >&2
    exit 1
  fi
}

if [[ ! "$RESOURCE_PREFIX" =~ ^[a-z0-9]{3,10}$ ]]; then
  echo "RESOURCE_PREFIX must be 3-10 lowercase letters/numbers only." >&2
  exit 1
fi

if [[ ! "$LOCATION" =~ ^[a-z0-9]+$ ]]; then
  echo "LOCATION must use the Azure CLI location name, such as canadacentral or eastus2." >&2
  exit 1
fi

require_command az
require_command docker
require_command dotnet

az account set --subscription "$SUBSCRIPTION_ID"

az bicep build \
  --file "$PROJECT_ROOT/infra/registry.bicep" \
  --stdout >/dev/null

az bicep build \
  --file "$PROJECT_ROOT/infra/main.bicep" \
  --stdout >/dev/null

docker buildx version >/dev/null
dotnet --info >/dev/null

echo "Preflight passed."
