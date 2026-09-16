// Enfolderer AI scan pipeline — full demo environment.
//
// Three resource groups on purpose:
//   <prefix>-platform  shared data plane + API/worker hosting (platform team)
//   <prefix>-cardgeo   Team A's identity and MCP server (geometry)
//   <prefix>-cardid    Team B's identity and MCP servers (identification)
//
// There are two isolation tiers here, and the demo is about telling them apart:
//
//   Project  isolates agents, connections and authoring. Each team's group holds Azure AI Project
//            Manager over its own project only, so Team B cannot edit Team A's boundary agent even
//            when both projects sit in the same account. The only path from cardid to cardgeo is
//            the invoke-only assignment in modules/cross-project-access.bicep.
//   Account  isolates model deployments and their quota, local-auth and networking settings, and
//            the blast radius of a mistake. Projects share all of it.
//
// singleAccount picks which tier you are demonstrating.
targetScope = 'subscription'

@description('Prefix for all resource names, e.g. "enf-demo".')
@minLength(3)
@maxLength(12)
param namePrefix string

param location string = deployment().location

@description('Entra group object id owning Team A\'s geometry project.')
param teamAGroupObjectId string

@description('Entra group object id owning Team B\'s identification project.')
param teamBGroupObjectId string

@description('Tenant id used to validate desktop-app tokens.')
param tenantId string = subscription().tenantId

@description('Application (client) id of the API app registration.')
param apiClientId string

@description('Set to false to run the "revoke Team B\'s access to Team A\'s agent" demo scenario.')
param grantIdentificationAccessToGeometry bool = true

@description('One Foundry account holding both projects (true, the default), or one account per team (false). True is the layout this demo is about: the boundary between the teams is then the Foundry project boundary, enforced by project-scoped role assignments. False additionally separates quota, model deployments and account settings, but the boundary becomes plain Azure RBAC between two unrelated resources — stronger isolation, weaker demonstration of Foundry itself.')
param singleAccount bool = true

@description('Audience the hosted MCP servers require in an incoming token, e.g. api://enf-demo-mcp. Leave empty to deploy them unauthenticated, which is only acceptable while you are still wiring the demo up: a public MCP endpoint lets any caller bypass the project boundaries the demo exists to show.')
param mcpAudience string = ''

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

// One user-assigned managed identity per role, each created in the resource group of the team that
// owns it. Creating them before everything else means the RBAC below is granted to principals that
// survive redeploys, and Team A's identity is a resource Team B cannot touch.
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
  }
}

// Account topology. A project lives in the same resource group as its account, so singleAccount
// decides both which account each project is created under and where it lands.
var sharedAccountName = '${namePrefix}-ai'
var geometryAccountName = singleAccount ? sharedAccountName : '${namePrefix}-cardgeo-ai'
var identificationAccountName = singleAccount ? sharedAccountName : '${namePrefix}-cardid-ai'
var geometryProjectRg = singleAccount ? platformRg.name : geometryRg.name
var identificationProjectRg = singleAccount ? platformRg.name : identificationRg.name

// One account for both teams: the platform team owns the account, its model deployments and its
// quota, and each team owns only its project inside it.
module sharedAccount 'modules/foundry-account.bicep' = if (singleAccount) {
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

// One account per team: each team owns its own quota and account settings as well as its project.
module geometryAccount 'modules/foundry-account.bicep' = if (!singleAccount) {
  name: 'foundry-account-cardgeo'
  scope: geometryRg
  params: {
    accountName: geometryAccountName
    teamIdentityIds: [ geometryIdentity.outputs.id ]
    location: location
  }
}

module identificationAccount 'modules/foundry-account.bicep' = if (!singleAccount) {
  name: 'foundry-account-cardid'
  scope: identificationRg
  params: {
    accountName: identificationAccountName
    teamIdentityIds: [ identificationIdentity.outputs.id ]
    location: location
  }
}

module geometryProject 'modules/foundry-project.bicep' = {
  name: 'cardgeo'
  scope: resourceGroup(geometryProjectRg)
  params: {
    accountName: geometryAccountName
    projectName: 'cardgeo'
    ownerGroupObjectId: teamAGroupObjectId
    teamIdentityIds: [ geometryIdentity.outputs.id ]
    location: location
  }
  dependsOn: [
    sharedAccount
    geometryAccount
  ]
}

module identificationProject 'modules/foundry-project.bicep' = {
  name: 'cardid'
  scope: resourceGroup(identificationProjectRg)
  params: {
    accountName: identificationAccountName
    projectName: 'cardid'
    ownerGroupObjectId: teamBGroupObjectId
    teamIdentityIds: [ identificationIdentity.outputs.id ]
    location: location
  }
  dependsOn: [
    sharedAccount
    identificationAccount
  ]
}

module hosting 'modules/hosting.bicep' = {
  name: 'hosting'
  scope: platformRg
  params: {
    namePrefix: namePrefix
    location: location
    storageAccountUrl: data.outputs.blobEndpoint
    queueAccountUrl: data.outputs.queueEndpoint
    cosmosEndpoint: data.outputs.cosmosEndpoint
    tenantId: tenantId
    apiClientId: apiClientId
    apiIdentityId: apiIdentity.outputs.id
    apiIdentityClientId: apiIdentity.outputs.clientId
    workerIdentityId: workerIdentity.outputs.id
    workerIdentityClientId: workerIdentity.outputs.clientId
    geometryProjectEndpoint: geometryProject.outputs.projectEndpoint
    identificationProjectEndpoint: identificationProject.outputs.projectEndpoint
  }
}

// Team A's MCP server, in Team A's resource group, running as Team A's identity — which already
// holds read on the scans container and nothing else.
module geometryMcp 'modules/mcp-servers.bicep' = {
  name: 'cardgeo-mcp'
  scope: geometryRg
  params: {
    namePrefix: namePrefix
    teamName: 'cardgeo'
    location: location
    identityId: geometryIdentity.outputs.id
    identityClientId: geometryIdentity.outputs.clientId
    tenantId: tenantId
    audience: mcpAudience
    storageAccountUrl: data.outputs.blobEndpoint
    servers: [
      {
        name: 'mcp-imaging'
        needsStorage: true
        // Only Team A's own project may call it. Team B reaches Team A through the boundary
        // agent, not by calling Team A's imaging tools directly.
        allowedCallerObjectIds: [
          geometryIdentity.outputs.principalId
          geometryProject.outputs.projectPrincipalId
        ]
      }
    ]
  }
}

// Team B's catalogue servers. Team A's principals are deliberately absent from every allow-list
// here: that is demo scenario 2, and it must fail even if someone hands Team A the URL.
module identificationMcp 'modules/mcp-servers.bicep' = {
  name: 'cardid-mcp'
  scope: identificationRg
  params: {
    namePrefix: namePrefix
    teamName: 'cardid'
    location: location
    identityId: identificationIdentity.outputs.id
    identityClientId: identificationIdentity.outputs.clientId
    tenantId: tenantId
    audience: mcpAudience
    servers: [
      {
        name: 'mcp-cardcatalog-mtg'
        needsStorage: false
        allowedCallerObjectIds: [
          identificationIdentity.outputs.principalId
          identificationProject.outputs.projectPrincipalId
        ]
      }
      {
        name: 'mcp-cardcatalog-pokemon'
        needsStorage: false
        allowedCallerObjectIds: [
          identificationIdentity.outputs.principalId
          identificationProject.outputs.projectPrincipalId
        ]
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
    identificationPrincipalId: identificationIdentity.outputs.principalId
  }
}

// Scoped to Team A's project, wherever that project lives. Flip grantIdentificationAccessToGeometry
// to false and redeploy to break the pipeline at DetectingBoundaries — see docs/foundry-demo.md.
module crossProjectAccess 'modules/cross-project-access.bicep' = if (grantIdentificationAccessToGeometry) {
  name: 'cross-project-access'
  scope: resourceGroup(geometryProjectRg)
  params: {
    geometryAccountName: geometryAccountName
    geometryProjectName: geometryProject.outputs.projectName
    identificationPrincipalId: identificationIdentity.outputs.principalId
  }
}

output apiUrl string = hosting.outputs.apiUrl
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
