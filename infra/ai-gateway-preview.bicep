targetScope = 'resourceGroup'

param namePrefix string
param ownerObjectId string
param location string = resourceGroup().location

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: '${namePrefix}-platform-env-logs'
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-gateway-insights'
  location: location
  kind: 'web'
  tags: {
    enfoldererPrefix: namePrefix
    enfoldererPurpose: 'ai-gateway-monitoring'
    enfoldererOwner: ownerObjectId
  }
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    RetentionInDays: 30
  }
}

resource gateways 'Microsoft.ApiManagement/aigateways@2025-09-01-preview' = [for suffix in ['', '-geo', '-mtg', '-pkm']: {
  name: '${namePrefix}-aigw${suffix}'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    enfoldererPrefix: namePrefix
    enfoldererPurpose: 'ai-gateway'
    enfoldererOwner: ownerObjectId
  }
  properties: {}
}]

output gatewayIds array = [for (suffix, index) in ['', '-geo', '-mtg', '-pkm']: gateways[index].id]
