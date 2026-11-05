# Teardown — captured

`infra/teardown.sh` was the last thing this issue ran. Below: what existed before, the
invocation that deleted it, the repeat that proves a repeat is harmless, the repeated
purge, and a subscription-wide check that does not assume the resource group.

## What existed before the final teardown

The **published run** (`evidence/run-20261105T170223Z/`) ran against a deployment whose record
was removed before teardown, on purpose: a teardown must work without it. Its names came from
the group itself:

```
$ az group show -n rg-bandd-durable --query '{name:name,location:location,state:properties.provisioningState}' -o json
{
  "location": "eastus2",
  "name": "rg-bandd-durable",
  "state": "Succeeded"
}

$ az resource list -g rg-bandd-durable --query '[].name' -o tsv
crlrnkp5x77yrzc
aif-lrnkp5x77yrzc
log-lrnkp5x77yrzc
aif-lrnkp5x77yrzc/durableinside
appi-lrnkp5x77yrzc
Failure Anomalies - appi-lrnkp5x77yrzc
```

Five objects are the Bicep deployment; the sixth is the alert rule Application Insights creates
for itself. The registered agent (`durable-inside-agent`) is **not** an ARM resource: it is an
Agent Service registration inside the Foundry project, and deleting the group is what removes it.

## The invocation that deleted it

```
$ ./infra/teardown.sh
teardown: subscription BizSpark (de345105-…)
teardown: resource groups
  rg-bandd-durable: deleting
teardown: waiting for deletion to finish
  [1] rg-bandd-durable: still present
  [2] rg-bandd-durable: still present
  [3] rg-bandd-durable: still present
  [4] rg-bandd-durable: still present
  [5] rg-bandd-durable: still present
  [6] rg-bandd-durable: still present
  [7] rg-bandd-durable: gone
teardown: verification
  rg-bandd-durable: absent, no resources remain
teardown: nothing named for this deployment remains anywhere in the subscription
  none
teardown: foundry account soft-delete record
  no foundryAccountName recorded; nothing to purge

teardown complete. A second run is harmless (the group and the account record are already absent).
EXIT=0
```

The purge step reports `no foundryAccountName recorded` because `.deployment.json` was gone —
so the account was purged explicitly, and the next invocation (below) found no grave left:

```
$ az cognitiveservices account purge -n aif-lrnkp5x77yrzc -g rg-bandd-durable -l eastus2
(purged; no output)
$ az cognitiveservices account list-deleted --query "[].name" -o tsv
(no output)
$ az group exists -n rg-bandd-durable
false
```

That gap is why `teardown.sh` now falls back to the **resource-group-scoped** soft-deleted
listing when the record is missing, instead of skipping the purge.

## The repeat — with the fallback, and nothing left to do

```
$ ./infra/teardown.sh
teardown: subscription BizSpark (de345105-…)
teardown: resource groups
  rg-bandd-durable: already absent
teardown: waiting for deletion to finish
  [1] rg-bandd-durable: gone
teardown: verification
  rg-bandd-durable: absent, no resources remain
teardown: nothing named for this deployment remains anywhere in the subscription
  none
teardown: foundry account soft-delete records in rg-bandd-durable
  no .deployment.json record; looking the graves up by resource group
  none
  no soft-delete record remains for rg-bandd-durable

teardown complete. A second run is harmless (the group and the account record are already absent).
EXIT=0
```

## A check that does not assume the group

The script's verification reads the group it deleted. These do not:

```
$ az group exists -n rg-bandd-durable
false

$ az resource list --subscription de345105-… \
    --query "[?starts_with(name, 'aif-lrnkp5x77yrzc') || starts_with(name, 'crlrnkp5x77yrzc') || \
              starts_with(name, 'appi-lrnkp5x77yrzc')].id" -o tsv
(no output — 0 lines)

$ az cognitiveservices account list-deleted --query "[].name" -o tsv
(no output — no soft-deleted Foundry account remains, from either deployment this issue made)
```

## The soft-delete grave

A Foundry account deleted with its resource group stays **soft-deleted** under its name for the
retention window, and re-provisioning that name fails:

```
ERROR: {"code": "InvalidTemplateDeployment", ...}
Inner Errors:
{"code": "FlagMustBeSetForRestore", "message": "An existing resource with ID
'/subscriptions/…/accounts/aif-qe7dhfdujkpiw' has been soft-deleted. To restore the
resource, you must specify 'restore' to be 'true' in the property. If you don't want to
restore existing resource, please purge it first."}
```

Three things came out of it, all landed rather than worked around by hand:

- `infra/provision.sh` takes `RUN_STAMP`, and `infra/main.bicep` mixes it into the name token, so
  a re-provision after a teardown starts from fresh names instead of colliding with a grave. The
  intermediate deployment used `RUN_STAMP=164749`.
- `infra/teardown.sh` purges the account and verifies that no soft-delete record remains —
  **including when `.deployment.json` is gone**, by listing the graves that belong to the group.
- Both graves this issue created were purged, so the subscription holds no soft-deleted account
  from either deployment.

**Deleting a resource group is not the whole of a Foundry teardown**, and `az resource list`
cannot see the difference. That is a finding worth carrying to the matrix.

## What this teardown does not claim

- It does not delete container images from a local Docker daemon: every build ran remotely
  (`az acr build`), so no image was ever on this machine's daemon.
- It does not remove the **store file** the agent wrote inside its container. That file lives on
  the platform's session volume, which the platform owns and deletes with the project; it was
  never a local artefact, and `evidence/` is where its contents live.
- Nothing in `evidence/` is a credential. The subscription **id is truncated** everywhere it
  appears (`de345105-…`): the script prints it to prove which subscription it targeted, and six
  characters are enough for that, so the full GUID is not committed. There are no keys,
  connection strings, tokens or tenant ids in this file or any other.
