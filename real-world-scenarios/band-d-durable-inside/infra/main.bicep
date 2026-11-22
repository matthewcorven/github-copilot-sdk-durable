// ---------------------------------------------------------------------------
// Band D (D2) — the SAME hosted deployment as D1, and one hosted agent whose
// working state lives in the Durable layer inside the container.
//
// D1's infrastructure is reused unchanged, deliberately: this band is not about
// the platform. The account, project, capability hosts, registry, monitoring and
// the one gpt-5.4 deployment are the D1 shapes, copied here so the two scenarios
// can be diffed by path. What D2 adds is inside the image (see agent/), and one
// thing outside it: the store has to be on a volume the platform keeps across a
// session stop, so the agent records where that volume is mounted as evidence
// rather than assuming a path.
//
//   rg            resource group (all teardown is `az group delete`)
//   cr            registry holding the agent image (Basic; Foundry pulls it)
//   log / appi    the only place the agent's stdout is visible
//   aif           Foundry account + project + both capability hosts + gpt-5.4
//
// targetScope is the subscription, the resource group is the teardown unit, and
// provision.sh records the subscription it deployed to so teardown cannot act on
// a different one. That discipline is D1's and is not altered here.
// ---------------------------------------------------------------------------

targetScope = 'subscription'

@minLength(1)
@maxLength(40)
@description('Short name used to derive every resource name. Lowercase letters, digits and hyphens.')
param namePrefix string = 'bandd-durable'

@description('Region. Hosted agents are region-limited; East US 2 is on the published list and carries gpt-5.4 GlobalStandard.')
param location string = 'eastus2'

@description('Model deployment name used by the agent (`MODEL_DEPLOYMENT_NAME`).')
param modelDeploymentName string = 'gpt-5.4'

@description('Model version for gpt-5.4.')
param modelVersion string = '2026-03-05'

@description('Deployment SKU. GlobalStandard is the D1 baseline and the East US 2 default.')
@allowed(['GlobalStandard', 'DataZoneStandard'])
param modelDeploymentSku string = 'GlobalStandard'

@description('Capacity in thousands of tokens per minute. Held at the D1 baseline of 50: this band adds no model traffic that 50k TPM cannot serve.')
@minValue(1)
@maxValue(250)
param modelCapacityK int = 50

// A Foundry resource group delete leaves the account SOFT-DELETED for the retention
// window, and re-provisioning the same name then fails with `FlagMustBeSetForRestore`:
// either the account is purged first, or the deployment name has to change. The token
// therefore mixes in a `runStamp` the operator supplies, so a re-run of provision.sh after
// a teardown starts from fresh names instead of colliding with a grave. Leave it empty and
// the names are stable, which is what an in-place re-deploy of a live group needs.
@description('Names the resource-set generation. Set it to a fresh value after a teardown; empty keeps the names stable.')
param runStamp string = ''

var token = toLower(uniqueString(subscription().id, namePrefix, location, runStamp))

var tags = {
  'real-world-scenario': 'band-d-durable-inside'
  issue: '160'
  baseline: 'band-d-hosted-foundry-agent'
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
    projectName: 'durableinside'
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
output appInsightsName string = monitoring.outputs.appInsightsName
output modelDeploymentName string = modelDeploymentName