// Hosting for the job API and the pipeline worker in the shared Container Apps
// environment. Both run with a user-assigned managed identity so that no connection string or
// storage key ever exists in configuration, and so the identities and their role assignments
// survive a redeploy or a recreated app.
targetScope = 'resourceGroup'

param namePrefix string
param location string = resourceGroup().location

@description('Resource id of the shared Container Apps environment.')
param environmentId string

@description('Login server of the container registry, e.g. myacr.azurecr.io.')
param registryLoginServer string

@description('''
Tag of the images to run, e.g. "v1". Leave empty on the very first deployment: the registry has no
images yet, so both apps start on a placeholder and are pointed at the real images once
`az acr build` has run. See docs/azure-setup.md.
''')
param imageTag string = ''

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
param mobileWebClientId string = ''
param scanAllowedUserObjectIds string = ''
param scanDiagnosticUserObjectIds string = ''
param scanAllowanceAdminUserObjectIds string = ''
param apiCustomDomains array = []

@description('Endpoint of the geometry project.')
param geometryProjectEndpoint string

@description('Endpoint of the identification project.')
param identificationProjectEndpoint string

@description('Resource id of the API\'s user-assigned managed identity.')
param apiIdentityId string

@description('Client id of the API\'s user-assigned managed identity.')
param apiIdentityClientId string

@description('Resource id of the worker\'s user-assigned managed identity.')
param workerIdentityId string

@description('Client id of the worker\'s user-assigned managed identity.')
param workerIdentityClientId string

// Named agent references are durable deployment parameters, not classic assistant IDs.
@description('Named Foundry agent for boundary detection.')
param boundaryAgentId string = 'CardBoundaryAgent'

@description('Named Foundry agent for Magic identification.')
param mtgAgentId string = 'MtgCardIdAgent'

// Until the first `az acr build`, there is nothing to pull. The placeholder is Microsoft's own
// sample image; it does nothing useful, and the app is meant to be pointed at a real tag straight
// afterwards. Registry credentials are omitted in that state too, so a deployment cannot fail on
// an AcrPull assignment that has not finished propagating.
var hasImages = !empty(imageTag)
var placeholder = 'mcr.microsoft.com/k8se/quickstart:latest'
var apiImage = hasImages ? '${registryLoginServer}/enfolderer/api:${imageTag}' : placeholder
var workerImage = hasImages ? '${registryLoginServer}/enfolderer/worker:${imageTag}' : placeholder
// Our images serve on 8080 (see src/Dockerfile), but the placeholder listens on 80. Container Apps
// probes the ingress target port before it will call a revision provisioned, so declaring 8080
// while the placeholder is in place fails the whole deployment with an empty
// "Failed to provision revision" — the port has to follow the image.
var targetPort = hasImages ? 8080 : 80

var apiRegistries = hasImages ? [
  {
    server: registryLoginServer
    identity: apiIdentityId
  }
] : []

var workerRegistries = hasImages ? [
  {
    server: registryLoginServer
    identity: workerIdentityId
  }
] : []

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

resource api 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // The one public endpoint in the whole system. Everything else the client might have talked
      // to — storage, Cosmos, the Foundry projects — is reached from in here.
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
        customDomains: apiCustomDomains
      }
      registries: apiRegistries
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(sharedSettings, [
            // An app can hold several user-assigned identities, so the credential must be told
            // which one to present. Without this, token acquisition is ambiguous and fails at
            // runtime.
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
            {
              name: 'MobileWeb__ClientId'
              value: mobileWebClientId
            }
            {
              name: 'MobileWeb__AllowedUserObjectIds'
              value: scanAllowedUserObjectIds
            }
            {
              name: 'Diagnostics__AllowedUserObjectIds'
              value: scanDiagnosticUserObjectIds
            }
            {
              name: 'ScanAllowances__AdminUserObjectIds'
              value: scanAllowanceAdminUserObjectIds
            }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 3
      }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-worker'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // No ingress at all. The worker takes its work from the queue, so it has no reason to be
      // reachable — not publicly, and not from inside the environment either. On App Service it
      // had to serve HTTP or the platform declared it failed to start; that constraint is gone.
      registries: workerRegistries
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: concat(sharedSettings, [
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
              value: boundaryAgentId
            }
            {
              name: 'ScanPipeline__UseResponses'
              value: 'true'
            }
            {
              name: 'ScanPipeline__FoundryApiVersion'
              value: '2025-11-15-preview'
            }
            {
              name: 'ScanPipeline__IdentificationAgentIds__mtg'
              value: mtgAgentId
            }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'scan-queue'
            custom: {
              type: 'azure-queue'
              identity: workerIdentityId
              metadata: {
                accountName: first(split(replace(queueAccountUrl, 'https://', ''), '.'))
                queueName: 'scan-jobs'
                queueLength: '1'
                // Include invisible leased messages so active scans keep the worker running.
                queueLengthStrategy: 'all'
              }
            }
          }
        ]
      }
    }
  }
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output apiAppName string = api.name
output workerAppName string = worker.name
