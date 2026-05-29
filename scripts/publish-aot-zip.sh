#!/usr/bin/env bash
set -euo pipefail

: "${RESOURCE_GROUP:=rg-image-fn-dev}"
: "${FUNCTION_APP_NAME:?Set FUNCTION_APP_NAME}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

cd "$PROJECT_ROOT"

ARTIFACT_ROOT=".artifacts"
PUBLISH_DIR="$ARTIFACT_ROOT/publish/ImageFunctions"
ZIP_PATH="$ARTIFACT_ROOT/ImageFunctions.zip"

rm -rf "$PUBLISH_DIR" "$ZIP_PATH"
mkdir -p "$PUBLISH_DIR"

# Build linux-x64 Native AOT output in Docker and copy publish output to PUBLISH_DIR.
docker buildx build \
  --file src/ImageFunctions/Dockerfile \
  --target export \
  --output "type=local,dest=$PUBLISH_DIR" \
  .

# Zip the contents of the publish directory, not the parent directory.
# This preserves the Functions-required root layout: host.json, worker.config.json,
# functions.metadata, extensions.json, and .azurefunctions/ at the ZIP root.
(
  cd "$PUBLISH_DIR"
  zip -r "../../ImageFunctions.zip" .
)

az functionapp deployment source config-zip \
  --resource-group "$RESOURCE_GROUP" \
  --name "$FUNCTION_APP_NAME" \
  --src "$ZIP_PATH"

echo "Published $ZIP_PATH to $FUNCTION_APP_NAME"
