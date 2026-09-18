// The cross-project connection that the demo revokes: Team B's identification project may invoke
// Team A's boundary agent and nothing more.
//
// Scoped to Team A's *project*, not to the account. That matters when both projects share one
// account: an account-scoped grant would hand Team B access to every project in the account,
// including its own siblings, which is the opposite of what the demo is claiming.
targetScope = 'resourceGroup'

@description('Name of the Foundry account holding Team A\'s project.')
param geometryAccountName string

@description('Name of Team A\'s project.')
param geometryProjectName string

@description('Principal id of Team B\'s user-assigned managed identity.')
param identificationPrincipalId string

// Foundry User (formerly Azure AI User): run threads and invoke agents. It carries no authoring permission, so Team B
// cannot read the boundary agent's instructions, change its model or redeploy it.
var azureAiUser = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

resource geometryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: geometryAccountName
}

resource geometryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  parent: geometryAccount
  name: geometryProjectName
}

resource invokeAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: geometryProject
  name: guid(geometryProject.id, identificationPrincipalId, azureAiUser)
  properties: {
    principalId: identificationPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAiUser)
  }
}

output invokeAssignmentId string = invokeAssignment.id
output invokeAssignmentName string = invokeAssignment.name
