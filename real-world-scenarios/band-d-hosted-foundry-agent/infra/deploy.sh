# deploy.sh — build, push, and register the accelerator's specialist agents as
# Foundry hosted agents.
#
# Runs against what provision.sh created. Sequence (from
# https://learn.microsoft.com/azure/foundry/agents/how-to/deploy-hosted-agent):
#   1. build each image for linux/amd64 (the platform will not run arm64 images)
#   2. push it to the registry
#   3. register an agent version that points at that image
#   4. wait for the version to become `active`
#   5. write deployment.env, ready for invoke.sh
#
# Agents deployed (AGENTS, space-separated; default "coverage synthesis"):
#   coverage   — NPI registry + CMS coverage MCP tools; produces the coverage
#                assessment that seeds the decision (agents/coverage/agent.yaml)
#   synthesis  — the gate-based APPROVE / PEND recommendation with its audit
#                trail (agents/synthesis/agent.yaml). Inputs are the three
#                specialist outputs; clinical and compliance are NOT deployed
#                here (see README, "what is not deployed"), so the synthesis
#                turn carries the coverage assessment as its specialist input
#                and says so. D2/D3 are where the other two arrive.
#
# Build contexts are assembled from upstream/ (agent source, verbatim) plus
# agent/requirements.txt (the one deliberate dependency change, agent/PATCHES.md).
#
# Roles this needs, relative to the identity that runs it:
#   - Contributor on the subscription/resource group (Bicep + ACR)
#   - User Access Administrator or Owner (User Access Administrator alone cannot
#     assign Owner/Built-in-Policy; the accelerator documents Contributor +
#     User Access Administrator for `azd up`, which this mirrors)
#   - Foundry Project Manager on the Foundry project, for create_version()
#
# Idempotent for the image (a unique tag per run_id; re-running creates a new
# version, which is the documented behaviour). It does not delete anything.
#
# Env: same as provision.sh, plus
#   AGENTS       default "coverage synthesis"
#   TAG          default band-d-<UTC timestamp> (one tag for all images)
#   CPU / MEMORY default 1 / 2Gi (agent.yaml declares 1 cpu / 2Gi for coverage)
#
# Usage: ./deploy.sh

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
UP="$ROOT/upstream/agents"

NAME_PREFIX="${NAME_PREFIX:-bandd-priorauth}"
RG="rg-$NAME_PREFIX"
MODEL="${MODEL:-gpt-5.4}"
AGENTS="${AGENTS:-coverage synthesis}"
TAG="${TAG:-band-d-$(date -u +%Y%m%d%H%M%S)}"
CPU="${CPU:-1}"
MEMORY="${MEMORY:-2Gi}"
RESOURCE="https://ai.azure.com"
API_VERSION="v1"

[ -f "$ROOT/.deployment.json" ] || { echo "deploy.sh: run ./provision.sh first (.deployment.json missing)." >&2; exit 1; }

read_out() { python3 -c "import json;print(json.load(open('$ROOT/.deployment.json'))['$1']['value'])"; }
ACCOUNT="$(read_out foundryAccountName)"
PROJECT="$(read_out foundryProjectName)"
ACR="$(read_out registryName)"
ACR_LOGIN="$(read_out registryLoginServer)"
BASE_URL="https://${ACCOUNT}.services.ai.azure.com/api/projects/${PROJECT}"

echo "registry : $ACR_LOGIN"
echo "agents   : $AGENTS"
echo "tag      : $TAG"
echo

# name_of <coverage|synthesis> — the registered name, identical to `name:` in the
# accelerator's agents/<dir>/agent.yaml, because the agent's own id must match it
# for Foundry trace correlation.
name_of() {
  case "$1" in
    coverage)  echo "coverage-assessment-agent" ;;
    synthesis) echo "synthesis-agent" ;;
    *) echo "deploy.sh: unknown agent '$1'" >&2; exit 2 ;;
  esac
}

# 1. Build through the registry's own build service: no local Docker requirement,
#    and ACR builds linux/amd64 by default, which is the platform's requirement.
echo "== build + push =="
for a in $AGENTS; do
  AGENT_NAME="$(name_of "$a")"
  BUILD_CTX="$ROOT/.build/$a"
  rm -rf "$BUILD_CTX"
  mkdir -p "$BUILD_CTX/skills"
  cp "$UP/$a/main.py" "$UP/$a/schemas.py" "$BUILD_CTX/"
  cp -R "$UP/$a"/skills/* "$BUILD_CTX/skills/"
  cp "$ROOT/agent/requirements.txt" "$BUILD_CTX/requirements.txt"
  cp "$ROOT/agent/Dockerfile" "$BUILD_CTX/Dockerfile"
  echo "-- $AGENT_NAME"
  az acr build --registry "$ACR" --image "$AGENT_NAME:$TAG" --platform linux/amd64 \
    "$BUILD_CTX" -o none
done

# 2. Let Foundry pull the image. Both MIs are granted: the accelerator records
#    that the account MI is used to validate the image at create_version() and the
#    project MI to run it, and that either omission surfaces as a generic 500.
echo "== roles =="
ACCOUNT_ID="$(az cognitiveservices account show -n "$ACCOUNT" -g "$RG" --query id -o tsv)"
PROJECT_ID="$(az resource show --ids "$ACCOUNT_ID/projects/$PROJECT" --query id -o tsv)"
ACCOUNT_PID="$(az cognitiveservices account show -n "$ACCOUNT" -g "$RG" --query identity.principalId -o tsv)"
PROJECT_PID="$(az resource show --ids "$PROJECT_ID" --query identity.principalId -o tsv)"
SIGNED_IN_PID="$(az ad signed-in-user show --query id -o tsv)"

assign() { # role-name principal-id scope
  az role assignment create --assignee-object-id "$2" --assignee-principal-type ServicePrincipal \
    --role "$1" --scope "$3" -o none 2>/dev/null || \
  az role assignment create --assignee-object-id "$2" --assignee-principal-type ServicePrincipal \
    --role "$1" --scope "$3" -o none
}
assign "AcrPull" "$ACCOUNT_PID" "$(read_out registryId)"
assign "AcrPull" "$PROJECT_PID" "$(read_out registryId)"
assign "Cognitive Services OpenAI User" "$PROJECT_PID" "$ACCOUNT_ID"
assign "Foundry User" "$PROJECT_PID" "$ACCOUNT_ID"
# Data-plane roles for the identity that registers and invokes the agent.
az role assignment create --assignee-object-id "$SIGNED_IN_PID" --assignee-principal-type User \
  --role "Foundry Project Manager" --scope "$PROJECT_ID" -o none 2>/dev/null || true
az role assignment create --assignee-object-id "$SIGNED_IN_PID" --assignee-principal-type User \
  --role "Foundry User" --scope "$PROJECT_ID" -o none 2>/dev/null || true

# 3. Register one version per agent. Fields are what the agent.yaml files declare,
#    expressed in the REST shape because `azd ai agent` is not part of the minimal
#    path. `session_configuration` is omitted on purpose, so the platform default
#    (900 s) applies and the observed behaviour is the default behaviour; see
#    evidence/platform-behaviour.md.
version_status() { # base-url agent-name
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

DEPLOYED=""
for a in $AGENTS; do
  AGENT_NAME="$(name_of "$a")"
  echo "== create version: $AGENT_NAME =="
  BODY="$(python3 - "$ACR_LOGIN" "$AGENT_NAME" "$TAG" "$MODEL" "$CPU" "$MEMORY" <<'PY2'
import json, sys
login, agent, tag, model, cpu, memory = sys.argv[1:7]
env = {
  "MODEL_DEPLOYMENT_NAME": model,
  "AZURE_OPENAI_DEPLOYMENT_NAME": model,
  "PYTHONNOUSERSITE": "1",
}
if agent.startswith("coverage"):
    env["MCP_NPI_REGISTRY"] = "https://hcls.mcp.claude.com/npi_registry/mcp"
    env["MCP_CMS_COVERAGE"] = "https://hcls.mcp.claude.com/cms_coverage/mcp"
print(json.dumps({
  "definition": {
    "kind": "hosted",
    "container_configuration": {"image": f"{login}/{agent}:{tag}"},
    "cpu": cpu,
    "memory": memory,
    "protocol_versions": [{"protocol": "responses", "version": "2.0.0"}],
    "environment_variables": env,
  }
}))
PY2
)"
  echo "$BODY" > "$ROOT/.agent-version-$a.request.json"
  az rest --method POST \
    --url "$BASE_URL/agents/$AGENT_NAME/versions?api-version=$API_VERSION" \
    --resource "$RESOURCE" --body "$BODY" -o json > "$ROOT/.agent-version-$a.json"

  echo "== wait for active: $AGENT_NAME =="
  S=""
  for i in $(seq 1 60); do
    S="$(version_status "$BASE_URL" "$AGENT_NAME")"
    echo "  [$i] $S"
    [ "$S" = "active" ] && break
    [ "$S" = "failed" ] && { echo "deploy.sh: $AGENT_NAME version provisioning failed." >&2; exit 1; }
    sleep 10
  done
  [ "$S" = "active" ] || { echo "deploy.sh: $AGENT_NAME did not become active in 10 minutes." >&2; exit 1; }
  DEPLOYED="$DEPLOYED $a=$AGENT_NAME"
done

python3 - "$ROOT/deployment.env" "$BASE_URL" "$TAG" "$DEPLOYED" <<'PY2'
import sys
path, base, tag, deployed = sys.argv[1:5]
pairs = dict(p.split("=", 1) for p in deployed.split())
lines = [f"FOUNDRY_BASE_URL={base}", f"IMAGE_TAG={tag}"]
for k, v in pairs.items():
    lines.append(f"AGENT_{k.upper()}={v}")
open(path, "w").write("\n".join(lines) + "\n")
print("wrote", path)
PY2

echo
echo "registered:$DEPLOYED"
echo "next: ./invoke.sh"
