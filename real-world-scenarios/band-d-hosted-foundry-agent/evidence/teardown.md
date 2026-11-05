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
