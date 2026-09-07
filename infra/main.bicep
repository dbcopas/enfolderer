// Enfolderer AI scan pipeline — full demo environment.
//
// Three resource groups on purpose:
//   <prefix>-platform  shared data plane + API/worker hosting (platform team)
//   <prefix>-cardgeo   Team A's Foundry project (geometry)
//   <prefix>-cardid    Team B's Foundry project (identification)
//
// Separate resource groups with different owner groups are what make the demo real: Team B
// cannot grant itself anything in Team A's group, so the only path from cardid to cardgeo is
// the invoke-only role assignment created by modules/cross-project-access.bicep.
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

module geometryProject 'modules/foundry-project.bicep' = {
  name: 'cardgeo'
  scope: geometryRg
  params: {
    accountName: '${namePrefix}-cardgeo-ai'
    projectName: 'cardgeo'
    ownerGroupObjectId: teamAGroupObjectId
    teamIdentityId: geometryIdentity.outputs.id
    location: location
  }
}

module identificationProject 'modules/foundry-project.bicep' = {
  name: 'cardid'
  scope: identificationRg
  params: {
    accountName: '${namePrefix}-cardid-ai'
    projectName: 'cardid'
    ownerGroupObjectId: teamBGroupObjectId
    teamIdentityId: identificationIdentity.outputs.id
    location: location
  }
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

// Deployed in Team A's resource group. Flip grantIdentificationAccessToGeometry to false and
// redeploy to break the pipeline at DetectingBoundaries — see docs/foundry-demo.md.
module crossProjectAccess 'modules/cross-project-access.bicep' = if (grantIdentificationAccessToGeometry) {
  name: 'cross-project-access'
  scope: geometryRg
  params: {
    geometryAccountName: geometryProject.outputs.accountName
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
