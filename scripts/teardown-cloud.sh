#!/usr/bin/env bash
set -euo pipefail

: "${RESOURCE_GROUP:=rg-image-fn-dev}"

az group delete \
  --name "$RESOURCE_GROUP" \
  --yes \
  --no-wait

echo "Deletion started for resource group $RESOURCE_GROUP"
