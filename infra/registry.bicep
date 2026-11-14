targetScope = 'resourceGroup'

@description('Globally unique Azure Container Registry name.')
@minLength(5)
@maxLength(50)
param registryName string

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: resourceGroup().location
  sku: {
    name: 'Standard'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

output registryLoginServer string = registry.properties.loginServer
