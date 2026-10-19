// Shared Foundry account and pay-per-token model deployment.
targetScope = 'resourceGroup'

@description('Foundry account name (must be globally unique).')
param accountName string

@description('Resource ids of the user-assigned managed identities the account may present.')
param teamIdentityIds array

param location string = resourceGroup().location

@description('Model deployments to create. Shared by every project in this account.')
param modelDeployments array = [
  {
    name: 'gpt-5-mini'
    model: 'gpt-5-mini'
    version: '2025-08-07'
    // Keep 100k TPM headroom for multi-card scans; this is not provisioned throughput.
    capacity: 100
  }
]

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  // These are infrastructure managed identities, not the distinct Entra Agent IDs used by named
  // agents for MCP authentication. Foundry provisions agent identities/blueprints with the agent.
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: reduce(teamIdentityIds, {}, (merged, id) => union(merged, { '${id}': {} }))
  }
  properties: {
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
    // Entra-only: no account keys to leak between teams.
    disableLocalAuth: true
    // Required before the account will accept child projects; without it the project deployment
    // fails even though the API version is correct.
    allowProjectManagement: true
  }
}

// Serialised because deployments against one account contend for the same quota.
@batchSize(1)
resource deployments 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = [for d in modelDeployments: {
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

// The capability host is the agent backend: it tells Foundry Agent Service where to run agents and
// store their threads. ARM provisions it as a separate sub-resource, so an account and a project
// can both report Succeeded while the agents data plane has nothing behind it and answers every
// call with 500 "Unable to get resource information."
//
// Declaring no storage, vector-store or thread-store connections is what selects the
// Microsoft-managed resources behind it, which is what this demo wants: the isolation being
// demonstrated is between the two projects, not between our own storage accounts.
//
// Capability hosts cannot be updated in place. If this ever needs changing, delete it and let the
// next deployment recreate it; see "Neither agent surface answers" in docs/azure-setup.md.
resource accountCapabilityHost 'Microsoft.CognitiveServices/accounts/capabilityHosts@2025-06-01' = {
  parent: account
  name: '${accountName}-caphost'
  properties: {
    capabilityHostKind: 'Agents'
  }
  // The account's own deployments must settle first: both contend for the same account, and ARM
  // will reject a capability host created while a deployment is still in flight.
  dependsOn: [ deployments ]
}

output accountId string = account.id
output capabilityHostId string = accountCapabilityHost.id
output accountName string = account.name
output accountPrincipalId string = account.identity.principalId
