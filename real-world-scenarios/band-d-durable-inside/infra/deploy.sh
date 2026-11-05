#!/usr/bin/env bash
# deploy.sh - build, push, and register the Durable hosted agent.
#
# Same five steps as D1 (build -> push -> grant pull roles -> register -> wait for
# active), with one difference that matters: the build context carries the repository's
# own Durable layer. `agent/Dockerfile` installs lib/py as a package, so the image runs
# the same `durable.py` every other scenario and suite in this repository runs, at the
# commit under test - not a vendored copy.
#
# The version is registered with `session_configuration.idle_timeout_seconds` set to
# 900, which is the platform default: it is stated explicitly here because the restart
# in invoke.sh depends on it, and a default that changes under the evidence would
# change what the evidence means.
#
# Roles this needs, relative to the identity that runs it:
#   - Contributor on the subscription/resource group (Bicep + ACR)
#   - User Access Administrator or Owner (role assignments)
#   - Foundry Project Manager on the Foundry project, for create_version()
#
# Env: same as provision.sh, plus
#   TAG          default band-d2-<UTC timestamp>
#   CPU / MEMORY default 1 / 2Gi
#
# Usage: ./deploy.sh

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
AGENT_DIR="$ROOT/agent"
LIB_PY="$(cd "$ROOT/../../lib/py" && pwd)"
BUILD_CTX="$ROOT/.build/durable-inside"

NAME_PREFIX="${NAME_PREFIX:-bandd-durable}"
RG="rg-$NAME_PREFIX"
MODEL="${MODEL:-gpt-5.4}"
TAG="${TAG:-band-d2-$(date -u +%Y%m%d%H%M%S)}"
CPU="${CPU:-1}"
MEMORY="${MEMORY:-2Gi}"
RESOURCE="https://ai.azure.com"
API_VERSION="v1"
AGENT_NAME="durable-inside-agent"

[ -f "$ROOT/.deployment.json" ] || { echo "deploy.sh: run ./provision.sh first (.deployment.json missing)." >&2; exit 1; }
[ -f "$LIB_PY/durable.py" ] || { echo "deploy.sh: $LIB_PY/durable.py is missing; this scenario consumes the layer from lib/py." >&2; exit 1; }

read_out() { python3 -c "import json;print(json.load(open('$ROOT/.deployment.json'))['$1']['value'])"; }
ACCOUNT="$(read_out foundryAccountName)"
PROJECT="$(read_out foundryProjectName)"
ACR="$(read_out registryName)"
ACR_LOGIN="$(read_out registryLoginServer)"
BASE_URL="https://${ACCOUNT}.services.ai.azure.com/api/projects/${PROJECT}"

echo "registry : $ACR_LOGIN"
echo "agent    : $AGENT_NAME"
echo "tag      : $TAG"
echo "layer    : $LIB_PY/durable.py"
echo

# 1. Assemble the build context. The Durable package goes in as `durable-package/`,
#    which is the repository's own tree - `pyproject.toml`, `durable.py`, nothing else.
#    The build context is thrown away after the build.
rm -rf "$BUILD_CTX"
mkdir -p "$BUILD_CTX"
cp "$AGENT_DIR/main.py" "$AGENT_DIR/durable_state.py" "$AGENT_DIR/d2_tool.py" \
   "$AGENT_DIR/requirements.txt" "$AGENT_DIR/Dockerfile" "$BUILD_CTX/"
mkdir -p "$BUILD_CTX/durable-package"
cp "$LIB_PY/durable.py" "$LIB_PY/pyproject.toml" "$BUILD_CTX/durable-package/"

echo "== build + push =="
az acr build --registry "$ACR" --image "$AGENT_NAME:$TAG" --platform linux/amd64 \
  "$BUILD_CTX" -o none

# 2. Let Foundry pull the image. Both identities are granted: the account MI validates
#    the image at create_version(), the project MI runs it, and either omission surfaces
#    as a generic 500 (D1's evidence and the accelerator's own notes).
echo "== roles =="
ACCOUNT_ID="$(az cognitiveservices account show -n "$ACCOUNT" -g "$RG" --query id -o tsv)"
PROJECT_ID="$(az resource show --ids "$ACCOUNT_ID/projects/$PROJECT" --query id -o tsv)"
ACCOUNT_PID="$(az cognitiveservices account show -n "$ACCOUNT" -g "$RG" --query identity.principalId -o tsv)"
PROJECT_PID="$(az resource show --ids "$PROJECT_ID" --query identity.principalId -o tsv)"
SIGNED_IN_PID="$(az ad signed-in-user show --query id -o tsv)"

assign() { # role principal-id scope
  az role assignment create --assignee-object-id "$2" --assignee-principal-type ServicePrincipal \
    --role "$1" --scope "$3" -o none 2>/dev/null || \
  az role assignment create --assignee-object-id "$2" --assignee-principal-type ServicePrincipal \
    --role "$1" --scope "$3" -o none
}
assign "AcrPull" "$ACCOUNT_PID" "$(read_out registryId)"
assign "AcrPull" "$PROJECT_PID" "$(read_out registryId)"
assign "Cognitive Services OpenAI User" "$PROJECT_PID" "$ACCOUNT_ID"
assign "Foundry User" "$PROJECT_PID" "$ACCOUNT_ID"
az role assignment create --assignee-object-id "$SIGNED_IN_PID" --assignee-principal-type User \
  --role "Foundry Project Manager" --scope "$PROJECT_ID" -o none 2>/dev/null || true
az role assignment create --assignee-object-id "$SIGNED_IN_PID" --assignee-principal-type User \
  --role "Foundry User" --scope "$PROJECT_ID" -o none 2>/dev/null || true

# 3. Register the version.
echo "== create version: $AGENT_NAME =="
BODY="$(python3 - "$ACR_LOGIN" "$AGENT_NAME" "$TAG" "$MODEL" "$CPU" "$MEMORY" <<'PY2'
import json, sys
login, agent, tag, model, cpu, memory = sys.argv[1:7]
env = {
  "MODEL_DEPLOYMENT_NAME": model,
  "AZURE_OPENAI_DEPLOYMENT_NAME": model,
  "PYTHONUNBUFFERED": "1",
  # Where the working state goes. Nothing sets this on the platform, so the agent
  # resolves it under HOME - the session volume - and says where in its startup line.
  # It is named here only to make the default visible in the version definition.
}
print(json.dumps({
  "definition": {
    "kind": "hosted",
    "container_configuration": {"image": f"{login}/{agent}:{tag}"},
    "cpu": cpu,
    "memory": memory,
    "protocol_versions": [{"protocol": "responses", "version": "2.0.0"}],
    "environment_variables": env,
    # Stated, not inherited: the restart in invoke.sh waits out an idle window, and the
    # window is part of what the evidence is about.
    "session_configuration": {"idle_timeout_seconds": 900},
  }
}))
PY2
)"
echo "$BODY" > "$ROOT/.agent-version.request.json"
az rest --method POST \
  --url "$BASE_URL/agents/$AGENT_NAME/versions?api-version=$API_VERSION" \
  --resource "$RESOURCE" --body "$BODY" -o json > "$ROOT/.agent-version.json"

echo "== wait for active =="
version_status() {
  az rest --method GET \
    --url "$1/agents/$2/versions?api-version=$API_VERSION" \
    --resource "$RESOURCE" -o json 2>/dev/null \
  | python3 -c '
import json, sys
try:
    d = json.load(sys.stdin)
except Exception:
    print(""); raise SystemExit
items = d.get("data") or d.get("value") or []
if items:
    items.sort(key=lambda x: str(x.get("created_at") or x.get("version") or ""))
    print(items[-1].get("status") or "")
'
}
S=""
for i in $(seq 1 60); do
  S="$(version_status "$BASE_URL" "$AGENT_NAME")"
  echo "  [$i] $S"
  [ "$S" = "active" ] && break
  [ "$S" = "failed" ] && { echo "deploy.sh: $AGENT_NAME version provisioning failed." >&2; exit 1; }
  sleep 10
done
[ "$S" = "active" ] || { echo "deploy.sh: $AGENT_NAME did not become active in 10 minutes." >&2; exit 1; }

cat > "$ROOT/deployment.env" <<ENV
FOUNDRY_BASE_URL=$BASE_URL
AGENT=$AGENT_NAME
IMAGE_TAG=$TAG
ENV
echo
echo "registered: $AGENT_NAME ($TAG)"
echo "next: ./invoke.sh"
