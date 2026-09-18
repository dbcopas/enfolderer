// One project inside a Foundry account, owned by one team.
//
// The project is the inner isolation tier and the one this demo is about: the owning team's group
// gets Foundry Project Manager scoped to *this project*, so it can author agents and connections
// here and has no rights in any sibling project of the same account.
//
// What a project does not isolate is anything owned by the account: model deployments, quota,
// local-auth and networking settings. Those are in modules/foundry-account.bicep.
targetScope = 'resourceGroup'

@description('Name of the Foundry account this project belongs to. Must already exist.')
param accountName string

@description('Project name inside the account, e.g. cardgeo or cardid.')
param projectName string

@description('Entra group object id that owns this project. Only this group gets authoring access.')
param ownerGroupObjectId string

@description('Resource ids of the user-assigned managed identities this project may present.')
param teamIdentityIds array

param location string = resourceGroup().location

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: reduce(teamIdentityIds, {}, (merged, id) => union(merged, { '${id}': {} }))
  }
  properties: {
    displayName: projectName
  }
}

// Foundry Project Manager (formerly Azure AI Project Manager): full authoring rights, granted only
// to the owning team's group and only over this project. Scoping it here rather than at the
// account is what makes the boundary a
// Foundry one: with both projects in a single account, Team B still cannot edit Team A's agents.
resource ownerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: project
  name: guid(project.id, ownerGroupObjectId, 'project-manager')
  properties: {
    principalId: ownerGroupObjectId
    principalType: 'Group'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'eadc314b-1a2d-4efa-be10-5d325db5065e')
  }
}

output projectId string = project.id
output projectName string = project.name
output projectEndpoint string = 'https://${accountName}.services.ai.azure.com/api/projects/${projectName}'
output projectPrincipalId string = project.identity.principalId
