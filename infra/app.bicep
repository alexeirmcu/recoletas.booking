targetScope = 'resourceGroup'

@allowed([
  'dev'
  'prod'
])
param deploymentEnvironment string
param location string
param registryName string
param registryResourceGroupName string
param imageReference string
param keyVaultName string
param rabbitHostName string
param rabbitTlsServerName string = ''

var prefix = 'recoletas-${deploymentEnvironment}'

resource environmentResource 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: '${prefix}-aca'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${prefix}-identity'
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
  scope: resourceGroup(registryResourceGroupName)
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: environmentResource.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
      secrets: [
        {
          name: 'booking-connection'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/connection-strings--booking'
          identity: identity.id
        }
        {
          name: 'jwt-secret'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/jwt--secret'
          identity: identity.id
        }
        {
          name: 'jwt-issuer'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/jwt--issuer'
          identity: identity.id
        }
        {
          name: 'jwt-audience'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/jwt--audience'
          identity: identity.id
        }
        {
          name: 'rabbit-user'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/rabbitmq--user-name'
          identity: identity.id
        }
        {
          name: 'rabbit-password'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/rabbitmq--password'
          identity: identity.id
        }
        {
          name: 'rabbit-vhost'
          keyVaultUrl: 'https://${keyVaultName}.${environment().suffixes.keyvaultDns}/secrets/rabbitmq--virtual-host'
          identity: identity.id
        }
      ]
    }
    template: {
      revisionSuffix: substring(replace(last(split(imageReference, '@')), 'sha256:', ''), 0, 7)
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
      containers: [
        {
          name: 'api'
          image: imageReference
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
              name: 'Booking__SeedDemoData'
              value: 'false'
            }
            {
              name: 'ConnectionStrings__Booking'
              secretRef: 'booking-connection'
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
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 10
            }
          ]
        }
      ]
    }
  }
}

output fqdn string = api.properties.configuration.ingress.fqdn
