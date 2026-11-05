#!/usr/bin/env bash
# teardown.sh — remove EVERYTHING deploy.sh provisioned.
#
# The minimal deployment is one resource group (see infra/main.bicep), so
# teardown is a resource-group delete: registry, Foundry account + project +
# capability hosts, gpt-5.4 deployment, Log Analytics and Application Insights
# all go together. Deleting the group is what makes "nothing is left running"
# checkable afterwards.
#
# Subscription: the deployment that provision.sh created records its
# subscription in .deployment.json, and teardown targets that one explicitly on
# every call. Without it, an operator whose `az` default subscription has moved
# would delete nothing from the deployment and still be told it was clean. If
# .deployment.json is gone, the current default subscription is used and printed
# so the choice is visible rather than implied.
#
# Failure is not absence. "The group does not exist" is decided by
# `az group exists`, which answers true/false and exits non-zero only when the
# query itself failed; a failed `az resource list` aborts the run instead of
# reading as "nothing left".
#
# Idempotent by construction: a missing group is a success. A second invocation
# prints "already absent" and exits 0.
#
# Destructive by design. The issue requires it: nothing is left running.
#
# Env:
#   NAME_PREFIX       default bandd-priorauth (resource group rg-$NAME_PREFIX)
#   SUBSCRIPTION_ID   default: the subscription recorded in .deployment.json,
#                     else the current `az` default
#   WAIT              default 1. When 1, waits for the delete to complete and then
#                     verifies that the groups are gone.
#   EXTRA_GROUPS      space-separated extra resource groups to delete as well (used
#                     for any probe groups a debug session left behind).
#
# Usage: ./teardown.sh
#
# Exit codes: 0 everything is gone (or was already gone); 1 an az call failed;
#             2 a group still exists after the wait. A non-zero exit is never
#             reported as a clean teardown.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
RECORD="$ROOT/.deployment.json"

NAME_PREFIX="${NAME_PREFIX:-bandd-priorauth}"
RG="rg-$NAME_PREFIX"
WAIT="${WAIT:-1}"

# --- subscription -----------------------------------------------------------

if ! az account show >/dev/null; then
  echo "teardown.sh: not signed in; nothing torn down, nothing verified." >&2
  exit 1
fi

DEFAULT_SUB="$(az account show --query id -o tsv)"
SUB="${SUBSCRIPTION_ID:-}"
if [ -z "$SUB" ] && [ -f "$RECORD" ]; then
  SUB="$(python3 - "$RECORD" <<'PY'
import json, sys
o = json.load(open(sys.argv[1]))
print(o.get("subscriptionId", {}).get("value") or "")
PY
)"
  if [ -n "$SUB" ]; then echo "teardown: subscription from $RECORD"; fi
fi
SUB="${SUB:-$DEFAULT_SUB}"
if [ "$SUB" != "$DEFAULT_SUB" ]; then
  echo "teardown: targeting $SUB (the current az default is $DEFAULT_SUB)"
fi
# `--subscription` is a global flag that must follow the command group, not
# precede the verb: `az --subscription X group exists` is parsed as a verb named X.
AZ_SUB=(--subscription "$SUB")

echo "teardown: subscription $(az account show --subscription "$SUB" --query name -o tsv) ($SUB)"

# --- helpers ----------------------------------------------------------------

# Existence is a boolean from `az group exists`; a non-zero exit is a failed
# query, not an absence, and aborts under `set -e`.
group_exists() {
  local out
  out="$(az group exists --name "$1" "${AZ_SUB[@]}")" || {
    echo "teardown.sh: 'az group exists --name $1' failed; refusing to report anything." >&2
    exit 1
  }
  [ "$out" = "true" ]
}

delete_group() {
  local group="$1"
  if ! group_exists "$group"; then
    echo "  $group: already absent"
    return 0
  fi
  echo "  $group: deleting"
  az group delete --name "$group" --yes --no-wait -o none "${AZ_SUB[@]}"
}

# Prints the ids of every resource still in the group. A failed query aborts:
# an empty list is only ever produced by a query that succeeded.
resources_in() {
  local out
  out="$(az resource list --resource-group "$1" --query '[].id' -o tsv "${AZ_SUB[@]}")" || {
    echo "teardown.sh: 'az resource list -g $1' failed; refusing to report anything." >&2
    exit 1
  }
  printf '%s\n' "$out"
}

# --- delete -----------------------------------------------------------------

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

# --- wait -------------------------------------------------------------------

echo "teardown: waiting for deletion to finish"
for i in $(seq 1 60); do
  if group_exists "$RG"; then
    echo "  [$i] $RG: still present"
    sleep 10
  else
    echo "  [$i] $RG: gone"
    break
  fi
done

# --- verify -----------------------------------------------------------------

# Absence of the group is the proof: a group that no longer exists holds no
# resources. A group that survived the wait is reported with its contents rather
# than being read as a completed delete.
echo "teardown: verification"
STATUS=0
for g in "$RG" ${EXTRA_GROUPS:-}; do
  if group_exists "$g"; then
    echo "  $g: STILL EXISTS" >&2
    left="$(resources_in "$g")"
    if [ -n "${left//[$'\n']/}" ]; then
      echo "    resources:" >&2
      printf '%s\n' "$left" | sed 's/^/      /' >&2
    fi
    STATUS=2
  else
    echo "  $g: absent, no resources remain"
  fi
done

if [ "$STATUS" -ne 0 ]; then
  echo "teardown.sh: teardown incomplete (see above)." >&2
  exit "$STATUS"
fi

echo
echo "teardown complete. A second run is harmless (the groups are already absent)."
