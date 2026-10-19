// Cost-optimized deployment: one ACA environment and Foundry account, no private networking.
// Resource groups and project names identify pipeline components, not separate teams.
targetScope = 'subscription'

@description('Prefix for all resource names, e.g. "enf-demo".')
@minLength(3)
@maxLength(12)
param namePrefix string

param location string = 'swedencentral'

@description('Entra user object ID administering both Foundry projects.')
param ownerObjectId string

@description('Enable Cosmos free tier at account creation. Only one account per subscription may use it.')
param cosmosFreeTier bool = true

@description('Tenant id used to validate desktop-app tokens.')
param tenantId string = subscription().tenantId

@description('Application (client) id of the API app registration.')
param apiClientId string

@description('Single-tenant Microsoft SPA registration for the mobile website. Empty disables browser sign-in.')
param mobileWebClientId string = ''

@description('Comma-separated tenant user/guest object IDs approved for the private scan pilot. Applies to desktop and web API access.')
param scanAllowedUserObjectIds string = ''

@description('Comma-separated operator object IDs allowed read-only diagnostics across approved pilot scans. Empty disables diagnostics.')
param scanDiagnosticUserObjectIds string = ''
@description('Explicit tenant user IDs allowed to grant and inspect scan credits. Empty disables allowance administration.')
param scanAllowanceAdminUserObjectIds string = ''
@description('API custom-domain bindings. deploy-images.ps1 preserves current bindings; use destination certificate IDs when moving environments.')
param apiCustomDomains array = []

@description('MCP resource app client ID (v2 JWT aud). Agent-identity connections request tokens for api://<this id>.')
@minLength(1)
param mcpAudience string

@description('''
Tag of the container images to run, e.g. "v1". Leave empty for the very first deployment: the
registry it names does not exist yet, so every app starts on a placeholder image. After that, use
`./scripts/deploy-images.ps1` for every code change: it builds a fresh tag and redeploys this
template with it. `az containerapp update --image` is not enough on its own, because the template
also sets each app's ingress target port — 80 on the placeholder, 8080 on ours — and the registry
configuration that lets it pull as its own managed identity.
''')
param imageTag string = ''

@description('Named Foundry agent for boundary detection, not a classic asst_ id.')
param boundaryAgentId string = 'CardBoundaryAgent'

@description('Named Foundry agent for Magic identification.')
param mtgAgentId string = 'MtgCardIdAgent'

@description('Distinct Entra agent identity principal of the boundary agent. Empty only during placeholder bootstrap.')
param boundaryAgentPrincipalId string = ''

@description('Distinct Entra agent identity principal of the Magic agent.')
param mtgAgentPrincipalId string = ''

resource platformRg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: '${namePrefix}-platform'
  location: location
}

resource geometryRg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: '${namePrefix}-cardgeo'
  location: location
}

resource identificationRg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: '${namePrefix}-cardid'
  location: location
}

// Keep separate workload identities and least-privilege data roles, without team ownership.
module apiIdentity 'modules/identity.bicep' = {
  name: 'api-identity'
  scope: platformRg
  params: {
    name: '${namePrefix}-api-id'
    location: location
  }
}

module workerIdentity 'modules/identity.bicep' = {
  name: 'worker-identity'
  scope: platformRg
  params: {
    name: '${namePrefix}-worker-id'
    location: location
  }
}

module geometryIdentity 'modules/identity.bicep' = {
  name: 'cardgeo-identity'
  scope: geometryRg
  params: {
    name: '${namePrefix}-cardgeo-id'
    location: location
  }
}

module identificationIdentity 'modules/identity.bicep' = {
  name: 'cardid-identity'
  scope: identificationRg
  params: {
    name: '${namePrefix}-cardid-id'
    location: location
  }
}

module data 'modules/data.bicep' = {
  name: 'data'
  scope: platformRg
  params: {
    namePrefix: namePrefix
    location: location
    enableFreeTier: cosmosFreeTier
  }
}

var sharedAccountName = '${namePrefix}-ai'
var geometryAccountName = sharedAccountName
var identificationAccountName = sharedAccountName
var geometryProjectRg = platformRg.name
var identificationProjectRg = platformRg.name

module sharedAccount 'modules/foundry-account.bicep' = {
  name: 'foundry-account'
  scope: platformRg
  params: {
    accountName: sharedAccountName
    teamIdentityIds: [
      geometryIdentity.outputs.id
      identificationIdentity.outputs.id
    ]
    location: location
  }
}

module geometryProject 'modules/foundry-project.bicep' = {
  name: 'cardgeo'
  scope: resourceGroup(geometryProjectRg)
  params: {
    accountName: geometryAccountName
    projectName: 'cardgeo'
    ownerObjectId: ownerObjectId
    teamIdentityIds: [ geometryIdentity.outputs.id ]
    location: location
  }
  dependsOn: [
    sharedAccount
  ]
}

module identificationProject 'modules/foundry-project.bicep' = {
  name: 'cardid'
  scope: resourceGroup(identificationProjectRg)
  params: {
    accountName: identificationAccountName
    projectName: 'cardid'
    ownerObjectId: ownerObjectId
    teamIdentityIds: [ identificationIdentity.outputs.id ]
    location: location
  }
  dependsOn: [
    sharedAccount
  ]
}

// Basic registry shared by all four services; pulls use managed identities.
module registry 'modules/registry.bicep' = {
  name: 'registry'
  scope: platformRg
  params: {
    namePrefix: namePrefix
    location: location
    pullPrincipalIds: [
      apiIdentity.outputs.principalId
      workerIdentity.outputs.principalId
      geometryIdentity.outputs.principalId
      identificationIdentity.outputs.principalId
    ]
  }
}

// All four apps share this environment and log workspace, including apps in other resource groups.
module platformEnv 'modules/container-env.bicep' = {
  name: 'platform-env'
  scope: platformRg
  params: {
    name: '${namePrefix}-platform-env'
    location: location
  }
}

module hosting 'modules/hosting.bicep' = {
  name: 'hosting'
  scope: platformRg
  params: {
    namePrefix: namePrefix
    location: location
    environmentId: platformEnv.outputs.id
    registryLoginServer: registry.outputs.loginServer
    imageTag: imageTag
    storageAccountUrl: data.outputs.blobEndpoint
    queueAccountUrl: data.outputs.queueEndpoint
    cosmosEndpoint: data.outputs.cosmosEndpoint
    tenantId: tenantId
    apiClientId: apiClientId
    mobileWebClientId: mobileWebClientId
    scanAllowedUserObjectIds: scanAllowedUserObjectIds
    scanDiagnosticUserObjectIds: scanDiagnosticUserObjectIds
    scanAllowanceAdminUserObjectIds: scanAllowanceAdminUserObjectIds
    apiCustomDomains: apiCustomDomains
    apiIdentityId: apiIdentity.outputs.id
    apiIdentityClientId: apiIdentity.outputs.clientId
    workerIdentityId: workerIdentity.outputs.id
    workerIdentityClientId: workerIdentity.outputs.clientId
    geometryProjectEndpoint: geometryProject.outputs.projectEndpoint
    identificationProjectEndpoint: identificationProject.outputs.projectEndpoint
    boundaryAgentId: boundaryAgentId
    mtgAgentId: mtgAgentId
  }
}

// Imaging retains read-only scan access and exact-agent caller authorization.
module geometryMcp 'modules/mcp-servers.bicep' = {
  name: 'cardgeo-mcp'
  scope: geometryRg
  params: {
    namePrefix: namePrefix
    location: location
    environmentId: platformEnv.outputs.id
    registryLoginServer: registry.outputs.loginServer
    imageTag: imageTag
    identityId: geometryIdentity.outputs.id
    identityClientId: geometryIdentity.outputs.clientId
    tenantId: tenantId
    audience: mcpAudience
    storageAccountUrl: data.outputs.blobEndpoint
    servers: [
      {
        name: 'mcp-imaging'
        image: 'mcp-imaging'
        needsStorage: true
        // Neither the worker nor either project's managed identity can call this endpoint.
        allowedCallerObjectIds: empty(boundaryAgentPrincipalId) ? [] : [boundaryAgentPrincipalId]
      }
    ]
  }
}

// Catalogue servers retain their exact-agent allowlists.
module identificationMcp 'modules/mcp-servers.bicep' = {
  name: 'cardid-mcp'
  scope: identificationRg
  params: {
    namePrefix: namePrefix
    location: location
    environmentId: platformEnv.outputs.id
    registryLoginServer: registry.outputs.loginServer
    imageTag: imageTag
    identityId: identificationIdentity.outputs.id
    identityClientId: identificationIdentity.outputs.clientId
    tenantId: tenantId
    audience: mcpAudience
    servers: [
      {
        name: 'mcp-cardcatalog-mtg'
        image: 'mcp-cardcatalog-mtg'
        needsStorage: false
        allowedCallerObjectIds: empty(mtgAgentPrincipalId) ? [] : [mtgAgentPrincipalId]
      }
    ]
  }
}

module dataRbac 'modules/data-rbac.bicep' = {
  name: 'data-rbac'
  scope: platformRg
  params: {
    storageAccountName: data.outputs.storageAccountName
    cosmosAccountName: data.outputs.cosmosAccountName
    apiPrincipalId: apiIdentity.outputs.principalId
    workerPrincipalId: workerIdentity.outputs.principalId
    geometryPrincipalId: geometryIdentity.outputs.principalId
  }
}

// The worker can invoke both projects, but cannot author agents.
module identificationAccess 'modules/foundry-invoke-access.bicep' = {
  name: 'cardid-invoke-access'
  scope: resourceGroup(identificationProjectRg)
  params: {
    accountName: identificationAccountName
    projectName: identificationProject.outputs.projectName
    principalId: workerIdentity.outputs.principalId
  }
}

module crossProjectAccess 'modules/foundry-invoke-access.bicep' = {
  name: 'cross-project-access'
  scope: resourceGroup(geometryProjectRg)
  params: {
    accountName: geometryAccountName
    projectName: geometryProject.outputs.projectName
    principalId: workerIdentity.outputs.principalId
  }
}

output apiUrl string = hosting.outputs.apiUrl
output registryName string = registry.outputs.name
output registryLoginServer string = registry.outputs.loginServer
output geometryProjectEndpoint string = geometryProject.outputs.projectEndpoint
output identificationProjectEndpoint string = identificationProject.outputs.projectEndpoint
output cosmosEndpoint string = data.outputs.cosmosEndpoint
output blobEndpoint string = data.outputs.blobEndpoint
output apiIdentityClientId string = apiIdentity.outputs.clientId
output workerIdentityClientId string = workerIdentity.outputs.clientId
output geometryIdentityClientId string = geometryIdentity.outputs.clientId
output identificationIdentityClientId string = identificationIdentity.outputs.clientId
output geometryAccountName string = geometryAccountName
output identificationAccountName string = identificationAccountName
output geometryMcpServerUrls array = geometryMcp.outputs.serverUrls
output identificationMcpServerUrls array = identificationMcp.outputs.serverUrls
