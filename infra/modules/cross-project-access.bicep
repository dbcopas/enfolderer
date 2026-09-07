// The cross-project connection that the demo revokes: Team B's identification project may invoke
// Team A's boundary agent and nothing more.
//
// Deployed into Team A's resource group, because only Team A can grant access to Team A's assets.
targetScope = 'resourceGroup'

@description('Name of Team A\'s Foundry account.')
param geometryAccountName string

@description('Principal id of Team B\'s user-assigned managed identity.')
param identificationPrincipalId string

// Azure AI User: run threads and invoke agents. It carries no authoring permission, so Team B
// cannot read the boundary agent's instructions, change its model or redeploy it.
var azureAiUser = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

resource geometryAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: geometryAccountName
}

resource invokeAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: geometryAccount
  name: guid(geometryAccount.id, identificationPrincipalId, azureAiUser)
  properties: {
    principalId: identificationPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAiUser)
  }
}

output invokeAssignmentId string = invokeAssignment.id
output invokeAssignmentName string = invokeAssignment.name
