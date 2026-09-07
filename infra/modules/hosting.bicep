// Hosting for the job API and the pipeline worker. Both run with system-assigned identities so
// that no connection string or storage key ever exists in configuration.
targetScope = 'resourceGroup'

param namePrefix string
param location string = resourceGroup().location

@description('Blob service endpoint of the shared storage account.')
param storageAccountUrl string

@description('Queue service endpoint of the shared storage account.')
param queueAccountUrl string

@description('Cosmos DB document endpoint.')
param cosmosEndpoint string

@description('Tenant id used to validate desktop-app tokens.')
param tenantId string

@description('Application (client) id of the API app registration.')
param apiClientId string

@description('Endpoint of Team A\'s geometry project.')
param geometryProjectEndpoint string

@description('Endpoint of Team B\'s identification project.')
param identificationProjectEndpoint string

@description('Resource id of the API\'s user-assigned managed identity.')
param apiIdentityId string

@description('Client id of the API\'s user-assigned managed identity.')
param apiIdentityClientId string

@description('Resource id of the worker\'s user-assigned managed identity.')
param workerIdentityId string

@description('Client id of the worker\'s user-assigned managed identity.')
param workerIdentityClientId string

var sharedSettings = [
  {
    name: 'ScanPlatform__StorageAccountUrl'
    value: storageAccountUrl
  }
  {
    name: 'ScanPlatform__QueueAccountUrl'
    value: queueAccountUrl
  }
  {
    name: 'ScanPlatform__QueueName'
    value: 'scan-jobs'
  }
  {
    name: 'ScanPlatform__CosmosEndpoint'
    value: cosmosEndpoint
  }
  {
    name: 'ScanPlatform__CosmosDatabase'
    value: 'enfolderer'
  }
  {
    name: 'ScanPlatform__CosmosContainer'
    value: 'jobs'
  }
]

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${namePrefix}-plan'
  location: location
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  kind: 'linux'
  properties: { reserved: true }
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: '${namePrefix}-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: concat(sharedSettings, [
        // A site can hold several user-assigned identities, so the credential must be told which
        // one to present. Without this, token acquisition is ambiguous and fails at runtime.
        {
          name: 'ScanPlatform__ManagedIdentityClientId'
          value: apiIdentityClientId
        }
        {
          name: 'AzureAd__TenantId'
          value: tenantId
        }
        {
          name: 'AzureAd__ClientId'
          value: apiClientId
        }
      ])
    }
  }
}

resource worker 'Microsoft.Web/sites@2023-12-01' = {
  name: '${namePrefix}-worker'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      alwaysOn: true
      appSettings: concat(sharedSettings, [
        {
          name: 'ScanPlatform__ManagedIdentityClientId'
          value: workerIdentityClientId
        }
        {
          name: 'ScanPipeline__GeometryProjectEndpoint'
          value: geometryProjectEndpoint
        }
        {
          name: 'ScanPipeline__IdentificationProjectEndpoint'
          value: identificationProjectEndpoint
        }
        {
          name: 'ScanPipeline__BoundaryAgentId'
          value: 'CardBoundaryAgent'
        }
        {
          name: 'ScanPipeline__IdentificationAgentIds__mtg'
          value: 'MtgCardIdAgent'
        }
        {
          name: 'ScanPipeline__IdentificationAgentIds__pokemon'
          value: 'PokemonCardIdAgent'
        }
      ])
    }
  }
}

output apiUrl string = 'https://${api.properties.defaultHostName}'
