# Teardown — captured

`infra/teardown.sh` was the last thing this issue ran. Below: what existed before, the
invocation that deleted it, the repeat that proves a repeat is harmless, the repeated
purge, and a subscription-wide check that does not assume the resource group.

## What existed before teardown

The deployment the final run used was created with `RUN_STAMP` (see `infra/provision.sh`),
so its names are `aif-26jfhem33f4eg` / `cr26jfhem33f4eg`:

```
$ az group show -n rg-bandd-durable --query '{name:name,location:location,state:properties.provisioningState}' -o json
{ "location": "eastus2", "name": "rg-bandd-durable", "state": "Succeeded" }

$ az resource list -g rg-bandd-durable --query '[].{type:type,name:name}' -o table
Name                                Type
----------------------------------  ------------------------------------------
cr26jfhem33f4eg                     Microsoft.ContainerRegistry/registries
log-v34bjkd3wnnjk                   Microsoft.OperationalInsights/workspaces
appi-v34bjkd3wnnjk                  Microsoft.Insights/components
aif-26jfhem33f4eg                   Microsoft.CognitiveServices/accounts
aif-26jfhem33f4eg/durableinside     Microsoft.CognitiveServices/accounts/projects
Failure Anomalies - appi-v34bjkd3wnnjk  Microsoft.AlertsManagement/actionRules
```

Five objects are the Bicep deployment; the sixth is the alert rule Application Insights
creates for itself. The registered agent (`durable-inside-agent`) is **not** an ARM
resource: it is an Agent Service registration inside the Foundry project, and deleting
the group is what removes it.

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
  [7] rg-bandd-durable: gone
teardown: verification
  rg-bandd-durable: absent, no resources remain
teardown: nothing named for this deployment remains anywhere in the subscription
  none

teardown complete. A second run is harmless (the group is already absent).
EXIT=0
```

## The repeat, with the purge step

The repeat is the acceptance test for the script: a second invocation, after everything is
gone, must delete nothing, purge nothing that exists, and still exit 0.

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
teardown: foundry account soft-delete record
  aif-26jfhem33f4eg (eastus2): purging
  aif-26jfhem33f4eg: purged, no soft-delete record remains

teardown complete. A second run is harmless (the group and the account record are already absent).
EXIT=0
```

## A check that does not assume the group

The script's verification reads the group it deleted. This one does not: it asks the whole
subscription by the names and tags both deployments used.

```
$ az group exists -n rg-bandd-durable
false

$ az resource list --subscription de345105-… \
    --query "[?contains(tags.\"real-world-scenario\", 'band-d-durable-inside')].id" -o tsv
(no output)

$ az resource list --subscription de345105-… \
    --query "[?starts_with(name, 'aif-26jfhem33f4eg') || starts_with(name, 'cr26jfhem33f4eg') || \
              starts_with(name, 'appi-v34bjkd3wnnjk')].id" -o tsv
(no output — 0 lines)

$ az cognitiveservices account list-deleted --query "[].name" -o tsv
(no output — no soft-deleted Foundry account is left either)
```

## The soft-delete grave, and the second teardown

A Foundry account deleted with its resource group stays **soft-deleted** under its name
for the retention window, and re-provisioning that name then fails:

```
ERROR: {"code": "InvalidTemplateDeployment", ...}
Inner Errors:
{"code": "FlagMustBeSetForRestore", "message": "An existing resource with ID
'/subscriptions/…/accounts/aif-qe7dhfdujkpiw' has been soft-deleted. To restore the
resource, you must specify 'restore' to be 'true' in the property. If you don't want to
restore existing resource, please purge it first."}
```

Two things came out of that, both landed rather than worked around by hand:

- `infra/provision.sh` takes `RUN_STAMP`, and `infra/main.bicep` mixes it into the name
  token, so a re-provision after a teardown starts from fresh names instead of colliding
  with a grave. The final run used `RUN_STAMP=164749`, which is why its names differ from
  the earlier run's.
- The graves themselves were purged (`az cognitiveservices account purge`), so the
  subscription holds no soft-deleted account from this issue either. Deleting a resource
  group is **not** the whole of a teardown for a Foundry account, and that is a finding
  worth carrying to the matrix.

## What this teardown does not claim

- It does not delete container images from a local Docker daemon: every build ran
  remotely (`az acr build`), so no image was ever on this machine's daemon.
- It does not remove the **store file** the agent wrote inside its container. That file
  lives on the platform's session volume, which the platform owns and deletes with the
  project; it was never a local artefact, and `evidence/` is where its contents live.
- Nothing in `evidence/` is a credential. The subscription **id is truncated** everywhere
  it appears (`de345105-…`): the script prints it to prove which subscription it targeted,
  and six characters are enough for that, so the full GUID is not committed. There are no
  keys, connection strings, tokens or tenant ids in this file or any other.

