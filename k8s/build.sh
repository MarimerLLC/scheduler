#!/bin/bash
set -euo pipefail

# Build and push the Scheduler image (web app plus migration bundle).
# Usage: ./build.sh <tag>        e.g. ./build.sh 1.0.0
# Tags are versions, not "latest": the migration Job and the Deployment must run the same image.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
IMAGE_NAME="rockylhotka/scheduler"
TAG="${1:?Usage: ./build.sh <tag>, e.g. ./build.sh 1.0.0}"

if [[ -n "$(git -C "$REPO_ROOT" status --porcelain)" ]]; then
    echo "The working tree has uncommitted changes; build from a clean checkout of what you mean to deploy." >&2
    exit 1
fi

echo "Building $IMAGE_NAME:$TAG from $(git -C "$REPO_ROOT" rev-parse --short HEAD)..."
docker build -t "$IMAGE_NAME:$TAG" "$REPO_ROOT"

echo ""
echo "Pushing $IMAGE_NAME:$TAG..."
docker push "$IMAGE_NAME:$TAG"

echo ""
echo "Pushed $IMAGE_NAME:$TAG. To deploy it: ./deploy.sh $TAG"
