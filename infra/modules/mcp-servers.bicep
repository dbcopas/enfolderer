// The MCP servers one team owns, hosted over HTTPS.
//
// They are ASP.NET sites rather than the stdio processes they started as, because the Foundry
// agent data plane will only accept an https:// URL for a tool server. Hosting them in the owning
// team's resource group keeps the blast radius honest: Team B cannot redeploy, reconfigure or read
// the logs of Team A's imaging server, and vice versa.
targetScope = 'resourceGroup'

@description('Prefix for all resource names, e.g. "enf-demo".')
param namePrefix string

@description('Suffix distinguishing this team\'s plan, e.g. cardgeo or cardid.')
param teamName string

param location string = resourceGroup().location

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
One object per server: { name, allowedCallerObjectIds, needsStorage, settings }.
allowedCallerObjectIds is the enforced form of the allowed_callers key in the server\'s YAML: it is
the list of principals the server will answer, and anything else is refused with 403.
''')
param servers array

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${namePrefix}-${teamName}-mcp-plan'
  location: location
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  kind: 'linux'
  properties: { reserved: true }
}

resource sites 'Microsoft.Web/sites@2023-12-01' = [for server in servers: {
  name: '${namePrefix}-${server.name}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: concat(
        [
          {
            name: 'McpServer__TenantId'
            value: tenantId
          }
          {
            name: 'McpServer__Audience'
            value: audience
          }
          // Comma-separated because App Service settings are flat strings; the server splits it.
          {
            name: 'McpServer__AllowedCallerObjectIds'
            value: join(server.allowedCallerObjectIds, ',')
          }
        ],
        // Only the imaging server touches storage. The catalogue servers make outbound HTTP calls
        // and nothing else, so they are given no data-plane configuration at all.
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
  }
}]

output serverUrls array = [for (server, i) in servers: {
  name: server.name
  url: 'https://${sites[i].properties.defaultHostName}/mcp'
}]
