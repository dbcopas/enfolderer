// A user-assigned managed identity.
//
// User-assigned identities are created before the resources that use them, so role assignments can
// be made up front and survive a redeploy or a recreated App Service. That matters for the demo:
// the identity is the security principal the whole story hangs on, and it should not change
// identity every time the hosting is rebuilt.
targetScope = 'resourceGroup'

@description('Name of the managed identity.')
param name string

param location string = resourceGroup().location

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
}

output id string = identity.id
output name string = identity.name
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
