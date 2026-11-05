# Teardown — captured

`infra/teardown.sh` was the last thing this issue ran. Below: what existed before, the
invocation that deleted it, the repeat that proves a repeat is harmless, a subscription-wide
check that does not assume the resource group — and the three cases review called out, each
**exercised rather than reasoned about**: a second grave in the same group, an operator whose
default subscription has moved, and the subscription id the script prints.

## What existed before the final teardown

The published run and the teardown below are both from the final deployment. Its names came
from a `RUN_STAMP`, so they are `xszmrm5jdx654`-suffixed; the record-less arm is the one
exercised earlier and described under "The moved-default case".

```
$ az group show -n rg-bandd-durable --query '{name:name,location:location,state:properties.provisioningState}' -o json
{
  "location": "eastus2",
  "name": "rg-bandd-durable",
  "state": "Succeeded"
}

$ az resource list -g rg-bandd-durable --query '[].name' -o tsv
crxszmrm5jdx654
log-xszmrm5jdx654
aif-xszmrm5jdx654
aif-xszmrm5jdx654/durableinside
appi-xszmrm5jdx654
Failure Anomalies - appi-xszmrm5jdx654
```

Five objects are the Bicep deployment; the sixth is the alert rule Application Insights creates
for itself. The registered agent (`durable-inside-agent`) is **not** an ARM resource: it is an
Agent Service registration inside the Foundry project, and deleting the group is what removes it.

## The invocation that deleted it

```
$ ./infra/teardown.sh
teardown: subscription from <scenario>/.deployment.json
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
  [7] rg-bandd-durable: still present
  [8] rg-bandd-durable: gone
teardown: verification
  rg-bandd-durable: absent, no resources remain
teardown: nothing named for this deployment remains anywhere in the subscription
  none
teardown: foundry account soft-delete records in rg-bandd-durable
  aif-xszmrm5jdx654 (eastus2): purging [recorded in .deployment.json]
  no soft-delete record remains for rg-bandd-durable

teardown complete. A second run is harmless (the group and the account record are already absent).
EXIT=0
```

## The repeat — nothing left to do, still exit 0

The acceptance test for an idempotent teardown: a second invocation deletes nothing, purges
nothing that exists (the recorded account's purge is a no-op on an already-purged grave, and
the sweep finds none), and exits 0.

```
$ ./infra/teardown.sh
teardown: subscription from <scenario>/.deployment.json
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
  aif-xszmrm5jdx654 (eastus2): purging [recorded in .deployment.json]
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
    --query "[?starts_with(name, 'aif-xszmrm5jdx654') || starts_with(name, 'crxszmrm5jdx654') || \
              starts_with(name, 'appi-xszmrm5jdx654')].id" -o tsv
(no output — 0 lines)

$ az cognitiveservices account list-deleted --query "[].name" -o tsv
(no output — no soft-deleted Foundry account remains anywhere in the subscription, from any
deployment this issue made. Re-checked after the two-grave exercise and the moved-default
exercise had been cleaned up.)
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

## The multi-grave sweep, and the two ways a sweep can lie

An earlier revision purged the **recorded** account only and consulted the group-scoped
listing only when `.deployment.json` was missing. Review called that out, and it is wrong for
a reason this issue's own evidence shows: one teardown can leave more than one grave. Rather
than assert it, the case was constructed — two accounts in one group, group deleted:

```
$ az cognitiveservices account create -n aif-grave-a -g rg-bandd-durable … --kind AIServices
$ az cognitiveservices account create -n aif-grave-b -g rg-bandd-durable … --kind AIServices
$ az group delete -n rg-bandd-durable --yes
$ az cognitiveservices account list-deleted --query "[].{name:name,rg:resourceGroup}" -o tsv
aif-grave-a	None
aif-grave-b	None
```

`rg: None` is the point. The soft-deleted record has **no `resourceGroup` field**; the group
is only inside the ARM `id`:

```
/subscriptions/…/locations/eastus2/resourceGroups/rg-bandd-durable/deletedAccounts/aif-grave-b
```

So a sweep written as `[?resourceGroup=='$RG']` matches nothing, and `az` does not complain —
it exits 0 with an empty list, and the teardown reads that as "no grave". Measured:

**Before** (record present, old query shape) — exits 0 and leaves both:

```
$ ./.old-teardown.sh
teardown: subscription BizSpark (de345105-…)
teardown: resource groups
  rg-bandd-durable: already absent
EXIT=0
$ az cognitiveservices account list-deleted --query '[].name' -o tsv
aif-grave-a
aif-grave-b
```

**After the first fix** (`[?resourceGroup==…]` plus a sweep) — worse in an instructive way:
it purged only `aif-grave-a`, exited 0, and `aif-grave-b` was still reserved. The sweep was
in the code and the filter matched nothing.

**After the second fix** (`[?contains(id, '/resourceGroups/$RG/')]`, always run, every grave
purged, recorded name as a fast path) — both gone:

```
$ ./infra/teardown.sh
teardown: foundry account soft-delete records in rg-bandd-durable
  aif-grave-a (eastus2): purging [recorded in .deployment.json]
  aif-grave-b (eastus2): purging [found by resource group]
  no soft-delete record remains for rg-bandd-durable
EXIT=0
$ az cognitiveservices account list-deleted --query "[?contains(id, '/resourceGroups/rg-bandd-durable/')].name" -o tsv
(no output)
```

The remaining soft-deleted account in the subscription at that point,
`aif-mxv7i27um4pks`, belongs to `rg-bandd-d3` (another band) and was correctly left alone —
a sweep that purged by name prefix would have taken it.

## The moved-default case, exercised rather than reasoned about

The purge calls originally lacked `"${AZ_SUB[@]}"`, which is the pinning this script's header
exists for. The operator-with-a-moved-default case was run, not argued:

```
$ az account set --subscription 5b9bde89-…      # the *wrong* subscription becomes default
$ SUBSCRIPTION_ID=de345105-… WAIT=1 ./.old-teardown.sh
teardown: targeting de345105-…
teardown: subscription BizSpark (de345105-…)
…
teardown: foundry account soft-delete records in rg-bandd-durable
  no .deployment.json record; looking the graves up by resource group
ERROR: (SubscriptionNotFound) The subscription '5b9bde89-…' could not be found.
teardown.sh: the soft-deleted account list failed; refusing to report anything.
OLD_EXIT=1
```

That exit is safe but only by luck: the query failed. The dangerous arm is the record path,
where the recorded name is purged against the *wrong* subscription, finds nothing, and the
script prints a clean teardown. The same invocation with the fix:

```
$ SUBSCRIPTION_ID=de345105-… ./infra/teardown.sh
…
teardown: foundry account soft-delete records in rg-bandd-durable
  aif-grave-test (eastus2): purging [recorded in .deployment.json]
  no soft-delete record remains for rg-bandd-durable
EXIT=0
```

Every `az` call in the purge section now carries the pinned subscription, and the default was
restored to BizSpark afterwards.

## What the script prints, and why it is truncated

`teardown.sh` prints `${SUB:0:8}-…`, not the GUID. Its output is pasted into this file, and
the full id in every block would make the sentence six lines above false. Every `az` call
still uses the untruncated `"$SUB"`; only the echo is short.

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
