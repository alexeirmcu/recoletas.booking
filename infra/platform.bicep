targetScope = 'resourceGroup'

@allowed([
  'dev'
  'prod'
])
param deploymentEnvironment string
param location string
param registryName string
param registryResourceGroupName string
@description('Non-overlapping VNet CIDR for this environment.')
param vnetAddressPrefix string
@description('Globally unique Key Vault name for this environment.')
param keyVaultName string
@secure()
param postgresAdminPassword string
@secure()
param jwtSecret string
@secure()
param jwtIssuer string
@secure()
param jwtAudience string
param rabbitHostName string
@secure()
param rabbitVirtualHost string
@secure()
param rabbitUserName string
@secure()
param rabbitPassword string
param rabbitTlsServerName string = ''
@description('Immutable ACR image reference, including its sha256 digest.')
param imageReference string

var prefix = 'recoletas-${deploymentEnvironment}'
var postgresSubnetPrefix = cidrSubnet(vnetAddressPrefix, 24, 8)
var containerAppsSubnetPrefix = cidrSubnet(vnetAddressPrefix, 23, 0)
var privateEndpointSubnetPrefix = cidrSubnet(vnetAddressPrefix, 27, 128)
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${prefix}-logs'
  location: location
  properties: {
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
    }
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: '${prefix}-vnet'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: 'postgres'
        properties: {
          addressPrefix: postgresSubnetPrefix
          delegations: [
            {
              name: 'postgres-flexible'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
          privateEndpointNetworkPolicies: 'Enabled'
        }
      }
      {
        name: 'container-apps'
        properties: {
          addressPrefix: containerAppsSubnetPrefix
          delegations: [
            {
              name: 'container-apps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource vaultPrivateDns 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
}

resource vaultPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: vaultPrivateDns
  name: '${prefix}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

resource vaultPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${prefix}-vault-endpoint'
  location: location
  properties: {
    subnet: {
      id: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'private-endpoints')
    }
    privateLinkServiceConnections: [
      {
        name: '${prefix}-vault'
        properties: {
          privateLinkServiceId: vault.id
          groupIds: [
            'vault'
          ]
        }
      }
    ]
  }
  dependsOn: [
    vaultPrivateDnsLink
  ]
}

resource vaultPrivateDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: vaultPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'vault'
        properties: {
          privateDnsZoneId: vaultPrivateDns.id
        }
      }
    ]
  }
}

resource postgresDns 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'private.postgres.database.azure.com'
  location: 'global'
}

resource postgresDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: postgresDns
  name: '${prefix}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${prefix}-pg'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: 'pgadmin'
    administratorLoginPassword: postgresAdminPassword
    createMode: 'Default'
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    storage: {
      storageSizeGB: 32
      autoGrow: 'Disabled'
    }
    network: {
      delegatedSubnetResourceId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'postgres')
      privateDnsZoneArmResourceId: postgresDns.id
      publicNetworkAccess: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
  }
  dependsOn: [
    postgresDnsLink
  ]
}

resource bookingDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'booking'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-aca'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'container-apps')
      internal: false
    }
    zoneRedundant: false
  }
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-identity'
  location: location
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
  scope: resourceGroup(registryResourceGroupName)
}

module registryPullAssignment 'modules/registry-pull-role.bicep' = {
  name: 'registry-pull-${deploymentEnvironment}'
  scope: resourceGroup(registryResourceGroupName)
  params: {
    registryName: registryName
    runtimeIdentityPrincipalId: runtimeIdentity.properties.principalId
  }
}

resource vaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, runtimeIdentity.id, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource connectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'connection-strings--booking'
  properties: {
    value: 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=booking;Username=pgadmin;Password=${postgresAdminPassword};Ssl Mode=Require;Trust Server Certificate=false'
  }
  dependsOn: [
    bookingDatabase
  ]
}

resource jwtSecretResource 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'jwt--secret'
  properties: {
    value: jwtSecret
  }
}

resource jwtIssuerSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'jwt--issuer'
  properties: {
    value: jwtIssuer
  }
}

resource jwtAudienceSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'jwt--audience'
  properties: {
    value: jwtAudience
  }
}

resource rabbitUserSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'rabbitmq--user-name'
  properties: {
    value: rabbitUserName
  }
}

resource rabbitPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'rabbitmq--password'
  properties: {
    value: rabbitPassword
  }
}

resource rabbitVhostSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'rabbitmq--virtual-host'
  properties: {
    value: rabbitVirtualHost
  }
}

resource migrationJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${prefix}-migrate'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: runtimeIdentity.id
        }
      ]
      secrets: [
        {
          name: 'booking-connection'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/connection-strings--booking'
          identity: runtimeIdentity.id
        }
        {
          name: 'rabbit-user'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/rabbitmq--user-name'
          identity: runtimeIdentity.id
        }
        {
          name: 'rabbit-password'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/rabbitmq--password'
          identity: runtimeIdentity.id
        }
        {
          name: 'rabbit-vhost'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/rabbitmq--virtual-host'
          identity: runtimeIdentity.id
        }
        {
          name: 'jwt-secret'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/jwt--secret'
          identity: runtimeIdentity.id
        }
        {
          name: 'jwt-issuer'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/jwt--issuer'
          identity: runtimeIdentity.id
        }
        {
          name: 'jwt-audience'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/jwt--audience'
          identity: runtimeIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: imageReference
          command: [
            'dotnet'
          ]
          args: [
            'Recoletas.Booking.API.dll'
            '--migrate-only'
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'Booking__ApplyMigrationsOnStartup'
              value: 'false'
            }
            {
              name: 'ConnectionStrings__Booking'
              secretRef: 'booking-connection'
            }
            {
              name: 'RabbitMq__HostName'
              value: rabbitHostName
            }
            {
              name: 'RabbitMq__Port'
              value: '5671'
            }
            {
              name: 'RabbitMq__UseTls'
              value: 'true'
            }
            {
              name: 'RabbitMq__TlsServerName'
              value: rabbitTlsServerName
            }
            {
              name: 'RabbitMq__UserName'
              secretRef: 'rabbit-user'
            }
            {
              name: 'RabbitMq__Password'
              secretRef: 'rabbit-password'
            }
            {
              name: 'RabbitMq__VirtualHost'
              secretRef: 'rabbit-vhost'
            }
            {
              name: 'Jwt__Secret'
              secretRef: 'jwt-secret'
            }
            {
              name: 'Jwt__Issuer'
              secretRef: 'jwt-issuer'
            }
            {
              name: 'Jwt__Audience'
              secretRef: 'jwt-audience'
            }
          ]
        }
      ]
    }
  }
  dependsOn: [
    connectionSecret
    jwtSecretResource
    jwtIssuerSecret
    jwtAudienceSecret
    rabbitUserSecret
    rabbitPasswordSecret
    rabbitVhostSecret
    registryPullAssignment
    vaultRole
  ]
}

output postgresFqdn string = postgres.properties.fullyQualifiedDomainName
output containerEnvironmentName string = containerEnvironment.name
output migrationJobName string = migrationJob.name
output runtimeIdentityId string = runtimeIdentity.id
output vaultName string = vault.name
