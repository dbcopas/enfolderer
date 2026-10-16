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

// The project half of the agent backend. The account-level capability host enables Agent Service
// for the account; this one gives *this project* its own agent runtime and thread storage, which
// is what keeps one team's agents and conversations out of the other's.
//
// It cannot be created before the account's capability host exists. main.bicep already orders the
// account module ahead of this one, which is what guarantees that.
resource projectCapabilityHost 'Microsoft.CognitiveServices/accounts/projects/capabilityHosts@2025-06-01' = {
  parent: project
  name: '${projectName}-caphost'
  // Deliberately empty. Unlike the account-level host, the project schema has no capabilityHostKind
  // — its only properties are the four connection lists (aiServices, storage, threadStorage,
  // vectorStore), and leaving them unset is what selects the Microsoft-managed resources behind the
  // project. Some Microsoft samples pass capabilityHostKind here; the ARM spec's
  // ProjectCapabilityHost does not define it.
  properties: {}
}

output projectId string = project.id
output projectName string = project.name
// The host comes from the account's customSubDomainName, not its resource name. They are equal in
// this deployment because foundry-account.bicep sets the subdomain to the account name, but reading
// the account means a rename of either one cannot silently point every app at a host that does not
// exist.
output projectEndpoint string = 'https://${account.properties.customSubDomainName}.services.ai.azure.com/api/projects/${projectName}'
output projectPrincipalId string = project.identity.principalId
