// The container registry every service image is pulled from, and the AcrPull grants that let the
// container apps pull without a registry password.
//
// One registry, shared by all three resource groups. That is a deliberate choice and it belongs in
// the same paragraph of the demo as the shared Foundry account: the registry is platform
// infrastructure, both teams can pull from it, and neither team owns it. If you need the registry
// to sit inside the team boundary as well, give each team its own — but say out loud that you have
// then left the tier of isolation the demo is about.
targetScope = 'resourceGroup'

@description('Prefix for all resource names, e.g. "enf-demo".')
param namePrefix string

param location string = resourceGroup().location

@description('Principal ids that pull images: the API, worker and both team identities.')
param pullPrincipalIds array

// Registry names are alphanumeric only and globally unique, so the prefix cannot be used as-is.
var registryName = '${replace(namePrefix, '-', '')}acr${uniqueString(resourceGroup().id)}'

var acrPull = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    // Images are pulled with a managed identity, so the admin user is not needed and would be a
    // password sitting in the resource waiting to be used.
    adminUserEnabled: false
  }
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in pullPrincipalIds: {
  name: guid(registry.id, principalId, 'acrpull')
  scope: registry
  properties: {
    roleDefinitionId: acrPull
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

output loginServer string = registry.properties.loginServer
output name string = registry.name
