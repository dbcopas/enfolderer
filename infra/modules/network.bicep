// The private path from the App Service tier to storage.
//
// Storage is reachable only over private endpoints, so every service that touches blobs or queues
// has to send its outbound traffic through this VNet. App Service does that with regional VNet
// integration, which needs a subnet of its own per App Service plan — a delegated subnet cannot be
// shared between plans — hence one integration subnet for the platform plan and one per team.
//
// The desktop client is deliberately absent from all of this: it only ever calls the API's public
// HTTPS endpoint, so it can run from anywhere without a VPN, a private resolver or a jump host.
targetScope = 'resourceGroup'

@description('Prefix for all resource names, e.g. "enf-demo".')
param namePrefix string

param location string = resourceGroup().location

@description('Resource id of the storage account to place behind private endpoints.')
param storageAccountId string

@description('Address space of the demo VNet. /24 is ample: the subnets below are /28.')
param addressPrefix string = '10.20.0.0/24'

var privateEndpointSubnetPrefix = cidrSubnet(addressPrefix, 28, 0)
var platformSubnetPrefix = cidrSubnet(addressPrefix, 28, 1)
var geometrySubnetPrefix = cidrSubnet(addressPrefix, 28, 2)
var identificationSubnetPrefix = cidrSubnet(addressPrefix, 28, 3)

// A subnet delegated to Microsoft.Web/serverFarms carries outbound traffic for one App Service
// plan. /28 is the documented minimum and gives App Service the addresses it reserves for scaling.
var webDelegation = [
  {
    name: 'appservice'
    properties: {
      serviceName: 'Microsoft.Web/serverFarms'
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
        name: 'platform-integration'
        properties: {
          addressPrefix: platformSubnetPrefix
          delegations: webDelegation
        }
      }
      {
        name: 'cardgeo-integration'
        properties: {
          addressPrefix: geometrySubnetPrefix
          delegations: webDelegation
        }
      }
      {
        name: 'cardid-integration'
        properties: {
          addressPrefix: identificationSubnetPrefix
          delegations: webDelegation
        }
      }
    ]
  }
}

// Without these zones the privatelink FQDNs still resolve to the account's public address, the
// request leaves the VNet and the private endpoint is never used. Linking them to the VNet is what
// makes App Service's DNS return the private address.
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

output platformSubnetId string = '${vnet.id}/subnets/platform-integration'
output geometrySubnetId string = '${vnet.id}/subnets/cardgeo-integration'
output identificationSubnetId string = '${vnet.id}/subnets/cardid-integration'
