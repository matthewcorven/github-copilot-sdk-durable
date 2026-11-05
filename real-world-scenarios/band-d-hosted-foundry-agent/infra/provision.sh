#!/usr/bin/env bash
# Provision the MINIMAL Band D hosted deployment (see infra/main.bicep).
#
# Creates exactly: resource group, Basic container registry, Log Analytics +
# Application Insights, Foundry account + project + both capability hosts +
# one gpt-5.4 deployment. No Container Apps, no backend, no frontend.
#
# This script only provisions infrastructure. It does not build or register the
# agent image; deploy.sh does that, because registration needs roles that
# depend on the deploying identity.
#
# Idempotent: re-running deploys the same resources in place.
#
# Env:
#   NAME_PREFIX  default bandd-priorauth   (resource group is rg-$NAME_PREFIX)
#   LOCATION     default eastus2
#   MODEL        default gpt-5.4
#   CAPACITY_K   default 50 (thousand tokens per minute)
#   MODEL_SKU    default GlobalStandard (DataZoneStandard is East US 2 only)
#   DEPLOYMENT_NAME default bandd-priorauth
#
# Usage: ./provision.sh
#
# Teardown is teardown.sh, and it is mandatory: this template deploys paid
# resources (registry, Foundry account, gpt-5.4 capacity, Log Analytics ingestion).

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

NAME_PREFIX="${NAME_PREFIX:-bandd-priorauth}"
LOCATION="${LOCATION:-eastus2}"
MODEL="${MODEL:-gpt-5.4}"
CAPACITY_K="${CAPACITY_K:-50}"
MODEL_SKU="${MODEL_SKU:-GlobalStandard}"
DEPLOYMENT_NAME="${DEPLOYMENT_NAME:-bandd-priorauth}"
RG="rg-$NAME_PREFIX"

if ! az account show >/dev/null 2>&1; then
  echo "provision.sh: not signed in; run 'az login' first." >&2
  exit 1
fi

echo "subscription : $(az account show --query name -o tsv)"
echo "resource grp : $RG ($LOCATION)"
echo "template     : $HERE/main.bicep"
echo

az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file "$HERE/main.bicep" \
  --parameters namePrefix="$NAME_PREFIX" location="$LOCATION" \
               modelDeploymentName="$MODEL" modelDeploymentSku="$MODEL_SKU" \
               modelCapacityK="$CAPACITY_K" \
  --query 'properties.outputs' -o json > "$ROOT/.deployment.json"

python3 - "$ROOT/.deployment.json" <<'PY'
import json, sys
o = json.load(open(sys.argv[1]))
print("provisioned:")
for k, v in o.items():
    print(f"  {k:24s} {v['value']}")
PY

echo
echo "next: ./deploy.sh   (build, push, register the agent version)"
echo "then: ./invoke.sh   (prove one prior-auth run)"
echo "last: ./teardown.sh (mandatory)"
