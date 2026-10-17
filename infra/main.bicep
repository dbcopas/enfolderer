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
//            the invoke-only assignment in modules/foundry-invoke-access.bicep.
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

@description('Single-tenant Microsoft SPA registration for the mobile website. Empty disables browser sign-in.')
param mobileWebClientId string = ''

@description('Comma-separated tenant user/guest object IDs approved for the private scan pilot. Applies to desktop and web API access.')
param scanAllowedUserObjectIds string = ''

@description('Comma-separated operator object IDs allowed read-only diagnostics across approved pilot scans. Empty disables diagnostics.')
param scanDiagnosticUserObjectIds string = ''

@description('Set to false to run the "revoke Team B\'s access to Team A\'s agent" demo scenario.')
param grantIdentificationAccessToGeometry bool = true

@description('One Foundry account holding both projects (true, the default), or one account per team (false). True is the layout this demo is about: the boundary between the teams is then the Foundry project boundary, enforced by project-scoped role assignments. False additionally separates quota, model deployments and account settings, but the boundary becomes plain Azure RBAC between two unrelated resources — stronger isolation, weaker demonstration of Foundry itself.')
param singleAccount bool = true

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

@description('Named Foundry agent for Team A\'s boundary detection, not a classic asst_ id.')
param boundaryAgentId string = 'CardBoundaryAgent'

@description('Named Foundry agent for Magic identification.')
param mtgAgentId string = 'MtgCardIdAgent'

@description('Named Foundry agent for Pokemon identification.')
param pokemonAgentId string = 'PokemonCardIdAgent'

@description('Distinct Entra agent identity principal of the boundary agent. Empty only during placeholder bootstrap.')
param boundaryAgentPrincipalId string = ''

@description('Distinct Entra agent identity principal of the Magic agent.')
param mtgAgentPrincipalId string = ''

@description('Distinct Entra agent identity principal of the Pokemon agent.')
param pokemonAgentPrincipalId string = ''

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

// The private path to the data tier. Deployed before anything that reaches the data plane, so the
// endpoints and DNS zones exist by the time the API and worker start.
module network 'modules/network.bicep' = {
  name: 'network'
  scope: platformRg
  params: {
    namePrefix: namePrefix
    location: location
    storageAccountId: data.outputs.storageAccountId
    cosmosAccountId: data.outputs.cosmosAccountId
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

// One registry for all five images, owned by the platform team. Both teams can pull from it, which
// is the same kind of shared platform dependency as the Foundry account and its model deployment —
// see the "two isolation tiers" note at the top of this file.
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

// One Container Apps environment per team, each on its own subnet and with its own log workspace.
// The environment is the thing a team operates, so sharing one would quietly undo the resource
// group boundary the rest of this file is built on.
module platformEnv 'modules/container-env.bicep' = {
  name: 'platform-env'
  scope: platformRg
  params: {
    name: '${namePrefix}-platform-env'
    location: location
    infrastructureSubnetId: network.outputs.platformSubnetId
  }
}

module geometryEnv 'modules/container-env.bicep' = {
  name: 'cardgeo-env'
  scope: geometryRg
  params: {
    name: '${namePrefix}-cardgeo-env'
    location: location
    infrastructureSubnetId: network.outputs.geometrySubnetId
  }
}

module identificationEnv 'modules/container-env.bicep' = {
  name: 'cardid-env'
  scope: identificationRg
  params: {
    name: '${namePrefix}-cardid-env'
    location: location
    infrastructureSubnetId: network.outputs.identificationSubnetId
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
    apiIdentityId: apiIdentity.outputs.id
    apiIdentityClientId: apiIdentity.outputs.clientId
    workerIdentityId: workerIdentity.outputs.id
    workerIdentityClientId: workerIdentity.outputs.clientId
    geometryProjectEndpoint: geometryProject.outputs.projectEndpoint
    identificationProjectEndpoint: identificationProject.outputs.projectEndpoint
    boundaryAgentId: boundaryAgentId
    mtgAgentId: mtgAgentId
    pokemonAgentId: pokemonAgentId
  }
}

// Team A's MCP server, in Team A's resource group, running as Team A's identity — which already
// holds read on the scans container and nothing else.
module geometryMcp 'modules/mcp-servers.bicep' = {
  name: 'cardgeo-mcp'
  scope: geometryRg
  params: {
    namePrefix: namePrefix
    location: location
    environmentId: geometryEnv.outputs.id
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

// Team B's catalogue servers. Team A's principals are deliberately absent from every allow-list
// here: that is demo scenario 2, and it must fail even if someone hands Team A the URL.
module identificationMcp 'modules/mcp-servers.bicep' = {
  name: 'cardid-mcp'
  scope: identificationRg
  params: {
    namePrefix: namePrefix
    location: location
    environmentId: identificationEnv.outputs.id
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
      {
        name: 'mcp-cardcatalog-pokemon'
        image: 'mcp-cardcatalog-pokemon'
        needsStorage: false
        allowedCallerObjectIds: empty(pokemonAgentPrincipalId) ? [] : [pokemonAgentPrincipalId]
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

// The orchestrator's access to Team B's own project. The worker creates a thread, uploads the card
// image and runs an identification agent there on every job, so without this the pipeline cannot
// start — and because the role is Foundry User rather than Project Manager, the orchestrator still
// cannot author or alter a single agent.
module identificationAccess 'modules/foundry-invoke-access.bicep' = {
  name: 'cardid-invoke-access'
  scope: resourceGroup(identificationProjectRg)
  params: {
    accountName: identificationAccountName
    projectName: identificationProject.outputs.projectName
    principalId: workerIdentity.outputs.principalId
  }
}

// The same grant on Team A's project, wherever that project lives. This one is the cross-project
// hop: it is the only route from Team B's pipeline into Team A's, and it is granted to the worker
// because the worker is the identity that actually presents itself to the cardgeo endpoint. Flip
// grantIdentificationAccessToGeometry to false and redeploy to break the pipeline at
// DetectingBoundaries — see docs/foundry-demo.md.
module crossProjectAccess 'modules/foundry-invoke-access.bicep' = if (grantIdentificationAccessToGeometry) {
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
