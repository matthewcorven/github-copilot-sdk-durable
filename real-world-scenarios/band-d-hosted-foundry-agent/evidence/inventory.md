# What the deployment contained, and what teardown removed

Captured on 2026-11-05, `az resource list` over the resource group, before teardown.

```
$ az account show --query name -o tsv
BizSpark

$ az group show -n rg-bandd-priorauth --query '{name:name,location:location,state:properties.provisioningState}' -o json
{
  "location": "eastus2",
  "name": "rg-bandd-priorauth",
  "state": "Succeeded"
}

$ az resource list -g rg-bandd-priorauth --query '[].{type:type,name:name}' -o table
Name                                   Type
-------------------------------------  ------------------------------------------
cruxtw3efmzqdpw                        Microsoft.ContainerRegistry/registries
log-uxtw3efmzqdpw                      Microsoft.OperationalInsights/workspaces
appi-uxtw3efmzqdpw                     Microsoft.Insights/components
aif-uxtw3efmzqdpw                      Microsoft.CognitiveServices/accounts
aif-uxtw3efmzqdpw/priorauth            Microsoft.CognitiveServices/accounts/projects
Failure Anomalies - appi-uxtw3efmzqdpw  Microsoft.AlertsManagement/actionRules
```

Six listed objects, five of them deployed by `infra/main.bicep`: the registry, the Log Analytics workspace, Application Insights, the Foundry account and the Foundry project. `Failure Anomalies - appi-…` is an alert rule Application Insights creates for itself when the component comes up; it lives in the same resource group and is removed with it.

The two hosted agents (`coverage-assessment-agent`, `synthesis-agent`) are **not** ARM resources in this group: they are Agent Service registrations backed by the platform. Deleting the Foundry project is what removes them, which is why teardown deletes the group rather than individual resources.
