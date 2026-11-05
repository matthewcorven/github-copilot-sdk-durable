# Teardown — D3 leaves nothing behind

$ NAME_PREFIX=bandd-d3 ../band-d-hosted-foundry-agent/infra/teardown.sh
teardown: subscription from /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-161-work/real-world-scenarios/band-d-hosted-foundry-agent/.deployment.json
teardown: subscription BizSpark (de345105-2ffc-4619-a6bc-9c41dec93241)
teardown: resource groups
  rg-bandd-d3: deleting
teardown: waiting for deletion to finish
  [1] rg-bandd-d3: still present
  [2] rg-bandd-d3: still present
  [3] rg-bandd-d3: still present
  [4] rg-bandd-d3: still present
  [5] rg-bandd-d3: still present
  [6] rg-bandd-d3: still present
  [7] rg-bandd-d3: still present
  [8] rg-bandd-d3: gone
teardown: soft-deleted cognitive accounts
  aif-mxv7i27um4pks (rg-bandd-d3): purging
  [1] rg-bandd-d3: soft-deleted account(s) still listed: aif-mxv7i27um4pks
  [2] rg-bandd-d3: soft-deleted account(s) still listed: aif-mxv7i27um4pks
  [3] rg-bandd-d3: soft-deleted account(s) still listed: aif-mxv7i27um4pks
  [4] rg-bandd-d3: soft-deleted account(s) still listed: aif-mxv7i27um4pks
  [5] rg-bandd-d3: no soft-deleted account remains
teardown: verification
  rg-bandd-d3: absent, no resources remain
  rg-bandd-d3: no soft-deleted cognitive account, name is free for re-provisioning

teardown complete. A second run is harmless (the groups are already absent).
EXIT=0

## Verification outside the script

The script checks the group and the deleted list it just purged. These checks assume neither.

```
$ az group list --query "[?starts_with(name,`rg-bandd`)].name" -o tsv
(no output: no band-d resource group remains)

$ az cognitiveservices account list-deleted --query "[].name" -o tsv
(no output: no soft-deleted cognitive account)

$ az resource list --query "[?contains(name,`mxv7i27um4pks`)].id" -o tsv
(no output: no resource carrying this deployment uniqueString)

$ az cognitiveservices account list --query "[?contains(name,`mxv7i27um4pks`)].name" -o tsv
(no output: the Foundry account is gone)
```

## A second invocation is harmless

```
$ NAME_PREFIX=bandd-d3 ../band-d-hosted-foundry-agent/infra/teardown.sh
teardown: subscription from /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-161-work/real-world-scenarios/band-d-hosted-foundry-agent/.deployment.json
teardown: subscription BizSpark (de345105-2ffc-4619-a6bc-9c41dec93241)
teardown: resource groups
  rg-bandd-d3: already absent
teardown: waiting for deletion to finish
  [1] rg-bandd-d3: gone
teardown: soft-deleted cognitive accounts
  [1] rg-bandd-d3: no soft-deleted account remains
teardown: verification
  rg-bandd-d3: absent, no resources remain
  rg-bandd-d3: no soft-deleted cognitive account, name is free for re-provisioning

teardown complete. A second run is harmless (the groups are already absent).
EXIT=0
```

## What D3 created, and what removed it

| Object | Created by | Removed by |
| --- | --- | --- |
| `d3-coverage-assessor` (prompt agent, version 18) | `deploy-agents.sh` | the resource-group delete: agent registrations live in the Foundry project |
| `d3-decision-letter` (prompt agent, version 2) | `deploy-agents.sh` | the same |
| `d3-probe`, `d3-temp-probe` (probe agents from development) | ad-hoc `curl` while writing this | the same |
| resource group, registry, account, project, model deployment, monitoring | D1 `provision.sh` | D1 `teardown.sh` |

D3 directly provisioned **no** Azure resource. Its two agents existed as versions inside the project D1 created, so the reuse requirement (4) and the leaving-nothing requirement (5) reduce to the same delete.
