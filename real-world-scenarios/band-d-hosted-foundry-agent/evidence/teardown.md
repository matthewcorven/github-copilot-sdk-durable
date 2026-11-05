# Teardown — captured

`infra/teardown.sh` was the last thing this issue did. Two invocations are below: the
real one, and the repeat that proves it is harmless. A third check, outside the script,
looks for anything left under the deployment's own names.

## First invocation — deletes, waits, verifies

```
$ ./infra/teardown.sh
teardown: subscription BizSpark
teardown: resource groups
  rg-bandd-priorauth: deleting
teardown: waiting for deletion to finish
  [1] rg-bandd-priorauth: Deleting
  [2] rg-bandd-priorauth: Deleting
  [3] rg-bandd-priorauth: Deleting
  [4] rg-bandd-priorauth: Deleting
  [5] rg-bandd-priorauth: Deleting
  [6] rg-bandd-priorauth: Deleting
  [7] rg-bandd-priorauth: absent
teardown: verification
  no resources remain in rg-bandd-priorauth

teardown complete. A second run is harmless (the group is already absent).
```

Exit code 0. About a minute of wall time.

## Second invocation — nothing to do, still exit 0

```
$ ./infra/teardown.sh
teardown: subscription BizSpark
teardown: resource groups
  rg-bandd-priorauth: already absent
teardown: waiting for deletion to finish
  [1] rg-bandd-priorauth: absent
teardown: verification
  no resources remain in rg-bandd-priorauth

teardown complete. A second run is harmless (the group is already absent).
EXIT=0
```

## Verification outside the script

The script checks `az resource list` scoped to the group it deleted, which would also
pass if a resource had been created in a *different* group. These two checks do not
assume the group exists:

```
$ az group list --query "[?starts_with(name,'rg-bandd')].name" -o tsv
                       # no output: no band-d resource group remains

$ az resource list --query "[?contains(name,'uxtw3efmzqdpw')].id" -o tsv
                       # no output: no resource whose name carries this
                       # deployment's uniqueString survives anywhere in the
                       # subscription
```

`uxtw3efmzqdpw` is the `uniqueString(subscription, namePrefix, location)` every resource
name in `infra/main.bicep` is derived from, so a survivor of this deployment would carry
it (the two hosted agent registrations are the exception — they are not ARM resources;
they are removed with the Foundry project, which the group deletion took with it).

## After review: the harness was corrected and re-run

An independent review found the script could report success without verifying
anything. Two conflations were real:

- `left="$(az resource list … || true)" 2>/dev/null` turned **any** query failure
  (expired token, 5xx, bad JMESPath) into empty output, and empty output into
  "no resources remain… exit 0".
- `if ! az group show` treated **any** non-zero as "already absent" — a wrong
  default subscription, a 403 or a network error included. With no subscription
  pinned, an operator whose default subscription had moved would delete nothing
  and be told the teardown was clean.

Both are fixed in `infra/teardown.sh`: existence is `az group exists --name`
(true/false, non-zero only on a failed query), `|| true` and `2>/dev/null` are gone
from the verification path, and a failed call exits 1 with
"refusing to report anything". `provision.sh` now records the subscription in
`.deployment.json`, and every `az` call in teardown passes it explicitly.

### Proof, on the already-torn-down deployment

```
$ az group exists --name rg-bandd-priorauth
false

$ ./infra/teardown.sh
teardown: subscription BizSpark (de345105-2ffc-4619-a6bc-9c41dec93241)
teardown: resource groups
  rg-bandd-priorauth: already absent
teardown: waiting for deletion to finish
  [1] rg-bandd-priorauth: gone
teardown: verification
  rg-bandd-priorauth: absent, no resources remain

teardown complete. A second run is harmless (the groups are already absent).
EXIT=0

$ ./infra/teardown.sh          # second run, byte-identical result
EXIT=0
```

### Control: a failed query must not read as clean

The failure path was exercised with a subscription that cannot be resolved, which is
the same class of failure as an expired token:

```
$ SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000 ./infra/teardown.sh
teardown: targeting 00000000-0000-0000-0000-000000000000 (the current az default is de345105-…)
ERROR: Subscription '00000000-0000-0000-0000-000000000000' not found. …
teardown: resource groups
ERROR: Subscription '00000000-0000-0000-0000-000000000000' not found. …
teardown.sh: 'az group exists --name rg-bandd-priorauth' failed; refusing to report anything.
EXIT=1
```

The old script would have printed `rg-bandd-priorauth: already absent`, then
`no resources remain in rg-bandd-priorauth`, and exited 0. An empty
`az group exists` list is not absence, and the corrected script says so.

The subscription is also read from the record `provision.sh` writes:

```
$ echo '{"subscriptionId":{"type":"String","value":"de345105-…"}}' > .deployment.json
$ ./infra/teardown.sh
teardown: subscription from …/.deployment.json
teardown: subscription BizSpark (de345105-…)
...
EXIT=0
```

Reasoning about the delete path rather than re-provisioning: nothing in it changed.
`az group delete --yes --no-wait` is still issued only after `group_exists` answers
`true`, the wait loop now asks the same boolean every 10 s instead of parsing
`provisioningState`, and a group still present after the loop is reported as
`STILL EXISTS` with its resource list and exit 2 rather than being read as a
completed delete. Re-running the destructive path to demonstrate it would cost a
fresh deployment for behaviour the boolean already pins down.

## What this teardown does *not* claim

- It does not delete the two probe resource groups used while developing the Bicep
  modules (`rg-bandd-priorauth` before the App Insights connection was removed, and
  `rg-bandd-probe2` for the monitoring module). Those were deleted during development,
  before this file was written; `EXTRA_GROUPS` exists in the script for exactly that
  case if another session leaves one behind.
- It does not delete the container images from a **local** Docker daemon. The local
  Compose run in `evidence/local-compose-path.md` was removed on its own side with
  `docker compose down --rmi all --remove-orphans` and a `docker ps -a` /
  `docker images` check; nothing from it remains running on this machine.
