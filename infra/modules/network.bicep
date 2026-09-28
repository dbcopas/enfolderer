// The private path from the compute tier to storage.
//
// Storage is reachable only over private endpoints, so every service that touches blobs or queues
// has to send its outbound traffic through this VNet. A Container Apps environment does that by
// being created on a subnet of its own — the delegation is exclusive, so an environment cannot
// share one — hence one subnet for the platform environment and one per team.
//
// The desktop client is deliberately absent from all of this: it only ever calls the API's public
// HTTPS endpoint, so it can run from anywhere without a VPN, a private resolver or a jump host.
targetScope = 'resourceGroup'

@description('Prefix for all resource names, e.g. "enf-demo".')
param namePrefix string

param location string = resourceGroup().location

@description('Resource id of the storage account to place behind private endpoints.')
param storageAccountId string

@description('''
Address space of the demo VNet. A /24 holds the three /27 environment subnets and the /28 for
private endpoints with room to spare; anything smaller will not, because /27 is the minimum a
Container Apps environment accepts.
''')
param addressPrefix string = '10.20.0.0/24'

// The three /27s take 10.20.0.0 - 10.20.0.95, so the private endpoints go in the /28 starting at
// .96. Index 6 of the /28 grid is that address; indexes 0-5 overlap the environment subnets.
var platformSubnetPrefix = cidrSubnet(addressPrefix, 27, 0)
var geometrySubnetPrefix = cidrSubnet(addressPrefix, 27, 1)
var identificationSubnetPrefix = cidrSubnet(addressPrefix, 27, 2)
var privateEndpointSubnetPrefix = cidrSubnet(addressPrefix, 28, 6)

// A Container Apps environment on a custom VNet takes the whole subnet: the delegation is
// exclusive, and the platform reserves addresses from it for the revisions it runs. /27 is the
// documented minimum for a workload-profiles environment.
var containerAppsDelegation = [
  {
    name: 'containerapps'
    properties: {
      serviceName: 'Microsoft.App/environments'
    }
  }
]

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: '${namePrefix}-vnet'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [ addressPrefix ]
    }
    subnets: [
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
        }
      }
      {
        name: 'platform-apps'
        properties: {
          addressPrefix: platformSubnetPrefix
          delegations: containerAppsDelegation
        }
      }
      {
        name: 'cardgeo-apps'
        properties: {
          addressPrefix: geometrySubnetPrefix
          delegations: containerAppsDelegation
        }
      }
      {
        name: 'cardid-apps'
        properties: {
          addressPrefix: identificationSubnetPrefix
          delegations: containerAppsDelegation
        }
      }
    ]
  }
}

// Without these zones the privatelink FQDNs still resolve to the account's public address, the
// request leaves the VNet and the private endpoint is never used. Linking them to the VNet is what
// makes the containers' DNS return the private address.
resource blobZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.blob.${environment().suffixes.storage}'
  location: 'global'
}

resource queueZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.queue.${environment().suffixes.storage}'
  location: 'global'
}

resource blobZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: blobZone
  name: '${namePrefix}-vnet'
  location: 'global'
  properties: {
    virtualNetwork: { id: vnet.id }
    registrationEnabled: false
  }
}

resource queueZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: queueZone
  name: '${namePrefix}-vnet'
  location: 'global'
  properties: {
    virtualNetwork: { id: vnet.id }
    registrationEnabled: false
  }
}

resource blobEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${namePrefix}-stg-blob-pe'
  location: location
  properties: {
    subnet: {
      id: '${vnet.id}/subnets/private-endpoints'
    }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: storageAccountId
          groupIds: [ 'blob' ]
        }
      }
    ]
  }
}

resource queueEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${namePrefix}-stg-queue-pe'
  location: location
  properties: {
    subnet: {
      id: '${vnet.id}/subnets/private-endpoints'
    }
    privateLinkServiceConnections: [
      {
        name: 'queue'
        properties: {
          privateLinkServiceId: storageAccountId
          groupIds: [ 'queue' ]
        }
      }
    ]
  }
}

resource blobZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: blobEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: { privateDnsZoneId: blobZone.id }
      }
    ]
  }
  dependsOn: [ blobZoneLink ]
}

resource queueZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: queueEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'queue'
        properties: { privateDnsZoneId: queueZone.id }
      }
    ]
  }
  dependsOn: [ queueZoneLink ]
}

output platformSubnetId string = '${vnet.id}/subnets/platform-apps'
output geometrySubnetId string = '${vnet.id}/subnets/cardgeo-apps'
output identificationSubnetId string = '${vnet.id}/subnets/cardid-apps'
