// Microsoft Foundry account + project + capability hosts + one model deployment.
//
// Every resource below is copied from the accelerator's infra/modules/ai-foundry.bicep
// at the pinned commit (see upstream/PROVENANCE.md); the comments record which
// piece is load-bearing and why, because the shapes are not obvious from the
// resource types alone.
//
// DEVIATION: the accelerator also creates a project *connection*
// (Microsoft.CognitiveServices/accounts/projects/connections, category
// AppInsights) so the portal Traces tab renders. Here it is omitted: on this
// subscription with api-version 2025-12-01 it fails validation with
// "Required metadata property ResourceId is missing", and it is not needed to
// run or observe the agent - the platform injects
// APPLICATIONINSIGHTS_CONNECTION_STRING itself (see README, "what is not
// deployed").
param name string
@minLength(2)
@maxLength(63)
param projectName string
param location string
param tags object = {}

@description('Model deployment name the agent reads from MODEL_DEPLOYMENT_NAME')
param modelDeploymentName string
@description('Model version (gpt-5.4 2026-03-05 at the pinned commit)')
param modelVersion string
@allowed(['GlobalStandard', 'DataZoneStandard'])
param modelDeploymentSku string
@minValue(1)
@maxValue(250)
param modelCapacityK int

resource account 'Microsoft.CognitiveServices/accounts@2025-12-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'S0'
  }
  kind: 'AIServices'
  properties: {
    // Required to create the project resource below.
    allowProjectManagement: true
    customSubDomainName: name
    // Entra-only, as in the accelerator: dispatch and agent inference both use
    // identities, never keys.
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-12-01' = {
  name: projectName
  parent: account
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

// Account-level capability host: without it the account cannot host agents.
resource accountCapabilityHost 'Microsoft.CognitiveServices/accounts/capabilityHosts@2025-12-01' = {
  name: 'accountcaphost'
  parent: account
  properties: {
    capabilityHostKind: 'Agents'
  }
}

// Project-level capability host: the refreshed preview requires it *in addition*
// to the account one. The accelerator records that `agents.create_version()`
// otherwise returns a generic `(server_error) 500` because the project has no
// agent hosting backend bound to it.
resource projectCapabilityHost 'Microsoft.CognitiveServices/accounts/projects/capabilityHosts@2025-12-01' = {
  name: 'projectcaphost'
  parent: project
  properties: {
    capabilityHostKind: 'Agents'
  }
  dependsOn: [
    accountCapabilityHost
  ]
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-12-01' = {
  name: modelDeploymentName
  parent: account
  sku: {
    name: modelDeploymentSku
    capacity: modelCapacityK
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5.4'
      version: modelVersion
    }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
  dependsOn: [
    accountCapabilityHost
  ]
}

output accountName string = account.name
output accountPrincipalId string = account.identity.principalId
output accountId string = account.id
output projectName string = project.name
output projectId string = project.id
output projectPrincipalId string = project.identity.principalId

// The Agent Service runtime only routes on the services.ai.azure.com subdomain;
// the cognitiveservices.azure.com endpoint returns 404
// "Subdomain does not map to a resource" (accelerator comment, same file).
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${project.name}'
