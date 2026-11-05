#!/usr/bin/env bash
# teardown.sh — remove EVERYTHING deploy.sh provisioned.
#
# The minimal deployment is one resource group (see infra/main.bicep), so
# teardown is a resource-group delete plus a purge: registry, Foundry account +
# project + capability hosts, gpt-5.4 deployment, Log Analytics and Application
# Insights all go with the group, and the Foundry account is then purged from the
# soft-deleted list it lands in. Both halves are needed for "nothing is left
# behind" to be true: the group delete frees the resources, and only the purge
# frees the *name*, which is what a later provision.sh needs.
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
# Three checks make up the verification, and they cover different mistakes:
#   - each group is absent            (the delete worked)
#   - no soft-deleted account remains (the *name* is free, so provision.sh can run)
#   - no resource anywhere in the subscription carries this deployment's uniqueString
#                                     (nothing was created outside the group and left
#                                      behind by a run that only checked the group)
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
#   PURGE             default 1. Delete the resource group, then purge the
#                     soft-deleted Cognitive Services account the group's deletion
#                     left behind. Without the purge the account name stays
#                     reserved and the next provision.sh cannot recreate it
#                     (FlagMustBeSetForRestore). PURGE=0 skips both the purge and
#                     its verification, for an operator restoring instead.
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

# --- purge ------------------------------------------------------------------

# A deleted resource group is not the whole story for a Cognitive Services
# account. Deleting the account leaves it SOFT-DELETED for its retention window
# (retentionDays on the deleted account, 48 h by default), under a name that is
# still reserved. The next provision.sh with the same NAME_PREFIX then fails
# preflight:
#
#   FlagMustBeSetForRestore: An existing resource with ID '…/aif-<token>' has
#   been soft-deleted. To restore the resource, you must specify 'restore' to be
#   'true'… If you don't want to restore existing resource, please purge it first.
#
# So "nothing left behind" has a second half, and this is it. The account record
# survives the group; purge it, or the deployment cannot be created again.
#
# Scope: the deleted accounts whose ARM id still names one of the groups being
# torn down, plus any name in already_gone. It never purges an account that
# belongs to a group this script is not touching.
#
# Failure is not absence here either: a DELETE that returns non-zero aborts, and
# the verification below re-reads the deleted list and reports what is left.
echo "teardown: soft-deleted cognitive accounts"
purge_cognitive() {
  local listing
  listing="$(az rest --method GET \
    --url "https://management.azure.com/subscriptions/$SUB/providers/Microsoft.CognitiveServices/deletedAccounts?api-version=2025-06-01" \
    -o json)" || {
    echo "teardown.sh: listing soft-deleted accounts failed; refusing to report anything." >&2
    exit 1
  }
  printf '%s' "$listing" | python3 -c '
import json, sys
groups = set(sys.argv[1:])
for account in json.load(sys.stdin).get("value") or []:
    account_id = account.get("id") or ""
    name = account.get("name") or ""
    if not name or not account_id.startswith("/subscriptions/"):
        continue
    parts = account_id.split("/")
    # /subscriptions/<sub>/providers/Microsoft.CognitiveServices/locations/<loc>/resourceGroups/<rg>/deletedAccounts/<name>
    if "resourceGroups" not in parts:
        location = None
        group = None
    else:
        i = parts.index("resourceGroups")
        group = parts[i + 1] if len(parts) > i + 1 else None
        location = parts[parts.index("locations") + 1] if "locations" in parts else None
    if group in groups:
        print("\t".join([name, location or "", group]))
' "$@" | while IFS=$'\t' read -r name location group; do
    [ -n "$name" ] || continue
    echo "  $name ($group): purging"
    if ! az rest --method DELETE \
      --url "https://management.azure.com/subscriptions/$SUB/providers/Microsoft.CognitiveServices/locations/$location/resourceGroups/$group/deletedAccounts/$name?api-version=2025-06-01" \
      -o none; then
      echo "teardown.sh: purge of $name failed; the name stays reserved and re-provisioning will fail." >&2
      exit 1
    fi
  done
}

# PURGE=0 skips the purge and the check that follows it, for an operator who
# intends to restore the account rather than recreate it.
if [ "${PURGE:-1}" = "1" ]; then
  if [ -n "${EXTRA_GROUPS:-}" ]; then
    purge_cognitive "$RG" $EXTRA_GROUPS
  else
    purge_cognitive "$RG"
  fi
  # A purge is asynchronous: the record moves to Deleting and disappears. Poll every
  # group this run touched, not just $RG — wait for all of them, because a purge that is
  # still in flight at exit is a teardown that will fail the next provision.sh.
  purge_pending() { # prints the groups that still have a deleted account listed
    local listing g
    listing="$(az rest --method GET \
      --url "https://management.azure.com/subscriptions/$SUB/providers/Microsoft.CognitiveServices/deletedAccounts?api-version=2025-06-01" \
      -o json)" || {
      echo "teardown.sh: re-reading soft-deleted accounts failed; refusing to report anything." >&2
      exit 1
    }
    for g in "$@"; do
      printf '%s' "$listing" | python3 -c '
import json, sys
group = sys.argv[1]
for account in json.load(sys.stdin).get("value") or []:
    if "/resourceGroups/" + group + "/" in (account.get("id") or ""):
        print(group)
        break
' "$g"
    done
  }

  PURGE_GROUPS="$RG ${EXTRA_GROUPS:-}"
  for i in $(seq 1 60); do
    still="$(purge_pending $PURGE_GROUPS)"
    if [ -z "${still//[$'\n']/}" ]; then
      echo "  [${i}] no soft-deleted account remains in: $(echo $PURGE_GROUPS)"
      break
    fi
    echo "  [${i}] still listed: ${still//$'\n'/, }"
    if [ "$i" = "60" ]; then
      echo "teardown.sh: a purge is still in flight after 10 minutes." >&2
      echo "             The name stays reserved until it finishes; this is not a clean teardown." >&2
      exit 2
    fi
    sleep 10
  done
else
  echo "  skipped (PURGE=0): the Foundry account stays soft-deleted and its name stays reserved"
fi

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

# The second half of "nothing left behind": a reserved name is a leftover even
# when the group is gone, because it stops the next provision.sh.
if [ "${PURGE:-1}" = "1" ]; then
  for g in "$RG" ${EXTRA_GROUPS:-}; do
    left="$(az rest --method GET \
      --url "https://management.azure.com/subscriptions/$SUB/providers/Microsoft.CognitiveServices/deletedAccounts?api-version=2025-06-01" \
      --query "value[?contains(id,'/resourceGroups/$g/')].name" -o tsv)" || {
      echo "teardown.sh: re-reading soft-deleted accounts failed; refusing to report anything." >&2
      exit 1
    }
    if [ -n "${left//[$'\n']/}" ]; then
      echo "  $g: soft-deleted cognitive account(s) remain: ${left//$'\n'/, }" >&2
      STATUS=2
    else
      echo "  $g: no soft-deleted cognitive account, name is free for re-provisioning"
    fi
  done
fi

# --- leftovers outside the group --------------------------------------------
#
# Every check above is scoped to the groups this run deleted, which would also pass if a
# resource had been created in a *different* group under one of this deployment's names.
# This one does not assume the group exists: it lists the whole subscription and looks for
# any resource carrying the deployment's own uniqueString.
#
# The token is derived, not guessed: every provisioned name is `<prefix>-<token>` where
# token = uniqueString(subscription, namePrefix, location), so the account name's suffix
# is the token. When .deployment.json is gone there is nothing to derive it from, and the
# check says so instead of passing silently on an assumption.
echo "teardown: leftovers outside the group"
LEFTOVER_TOKEN=""
if [ -f "$RECORD" ]; then
  LEFTOVER_TOKEN="$(python3 - "$RECORD" <<'PY'
import json, sys
try:
    o = json.load(open(sys.argv[1]))
except Exception:
    print(""); raise SystemExit
name = (o.get("foundryAccountName") or {}).get("value") or ""
print(name[4:] if name.startswith("aif-") else "")
PY
)"
fi

if [ -n "$LEFTOVER_TOKEN" ]; then
  # A failed query aborts: an empty list must only ever come from a query that succeeded.
  leftover="$(az resource list --subscription "$SUB" \
    --query "[?contains(name,'$LEFTOVER_TOKEN')].id" -o tsv)" || {
    echo "teardown.sh: 'az resource list' over the subscription failed; refusing to report anything." >&2
    exit 1
  }
  if [ -n "${leftover//[$'\n']/}" ]; then
    echo "  resources carrying '$LEFTOVER_TOKEN' still exist outside the deleted group(s):" >&2
    printf '%s\n' "$leftover" | sed 's/^/    /' >&2
    STATUS=2
  else
    echo "  no resource outside the deleted group(s) carries '$LEFTOVER_TOKEN'"
  fi
else
  echo "  skipped: no .deployment.json to derive the deployment's uniqueString from"
  echo "           (set SUBSCRIPTION_ID and keep .deployment.json for a complete check)"
fi

if [ "$STATUS" -ne 0 ]; then
  echo "teardown.sh: teardown incomplete (see above)." >&2
  exit "$STATUS"
fi

echo
echo "teardown complete. A second run is harmless (the groups are already absent)."
