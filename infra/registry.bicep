targetScope = 'resourceGroup'

@description('Azure region for the container registry.')
param location string = resourceGroup().location

@description('Short lowercase prefix used for resource names. Use letters and numbers only.')
@minLength(3)
@maxLength(10)
param resourcePrefix string

var uniqueSuffix = uniqueString(resourceGroup().id, resourcePrefix)
var acrName = toLower(take('${resourcePrefix}acr${uniqueSuffix}', 50))

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
