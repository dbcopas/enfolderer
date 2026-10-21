// Opt-in, separately billed knowledge service. Deploy into the Foundry account's resource group.
targetScope = 'resourceGroup'

@description('Prefix that owns this optional scan-knowledge service, e.g. "enf-demo".')
param namePrefix string
param searchName string
@description('Explicit region with agentic retrieval capacity; need not match the app or Foundry region.')
param location string
param foundryAccountName string
param apiPrincipalId string
@description('Human or CI principal that configures the index and imports public card knowledge.')
param setupPrincipalId string
@allowed(['User', 'ServicePrincipal', 'Group'])
param setupPrincipalType string = 'User'

resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  name: searchName
  location: location
  tags: {
    enfoldererPrefix: namePrefix
    enfoldererPurpose: 'scan-knowledge'
  }
  sku: {
    name: 'basic'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'Default'
    disableLocalAuth: true
    publicNetworkAccess: 'enabled'
    semanticSearch: 'free'
  }
}

var readerRole = '1407120a-92aa-4202-b7e9-c0e197c71c8f'
var writerRole = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var serviceRole = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
resource apiRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: search
  name: guid(search.id, apiPrincipalId, readerRole)
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRole)
  }
}
resource setupRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in [writerRole, serviceRole]: {
  scope: search
  name: guid(search.id, setupPrincipalId, role)
  properties: {
    principalId: setupPrincipalId
    principalType: setupPrincipalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
  }
}]
resource selfRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: search
  name: guid(search.id, 'knowledge-self-read', readerRole)
  properties: {
    principalId: search.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRole)
  }
}
module modelAccess 'modules/search-model-access.bicep' = {
  name: '${searchName}-model-access'
  params: {
    foundryAccountName: foundryAccountName
    searchPrincipalId: search.identity.principalId
  }
}
output endpoint string = 'https://${search.name}.search.windows.net'
output searchResourceId string = search.id
