// Shared data plane for the Enfolderer scan pipeline: one storage account (scans + crops), the
// job queue and the Cosmos job store. Deployed into the platform resource group; the two Foundry
// projects live in their own resource groups and are granted only the roles they need.
targetScope = 'resourceGroup'

@description('Base name used to derive resource names.')
param namePrefix string

@description('Location for the data-plane resources.')
param location string = resourceGroup().location

@description('Job document time-to-live in seconds. Demo jobs expire after a week.')
param jobTtlSeconds int = 604800

var storageName = toLower(replace('${namePrefix}stg', '-', ''))
var cosmosName = toLower('${namePrefix}-cosmos')

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    // The desktop app uploads with a user-delegation SAS, so shared keys are switched off:
    // every caller must present an Entra identity.
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource scansContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'scans'
  properties: { publicAccess: 'None' }
}

resource cropsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'crops'
  properties: { publicAccess: 'None' }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource jobQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: 'scan-jobs'
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    // Job state is only ever read through the data-plane RBAC roles below.
    disableLocalAuth: true
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
  }
}

resource cosmosDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: 'enfolderer'
  properties: {
    resource: { id: 'enfolderer' }
    options: { throughput: 400 }
  }
}

resource jobsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDb
  name: 'jobs'
  properties: {
    resource: {
      id: 'jobs'
      partitionKey: {
        paths: [ '/jobId' ]
        kind: 'Hash'
      }
      defaultTtl: jobTtlSeconds
    }
  }
}

output storageAccountId string = storage.id
output storageAccountName string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output queueEndpoint string = storage.properties.primaryEndpoints.queue
output scansContainerId string = scansContainer.id
output cropsContainerId string = cropsContainer.id
output cosmosAccountName string = cosmos.name
output cosmosAccountId string = cosmos.id
output cosmosEndpoint string = cosmos.properties.documentEndpoint
