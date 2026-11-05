$ NAME_PREFIX=bandd-d3b ../band-d-hosted-foundry-agent/infra/teardown.sh
teardown: subscription from /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-161-work/real-world-scenarios/band-d-hosted-foundry-agent/.deployment.json
teardown: subscription BizSpark (de345105-2ffc-4619-a6bc-9c41dec93241)
teardown: resource groups
  rg-bandd-d3b: deleting
teardown: waiting for deletion to finish
  [1] rg-bandd-d3b: still present
  [2] rg-bandd-d3b: still present
  [3] rg-bandd-d3b: still present
  [4] rg-bandd-d3b: still present
  [5] rg-bandd-d3b: still present
  [6] rg-bandd-d3b: still present
  [7] rg-bandd-d3b: gone
teardown: soft-deleted cognitive accounts
  aif-gkr52qggxgbu6 (rg-bandd-d3b): purging
  [1] still listed: rg-bandd-d3b
  [2] still listed: rg-bandd-d3b
  [3] still listed: rg-bandd-d3b
  [4] still listed: rg-bandd-d3b
  [5] no soft-deleted account remains in: rg-bandd-d3b
teardown: verification
  rg-bandd-d3b: absent, no resources remain
  rg-bandd-d3b: no soft-deleted cognitive account, name is free for re-provisioning
teardown: leftovers outside the group
  no resource outside the deleted group(s) carries 'gkr52qggxgbu6'

teardown complete. A second run is harmless (the groups are already absent).
EXIT=0

$ NAME_PREFIX=bandd-d3 ../band-d-hosted-foundry-agent/infra/teardown.sh
teardown: subscription from /Users/core/git/matthewcorven/github-copilot-sdk-durable-worktrees/squad-161-work/real-world-scenarios/band-d-hosted-foundry-agent/.deployment.json
teardown: subscription BizSpark (de345105-2ffc-4619-a6bc-9c41dec93241)
teardown: resource groups
  rg-bandd-d3: already absent
teardown: waiting for deletion to finish
  [1] rg-bandd-d3: gone
teardown: soft-deleted cognitive accounts
  [1] no soft-deleted account remains in: rg-bandd-d3
teardown: verification
  rg-bandd-d3: absent, no resources remain
  rg-bandd-d3: no soft-deleted cognitive account, name is free for re-provisioning
teardown: leftovers outside the group
  no resource outside the deleted group(s) carries 'gkr52qggxgbu6'

teardown complete. A second run is harmless (the groups are already absent).
EXIT=0

## Verification outside the script

The script checks the groups it deleted. These checks assume nothing about them.

```
$ az resource list --query "[?contains(name,'gkr52qggxgbu6')].id" -o tsv
(exit 0)

$ az resource list --query "[?contains(name,'mxv7i27um4pks')].id" -o tsv
(exit 0)

$ az group list --query "[?starts_with(name,`rg-bandd`)].name" -o tsv
rg-bandd-durable
(exit 0)

# rg-bandd-durable is NOT this issue's: it holds aif-xszmrm5jdx654 with a project
# named "durableinside", which is Band D's D2 variant. It is another run's live
# deployment and deleting it would destroy work in flight, so it is reported here and
# left alone. Everything D3 stood up is accounted for by the two prefixes above.

$ az cognitiveservices account list-deleted --query "[].name" -o tsv
(exit 0)
```

## The third check actually fires

A check that has never failed is not evidence. The leftover block was exercised by
pointing D1's `.deployment.json` at a token belonging to a **live resource in another
group** (Band D"s D2 variant, `rg-bandd-durable`) and tearing down a group that holds
nothing. The check found the cross-group resources and failed the run:

```
$ NAME_PREFIX=bandd-nonexistent ./infra/teardown.sh; echo "REAL EXIT=$?"
teardown: leftovers outside the group
  resources carrying 'xszmrm5jdx654' still exist outside the deleted group(s):
    /subscriptions/de345105-2ffc-4619-a6bc-9c41dec93241/resourceGroups/rg-bandd-durable/providers/Microsoft.ContainerRegistry/registries/crxszmrm5jdx654
    /subscriptions/de345105-2ffc-4619-a6bc-9c41dec93241/resourceGroups/rg-bandd-durable/providers/Microsoft.OperationalInsights/workspaces/log-xszmrm5jdx654
    /subscriptions/de345105-2ffc-4619-a6bc-9c41dec93241/resourceGroups/rg-bandd-durable/providers/Microsoft.CognitiveServices/accounts/aif-xszmrm5jdx654
    /subscriptions/de345105-2ffc-4619-a6bc-9c41dec93241/resourceGroups/rg-bandd-durable/providers/Microsoft.CognitiveServices/accounts/aif-xszmrm5jdx654/projects/durableinside
    /subscriptions/de345105-2ffc-4619-a6bc-9c41dec93241/resourceGroups/rg-bandd-durable/providers/Microsoft.Insights/components/appi-xszmrm5jdx654
    /subscriptions/de345105-2ffc-4619-a6bc-9c41dec93241/resourceGroups/rg-bandd-durable/providers/microsoft.alertsmanagement/smartDetectorAlertRules/Failure Anomalies - appi-xszmrm5jdx654
teardown.sh: teardown incomplete (see above).
REAL EXIT=2
```

The exit is **2**, not 0 — captured without a pipe, because `| tail` makes `$?` the
exit of `tail` and would have reported a failing teardown as clean.

`.deployment.json` was restored immediately afterwards; the next section records the
teardowns that actually ran for D3.
