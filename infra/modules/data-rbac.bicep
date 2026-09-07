// Role assignments for the data plane. These *are* the demo: each identity gets the narrowest
// role that lets it do its job, and nothing else.
targetScope = 'resourceGroup'

@description('Name of the storage account holding the scans and crops containers.')
param storageAccountName string

@description('Cosmos DB account name holding the jobs container.')
param cosmosAccountName string

@description('Principal id of the API\'s user-assigned identity (issues upload SAS, reads/writes job state).')
param apiPrincipalId string

@description('Principal id of the worker\'s user-assigned identity (orchestrates the pipeline).')
param workerPrincipalId string

@description('Principal id of Team A\'s user-assigned identity.')
param geometryPrincipalId string

@description('Principal id of Team B\'s user-assigned identity.')
param identificationPrincipalId string

var storageBlobDataReader = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageBlobDelegator = 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'
var storageQueueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var cosmosDataContributorId = '00000000-0000-0000-0000-000000000002'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}

resource scans 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: blobService
  name: 'scans'
}

resource crops 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: blobService
  name: 'crops'
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosAccountName
}

// --- API -------------------------------------------------------------------------------------
// The API mints user-delegation SAS tokens but never reads image content itself, so it gets the
// delegator role at account scope and no blob data role.
resource apiDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, apiPrincipalId, storageBlobDelegator)
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDelegator)
  }
}

resource apiQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, apiPrincipalId, storageQueueDataContributor)
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributor)
  }
}

resource apiCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, apiPrincipalId, 'cosmos-data-contributor')
  properties: {
    principalId: apiPrincipalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorId}'
    scope: cosmos.id
  }
}

// --- Worker ----------------------------------------------------------------------------------
// The worker does the cropping, so it is the only identity with write access to both containers.
resource workerScans 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: scans
  name: guid(scans.id, workerPrincipalId, storageBlobDataReader)
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReader)
  }
}

resource workerCrops 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: crops
  name: guid(crops.id, workerPrincipalId, storageBlobDataContributor)
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
  }
}

resource workerDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, workerPrincipalId, storageBlobDelegator)
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDelegator)
  }
}

resource workerQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, workerPrincipalId, storageQueueDataContributor)
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributor)
  }
}

resource workerCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, workerPrincipalId, 'cosmos-data-contributor')
  properties: {
    principalId: workerPrincipalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorId}'
    scope: cosmos.id
  }
}

// --- Team A (geometry) -----------------------------------------------------------------------
// Read on scans so the boundary agent can see the photo. No write anywhere, and deliberately no
// Cosmos role assignment: Team A cannot see job state, customer notes or identification results.
resource geometryScans 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: scans
  name: guid(scans.id, geometryPrincipalId, storageBlobDataReader)
  properties: {
    principalId: geometryPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReader)
  }
}

// --- Team B (identification) -----------------------------------------------------------------
// Read on crops only. Team B never sees the original photo, only the rectified card faces.
resource identificationCrops 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: crops
  name: guid(crops.id, identificationPrincipalId, storageBlobDataReader)
  properties: {
    principalId: identificationPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReader)
  }
}
