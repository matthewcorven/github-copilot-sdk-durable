// ---------------------------------------------------------------------------
// Band D (D1) — MINIMAL hosted deployment of the Prior-Authorization coverage
// agent, provisioned with plain Bicep instead of `azd up`.
//
// The accelerator's own infra/main.bicep is a full application stack: Foundry
// account + project, ACR, Log Analytics, App Insights, Container Apps
// environment, backend + frontend Container Apps and the role assignments for
// all of them. D1 proves ONE prior-authorization run, so this template keeps
// only what the single hosted agent actually needs:
//
//   rg            resource group (all teardown is `az group delete`)
//   cr            registry holding the agent image (Basic; Foundry pulls it)
//   log           Log Analytics (required by Application Insights)
//   appi          Application Insights (the only place the agent's stdout and
//                 OTel spans are visible; `azd ai agent monitor` equivalent)
//   aif           Foundry account (AIServices) + project + account capability
//                 host + project capability host (both required for hosted
//                 agents) + one gpt-5.4 deployment
//
// Deliberately NOT provisioned: Container Apps environment, backend, frontend,
// frontend images, Content Safety, Cosmos DB, Key Vault. The agent is invoked
// directly through its Responses endpoint, which is how the accelerator's
// backend reaches it in Foundry mode (`hosted_agents.py`).
//
// Roles: the deploying identity needs Contributor + User Access Administrator on
// the subscription (the accelerator's documented requirement). `deploy.sh`
// assigns the data-plane roles the hosted agent needs, because those depend on
// the deployer's own principal id.
// ---------------------------------------------------------------------------

targetScope = 'subscription'

@minLength(1)
@maxLength(40)
@description('Short name used to derive every resource name. Lowercase letters, digits and hyphens.')
param namePrefix string = 'bandd-priorauth'

@description('Region. Hosted agents are region-limited; East US 2 is on the published list and also carries the gpt-5.4 GlobalStandard deployment.')
param location string = 'eastus2'

@description('Model deployment name used by the agent (`MODEL_DEPLOYMENT_NAME`). Matches agents/coverage/agent.yaml and the accelerator README.')
param modelDeploymentName string = 'gpt-5.4'

@description('Model version for gpt-5.4.')
param modelVersion string = '2026-03-05'

@description('Deployment SKU. GlobalStandard is what the accelerator README documents for Sweden Central and the default for East US 2.')
@allowed(['GlobalStandard', 'DataZoneStandard'])
param modelDeploymentSku string = 'GlobalStandard'

@description('Capacity in thousands of tokens per minute.')
@minValue(1)
@maxValue(250)
param modelCapacityK int = 50

var token = toLower(uniqueString(subscription().id, namePrefix, location))

var tags = {
  'real-world-scenario': 'band-d-hosted-foundry-agent'
  issue: '159'
  'solution-accelerator': 'prior-auth-maf'
}

// ── Resource group — the teardown unit ──────────────────────────────────────

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${namePrefix}'
  location: location
  tags: tags
}

// ── Container registry ──────────────────────────────────────────────────────

module registry 'modules/registry.bicep' = {
  name: 'registry'
  scope: rg
  params: {
    name: 'cr${token}'
    location: location
    tags: tags
  }
}

// ── Monitoring — the only view of a hosted agent's stdout ───────────────────

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    logAnalyticsName: 'log-${token}'
    appInsightsName: 'appi-${token}'
    location: location
    tags: tags
  }
}

// ── Foundry account + project ───────────────────────────────────────────────

module foundry 'modules/foundry.bicep' = {
  name: 'foundry'
  scope: rg
  params: {
    name: 'aif-${token}'
    projectName: 'priorauth'
    location: location
    tags: tags
    modelDeploymentName: modelDeploymentName
    modelVersion: modelVersion
    modelDeploymentSku: modelDeploymentSku
    modelCapacityK: modelCapacityK
  }
}

// ── Outputs the scripts consume ─────────────────────────────────────────────

output resourceGroupName string = rg.name
output registryName string = registry.outputs.name
output registryLoginServer string = registry.outputs.loginServer
output registryId string = registry.outputs.id
output foundryAccountName string = foundry.outputs.accountName
output foundryProjectName string = foundry.outputs.projectName
output projectEndpoint string = foundry.outputs.projectEndpoint
output foundryProjectId string = foundry.outputs.projectId
output appInsightsName string = monitoring.outputs.appInsightsName
output modelDeploymentName string = modelDeploymentName
