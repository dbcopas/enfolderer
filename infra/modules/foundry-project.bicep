// Logical pipeline project, administered by the same operator as its sibling.
targetScope = 'resourceGroup'

@description('Name of the Foundry account this project belongs to. Must already exist.')
param accountName string

@description('Project name inside the account, e.g. cardgeo or cardid.')
param projectName string

@description('Entra user object ID administering the deployment.')
param ownerObjectId string

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

// Project Manager for the deployment operator; workload identities remain invoke-only.
resource ownerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: project
  name: guid(project.id, ownerObjectId, 'project-manager')
  properties: {
    principalId: ownerObjectId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'eadc314b-1a2d-4efa-be10-5d325db5065e')
  }
}

// The project half of the agent backend. The account-level capability host enables Agent Service
// for the account; this one retains the project's agent runtime and thread storage.
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
