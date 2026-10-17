#!/bin/bash
set -euo pipefail

# Deploy Scheduler to the current kubectl context (lhotkalake): migrations first, then the app.
# Usage: ./deploy.sh <tag>        e.g. ./deploy.sh 1.0.0   (an image pushed by ./build.sh)
# The secrets scheduler-app and scheduler-identity must exist first: see docs/deployment.md.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TAG="${1:?Usage: ./deploy.sh <tag>, e.g. ./deploy.sh 1.0.0}"
NS=scheduler

echo "Context: $(kubectl config current-context)"
echo "Running now: $(kubectl get deployment scheduler -n $NS -o jsonpath='{.spec.template.spec.containers[0].image}' 2>/dev/null || echo 'nothing')"
echo "Deploying:   rockylhotka/scheduler:$TAG"
read -r -p "Continue? [y/N] " answer
[[ "$answer" == [yY] ]] || exit 1

with_tag() { sed "s/IMAGE_TAG/$TAG/g" "$1"; }

kubectl apply -f "$SCRIPT_DIR/namespace.yaml"
kubectl apply -f "$SCRIPT_DIR/configmap.yaml"

for secret in scheduler-app scheduler-identity; do
    if ! kubectl get secret "$secret" -n $NS >/dev/null 2>&1; then
        echo "Secret $secret is missing in namespace $NS. See docs/deployment.md, step 3." >&2
        exit 1
    fi
done

echo ""
echo "Applying migrations..."
kubectl delete job scheduler-migrate -n $NS --ignore-not-found
with_tag "$SCRIPT_DIR/migrate-job.yaml" | kubectl apply -f -
if ! kubectl wait --for=condition=complete job/scheduler-migrate -n $NS --timeout=300s; then
    echo "Migrations failed; the running version is untouched. Job log:" >&2
    kubectl logs job/scheduler-migrate -n $NS >&2 || true
    exit 1
fi
kubectl logs job/scheduler-migrate -n $NS | tail -5

echo ""
echo "Deploying the app..."
with_tag "$SCRIPT_DIR/deployment.yaml" | kubectl apply -f -
kubectl apply -f "$SCRIPT_DIR/service.yaml"
kubectl apply -f "$SCRIPT_DIR/ingress.yaml"
kubectl rollout status deployment/scheduler -n $NS --timeout=300s

echo ""
echo "Deployed rockylhotka/scheduler:$TAG. Check it: https://scheduler.tail920062.ts.net/.well-known/oauth-protected-resource/mcp"
