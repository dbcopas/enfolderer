// A Container Apps environment and the workspace its console logs land in.
//
// One per team rather than one shared environment, because the environment is the unit a team owns
// and operates: revisions, scale, secrets and logs all belong to it. A shared environment would put
// Team A's containers and Team B's containers in the same resource, in one resource group, under
// one set of permissions, and the resource-group boundary the demo relies on would stop being a
// boundary at all.
//
// The workspace is per-environment for the same reason. Central logging is a defensible design, but
// it would mean Team B could read Team A's container output, and this demo is specifically about
// what one team cannot see.
targetScope = 'resourceGroup'

@description('Name of the environment, e.g. "enf-demo-cardgeo-env".')
param name string

param location string = resourceGroup().location

@description('''
Resource id of the subnet carrying this environment. Must be at least a /27 and delegated to
Microsoft.App/environments. All outbound traffic leaves through it, which is how the containers
reach the private endpoints for storage.
''')
param infrastructureSubnetId string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    // A demo does not need a month of container logs, and retention is what a Log Analytics bill
    // is made of.
    retentionInDays: 30
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: name
  location: location
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: infrastructureSubnetId
      // The API and the MCP servers need public ingress: the desktop client must run anywhere, and
      // Foundry calls the tool servers from outside this VNet. Outbound traffic still leaves
      // through the subnet regardless, which is what reaches private storage.
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
  }
}

output id string = environment.id
output defaultDomain string = environment.properties.defaultDomain
output workspaceName string = workspace.name
