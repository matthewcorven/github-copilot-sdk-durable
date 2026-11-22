// Azure Container Registry — holds the coverage agent image.
// Foundry Agent Service pulls the image when it creates an agent version, so
// the registry must be reachable over its public endpoint (the accelerator's own
// infra makes the same choice). Basic SKU is enough for one image.
param name string
param location string
param tags object = {}

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    // The deployer pushes with its own Entra token (az acr login); the Foundry
    // project identity and account identity pull with AcrPull. No admin user.
    adminUserEnabled: false
  }
}

output name string = registry.name
output loginServer string = registry.properties.loginServer
output id string = registry.id
