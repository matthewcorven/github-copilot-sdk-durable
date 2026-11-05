#!/usr/bin/env bash
# teardown.sh — remove EVERYTHING provision.sh created.
#
# One resource group (see infra/main.bicep), so teardown is a resource-group delete:
# registry, Foundry account + project + capability hosts, the gpt-5.4 deployment, Log
# Analytics and Application Insights all go together. D2 adds no Azure resource, so the
# group is the whole deployment.
#
# Subscription: the deployment that provision.sh created records its subscription in
# .deployment.json, and teardown targets that one explicitly on every call. Without it,
# an operator whose `az` default subscription has moved would delete nothing from the
# deployment and still be told it was clean. If .deployment.json is gone, the current
# default subscription is used and printed, so the choice is visible rather than implied.
#
# Failure is not absence. "The group does not exist" is decided by `az group exists`,
# which answers true/false and exits non-zero only when the query itself failed; a failed
# `az resource list` aborts the run instead of reading as "nothing left".
#
# A resource group delete is not the whole of a Foundry teardown. The account is left
# SOFT-DELETED under its name for the retention window, and a later provision of that name
# fails with FlagMustBeSetForRestore (see ../evidence/teardown.md). So after the group is
# gone this script purges the account too, and reports whether any soft-deleted account of
# that name remains. "Not found" on purge is the already-purged case and is not a failure.
#
# Idempotent by construction: a missing group is a success, and so is a purge whose target
# is already gone. A second invocation prints "already absent" and exits 0.
#
# Env:
#   NAME_PREFIX       default bandd-durable (resource group rg-$NAME_PREFIX)
#   SUBSCRIPTION_ID   default: the subscription recorded in .deployment.json, else the
#                     current `az` default
#   WAIT              default 1. When 1, waits for the delete and then verifies.
#   EXTRA_GROUPS      space-separated extra resource groups to delete too.
#
# Usage: ./teardown.sh
#
# Exit codes: 0 everything is gone (or was already gone); 1 an az call failed;
#             2 a group still exists after the wait. A non-zero exit is never reported
#             as a clean teardown.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
RECORD="$ROOT/.deployment.json"

NAME_PREFIX="${NAME_PREFIX:-bandd-durable}"
RG="rg-$NAME_PREFIX"
WAIT="${WAIT:-1}"

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
AZ_SUB=(--subscription "$SUB")

echo "teardown: subscription $(az account show --subscription "$SUB" --query name -o tsv) ($SUB)"

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

resources_in() {
  local out
  out="$(az resource list --resource-group "$1" --query '[].id' -o tsv "${AZ_SUB[@]}")" || {
    echo "teardown.sh: 'az resource list -g $1' failed; refusing to report anything." >&2
    exit 1
  }
  printf '%s\n' "$out"
}

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
  if group_exists "$RG"; then
    echo "  [$i] $RG: still present"
    sleep 10
  else
    echo "  [$i] $RG: gone"
    break
  fi
done

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

# A resource created outside the group would survive a group delete. The deployment
# names its own resources, so a name-scoped list over the whole subscription is what
# catches that; it is a second check, not a substitute for the first.
echo "teardown: nothing named for this deployment remains anywhere in the subscription"
LEFTOVER="$(az resource list --subscription "$SUB" \
  --query "[?starts_with(name, '${NAME_PREFIX}') || starts_with(name, 'aif-') && contains(tags.\"real-world-scenario\", 'band-d-durable-inside')].id" \
  -o tsv)" || {
  echo "teardown.sh: the subscription-wide 'az resource list' failed; refusing to report anything." >&2
  exit 1
}
if [ -n "${LEFTOVER//[$'\n']/}" ]; then
  echo "  resources still present:" >&2
  printf '%s\n' "$LEFTOVER" | sed 's/^/    /' >&2
  STATUS=2
else
  echo "  none"
fi

if [ "$STATUS" -ne 0 ]; then
  echo "teardown.sh: teardown incomplete (see above)." >&2
  exit "$STATUS"
fi

# --- purge the Foundry account's soft-deleted record ---------------------------

ACCOUNT=""
if [ -f "$RECORD" ]; then
  ACCOUNT="$(python3 - "$RECORD" <<'PY'
import json, sys
o = json.load(open(sys.argv[1]))
print(o.get("foundryAccountName", {}).get("value") or "")
PY
)"
fi

echo "teardown: foundry account soft-delete record"
if [ -n "$ACCOUNT" ]; then
  LOCATION="$(python3 - "$RECORD" <<'PY'
import json, sys
o = json.load(open(sys.argv[1]))
print(o.get("location", {}).get("value") or "eastus2")
PY
)"
  echo "  $ACCOUNT ($LOCATION): purging"
  PURGE_OUT="$(az cognitiveservices account purge -n "$ACCOUNT" -g "$RG" -l "$LOCATION" 2>&1)" || {
    echo "teardown.sh: 'az cognitiveservices account purge -n $ACCOUNT' failed; refusing to report anything." >&2
    printf '%s\n' "$PURGE_OUT" | sed 's/^/    /' >&2
    exit 1
  }
  REMAINING="$(az cognitiveservices account list-deleted --query "[?name=='$ACCOUNT'].name" -o tsv)" || {
    echo "teardown.sh: the soft-deleted account list failed; refusing to report anything." >&2
    exit 1
  }
  if [ -n "${REMAINING//[$'\n']/}" ]; then
    echo "  $ACCOUNT: STILL SOFT-DELETED; re-provisioning this name will fail" >&2
    STATUS=2
  else
    echo "  $ACCOUNT: purged, no soft-delete record remains"
  fi
else
  echo "  no foundryAccountName recorded; nothing to purge"
fi

if [ "$STATUS" -ne 0 ]; then
  echo "teardown.sh: teardown incomplete (see above)." >&2
  exit "$STATUS"
fi

echo
echo "teardown complete. A second run is harmless (the group and the account record are already absent)."
