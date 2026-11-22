#!/usr/bin/env bash
# Provision the Band D (D2) deployment: D1's infrastructure, unchanged in shape.
#
# Creates exactly: resource group, Basic container registry, Log Analytics +
# Application Insights, Foundry account + project + both capability hosts + one
# gpt-5.4 deployment. Nothing else - D2 adds no Azure resource, because the thing it
# adds is inside the container.
#
# Idempotent: re-running deploys the same resources in place.
#
# Env:
#   NAME_PREFIX  default bandd-durable   (resource group is rg-$NAME_PREFIX)
#   LOCATION     default eastus2
#   MODEL        default gpt-5.4
#   CAPACITY_K   default 50 (thousand tokens per minute)
#   MODEL_SKU    default GlobalStandard
#   DEPLOYMENT_NAME default bandd-durable
#   RUN_STAMP    default empty; set a fresh value after a teardown. A Foundry account that
#                was deleted with its resource group stays soft-deleted under its name,
#                and re-provisioning that name fails with FlagMustBeSetForRestore unless
#                the account is purged first. A fresh stamp gives every resource a new name.
#
# Usage: ./provision.sh
#
# Teardown is teardown.sh, and it is mandatory: this template deploys paid resources
# (registry, Foundry account, gpt-5.4 capacity, Log Analytics ingestion).

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

NAME_PREFIX="${NAME_PREFIX:-bandd-durable}"
LOCATION="${LOCATION:-eastus2}"
MODEL="${MODEL:-gpt-5.4}"
CAPACITY_K="${CAPACITY_K:-50}"
MODEL_SKU="${MODEL_SKU:-GlobalStandard}"
DEPLOYMENT_NAME="${DEPLOYMENT_NAME:-bandd-durable}"
RUN_STAMP="${RUN_STAMP:-}"
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
               modelCapacityK="$CAPACITY_K" runStamp="$RUN_STAMP" \
  --query 'properties.outputs' -o json > "$ROOT/.deployment.json.raw"

# Record the subscription alongside the outputs. teardown.sh targets this
# subscription explicitly, so a later `az account set` cannot make it delete
# nothing from the wrong place and call that clean.
python3 - "$ROOT/.deployment.json.raw" "$ROOT/.deployment.json" <<'PY'
import json, subprocess, sys
raw, out = sys.argv[1:3]
o = json.load(open(raw))
sub = subprocess.run(["az", "account", "show", "--query", "id", "-o", "tsv"],
                     capture_output=True, text=True, check=True).stdout.strip()
o["subscriptionId"] = {"type": "String", "value": sub}
json.dump(o, open(out, "w"), indent=2)
print("provisioned (subscription %s):" % sub)
for k, v in o.items():
    if k == "subscriptionId":
        continue
    print(f"  {k:24s} {v['value']}")
PY
rm -f "$ROOT/.deployment.json.raw"

echo
echo "next: ./deploy.sh   (build, push, register the Durable agent version)"
echo "then: ./invoke.sh   (prove the cross-restart approval)"
echo "last: ./teardown.sh (mandatory)"