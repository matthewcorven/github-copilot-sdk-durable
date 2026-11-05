#!/usr/bin/env bash
# teardown.sh — remove EVERYTHING deploy.sh provisioned.
#
# The minimal deployment is one resource group (see infra/main.bicep), so
# teardown is a resource-group delete: registry, Foundry account + project +
# capability hosts, gpt-5.4 deployment, Log Analytics and Application Insights
# all go together. Deleting the group is what makes "nothing is left running"
# checkable with a single `az group show` afterwards.
#
# Idempotent by construction: a missing group is a success. A second invocation
# prints "nothing to do" and exits 0.
#
# Destructive by design. The issue requires it: nothing is left running.
#
# Env:
#   NAME_PREFIX   default bandd-priorauth (resource group rg-$NAME_PREFIX)
#   WAIT          default 1. When 1, waits for the delete to complete and then
#                 verifies with `az resource list` that nothing of ours remains.
#   EXTRA_GROUPS  space-separated extra resource groups to delete as well (used
#                 for any probe groups a debug session left behind).
#
# Usage: ./teardown.sh

set -euo pipefail

NAME_PREFIX="${NAME_PREFIX:-bandd-priorauth}"
RG="rg-$NAME_PREFIX"
WAIT="${WAIT:-1}"

delete_group() {
  local group="$1"
  if ! az group show --name "$group" >/dev/null 2>&1; then
    echo "  $group: already absent"
    return 0
  fi
  echo "  $group: deleting"
  az group delete --name "$group" --yes --no-wait -o none
}

if ! az account show >/dev/null 2>&1; then
  echo "teardown.sh: not signed in; nothing to do (and nothing verified)." >&2
  exit 1
fi

echo "teardown: subscription $(az account show --query name -o tsv)"
echo "teardown: resource groups"
delete_group "$RG"
for g in ${EXTRA_GROUPS:-}; do
  delete_group "$g"
done

if [ "$WAIT" != "1" ]; then
  echo
  echo "not waiting (WAIT=0). Re-run without WAIT=0 to verify emptiness."
  exit 0
fi

echo "teardown: waiting for deletion to finish"
for i in $(seq 1 60); do
  state="$(az group show --name "$RG" --query properties.provisioningState -o tsv 2>/dev/null || echo absent)"
  echo "  [$i] $RG: $state"
  [ "$state" = "absent" ] && break
  sleep 10
done

echo "teardown: verification"
left="$(az resource list --query "[?resourceGroup=='$RG'].id" -o tsv 2>/dev/null || true)"
for g in ${EXTRA_GROUPS:-}; do
  left="$left$(az resource list --query "[?resourceGroup=='$g'].id" -o tsv 2>/dev/null || true)"
done
if [ -n "${left//[$'\n']/}" ]; then
  echo "  LEFTOVER RESOURCES:" >&2
  echo "$left" | sed 's/^/    /' >&2
  exit 1
fi
echo "  no resources remain in $RG${EXTRA_GROUPS:+ or $EXTRA_GROUPS}"
echo
echo "teardown complete. A second run is harmless (the group is already absent)."
