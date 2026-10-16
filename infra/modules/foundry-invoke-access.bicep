// Invoke-only access to one Foundry project, granted to one principal.
//
// Scoped to the *project*, never to the account. That distinction is the whole demo when both
// projects share an account: an account-scoped grant would hand the principal access to every
// project in the account, including its siblings, which is the opposite of what is being claimed.
//
// Used twice from main.bicep:
//
//   * the orchestrator on Team B's own project, which it must always have; and
//   * the orchestrator on Team A's project, which is the cross-project hop that demo scenario 1
//     revokes by redeploying with grantIdentificationAccessToGeometry=false.
targetScope = 'resourceGroup'

@description('Name of the Foundry account holding the project.')
param accountName string

@description('Name of the project to grant access to, e.g. cardgeo or cardid.')
param projectName string

@description('Principal id of the managed identity being granted invoke access.')
param principalId string

// Agent endpoints need only interact/action. Foundry User also permits authoring and is not an
// invoke-only role; the worker must not hold it after the classic-to-Responses migration.
var agentConsumer = 'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  parent: account
  name: projectName
}

resource invokeAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: project
  name: guid(project.id, principalId, agentConsumer)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', agentConsumer)
  }
}

output invokeAssignmentId string = invokeAssignment.id
output invokeAssignmentName string = invokeAssignment.name
