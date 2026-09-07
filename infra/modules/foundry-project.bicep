// One Azure AI Foundry account plus a single project, owned by one team. Deployed twice — once
// per resource group — so that "Team A" and "Team B" are genuinely separate blast radiuses.
targetScope = 'resourceGroup'

@description('Foundry account name (must be globally unique).')
param accountName string

@description('Project name inside the account, e.g. cardgeo or cardid.')
param projectName string

@description('Entra group object id that owns this project. Only this group gets write access.')
param ownerGroupObjectId string

@description('Resource id of the team\'s user-assigned managed identity.')
param teamIdentityId string

param location string = resourceGroup().location

@description('Model deployments to create in this project.')
param modelDeployments array = [
  {
    name: 'gpt-4o'
    model: 'gpt-4o'
    version: '2024-11-20'
    capacity: 10
  }
]

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  // The team's own user-assigned identity is what the project's agents present when they reach
  // outside the project, so the data-plane roles are granted to a principal that outlives this
  // account. A system-assigned identity is kept alongside it for services that cannot yet be
  // told which user-assigned identity to use.
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${teamIdentityId}': {}
    }
  }
  properties: {
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
    // Entra-only: no account keys to leak between teams.
    disableLocalAuth: true
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2024-10-01' = {
  parent: account
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${teamIdentityId}': {}
    }
  }
  properties: {
    displayName: projectName
  }
}

@batchSize(1)
resource deployments 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [for d in modelDeployments: {
  parent: account
  name: d.name
  sku: {
    name: 'GlobalStandard'
    capacity: d.capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: d.model
      version: d.version
    }
  }
}]

// Azure AI Project Manager: full authoring rights, granted only to the owning team's group.
// This is what makes Team B unable to edit Team A's boundary agent.
resource ownerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account
  name: guid(account.id, ownerGroupObjectId, 'project-manager')
  properties: {
    principalId: ownerGroupObjectId
    principalType: 'Group'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'eadc314b-1a2d-4efa-be10-5d325db5065e')
  }
}

output accountId string = account.id
output accountName string = account.name
output projectId string = project.id
output projectEndpoint string = 'https://${accountName}.services.ai.azure.com/api/projects/${projectName}'
output projectPrincipalId string = project.identity.principalId
output accountPrincipalId string = account.identity.principalId
