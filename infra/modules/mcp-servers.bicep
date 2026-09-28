// The MCP servers one team owns, hosted over HTTPS as container apps.
//
// They are ASP.NET sites rather than the stdio processes they started as, because the Foundry
// agent data plane will only accept an https:// URL for a tool server. Hosting them in the owning
// team's environment, in the owning team's resource group, keeps the blast radius honest: Team B
// cannot redeploy, reconfigure or read the logs of Team A's imaging server, and vice versa.
targetScope = 'resourceGroup'

@description('Prefix for all resource names, e.g. "enf-demo".')
param namePrefix string

param location string = resourceGroup().location

@description('Resource id of this team\'s Container Apps environment.')
param environmentId string

@description('Login server of the container registry, e.g. myacr.azurecr.io.')
param registryLoginServer string

@description('Tag of the images to run. Empty means "no images built yet" — see hosting.bicep.')
param imageTag string = ''

@description('Resource id of the identity the servers run as.')
param identityId string

@description('Client id of that identity, so the credential knows which one to present.')
param identityClientId string

@description('Tenant whose tokens are accepted. Leave empty to run the servers unauthenticated.')
param tenantId string = ''

@description('Audience the servers require in an incoming token, e.g. api://enf-demo-mcp.')
param audience string = ''

@description('Blob service endpoint, for servers that read the uploaded scan.')
param storageAccountUrl string = ''

@description('''
One object per server: { name, image, allowedCallerObjectIds, needsStorage, settings }.
`image` is the repository under enfolderer/ in the registry, e.g. "mcp-imaging".
allowedCallerObjectIds is the enforced form of the allowed_callers key in the server\'s YAML: it is
the list of principals the server will answer, and anything else is refused with 403.
''')
param servers array

var hasImages = !empty(imageTag)
var placeholder = 'mcr.microsoft.com/k8se/quickstart:latest'
// Our images serve on 8080 (see src/Dockerfile), but the placeholder listens on 80. Container Apps
// probes the ingress target port before it will call a revision provisioned, so declaring 8080
// while the placeholder is in place fails the whole deployment with an empty
// "Failed to provision revision" — the port has to follow the image.
var targetPort = hasImages ? 8080 : 80

var registries = hasImages ? [
  {
    server: registryLoginServer
    identity: identityId
  }
] : []

resource apps 'Microsoft.App/containerApps@2024-03-01' = [for server in servers: {
  name: '${namePrefix}-${server.name}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // Public, because Foundry calls tool servers from its own service and not from this VNet.
      // That is exactly why these servers check the caller's object id themselves: reachable by
      // anyone is not the same as callable by anyone, and the allow-list below is what makes the
      // difference.
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
      }
      registries: registries
    }
    template: {
      containers: [
        {
          name: server.name
          image: hasImages ? '${registryLoginServer}/enfolderer/${server.image}:${imageTag}' : placeholder
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat(
            [
              {
                name: 'McpServer__TenantId'
                value: tenantId
              }
              {
                name: 'McpServer__Audience'
                value: audience
              }
              // Comma-separated because environment variables are flat strings; the server splits
              // it.
              {
                name: 'McpServer__AllowedCallerObjectIds'
                value: join(server.allowedCallerObjectIds, ',')
              }
            ],
            // Only the imaging server touches storage. The catalogue servers make outbound HTTP
            // calls and nothing else, so they are given no data-plane configuration at all.
            server.needsStorage ? [
              {
                name: 'ScanPlatform__StorageAccountUrl'
                value: storageAccountUrl
              }
              {
                name: 'ScanPlatform__ManagedIdentityClientId'
                value: identityClientId
              }
            ] : [],
            server.?settings ?? [])
        }
      ]
      scale: {
        // Not zero. An agent calling a cold tool server waits for the image to be pulled and the
        // runtime to start, and Foundry gives up on a tool long before that finishes.
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}]

output serverUrls array = [for (server, i) in servers: {
  name: server.name
  url: 'https://${apps[i].properties.configuration.ingress.fqdn}/mcp'
}]

output appNames array = [for server in servers: '${namePrefix}-${server.name}']
