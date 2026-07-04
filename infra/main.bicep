targetScope = 'resourceGroup'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Short lowercase prefix used for resource names. Use letters and numbers only.')
@minLength(3)
@maxLength(10)
param resourcePrefix string

@description('Existing Azure Container Registry name.')
param acrName string

@description('Full container image name, including registry, repository, and tag.')
param containerImage string

@description('Microsoft Entra tenant ID used for JWT validation.')
param authenticationTenantId string

@description('Expected JWT audience, usually api://<api-app-client-id>.')
param authenticationAudience string

@description('Blob container name for uploaded images.')
param imageContainerName string = 'images'

@description('Minimum Container App replicas. Use 0 for low-cost PoC scale-to-zero.')
@minValue(0)
@maxValue(10)
param minReplicas int = 0

@description('Maximum Container App replicas.')
@minValue(1)
@maxValue(100)
param maxReplicas int = 3

var uniqueSuffix = uniqueString(resourceGroup().id, resourcePrefix)

var appName = '${resourcePrefix}-api-${uniqueSuffix}'
var envName = '${resourcePrefix}-env-${uniqueSuffix}'
var identityName = '${resourcePrefix}-id-${uniqueSuffix}'
var logAnalyticsName = '${resourcePrefix}-log-${uniqueSuffix}'
var imageStorageName = take('${resourcePrefix}img${uniqueSuffix}', 24)

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource imageStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: imageStorageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource imageBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  name: 'default'
  parent: imageStorage
}

resource imageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: imageContainerName
  parent: imageBlobService
  properties: {
    publicAccess: 'None'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2026-01-01' = {
  name: envName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, appIdentity.id, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(imageContainer.id, appIdentity.id, storageBlobDataContributorRoleId)
  scope: imageContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource containerApp 'Microsoft.App/containerApps@2026-01-01' = {
  name: appName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${appIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        transport: 'http'
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: [
        {
          server: acr.properties.loginServer
          identity: appIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'image-api'
          image: containerImage
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'Authentication__TenantId'
              value: authenticationTenantId
            }
            {
              name: 'Authentication__Audience'
              value: authenticationAudience
            }
            {
              name: 'ImageStorageBlobServiceUri'
              value: imageStorage.properties.primaryEndpoints.blob
            }
            {
              name: 'ImageStorageContainerName'
              value: imageContainerName
            }
            {
              name: 'ImageStorageManagedIdentityClientId'
              value: appIdentity.properties.clientId
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/api/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 1
              periodSeconds: 5
              timeoutSeconds: 3
              failureThreshold: 12
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/api/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 30
              timeoutSeconds: 3
              failureThreshold: 3
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-scale'
            http: {
              metadata: {
                concurrentRequests: '20'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    acrPull
    blobDataContributor
  ]
}

output containerAppName string = containerApp.name
output apiBaseUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}/api'
output acrLoginServer string = acr.properties.loginServer
output imageStorageAccountName string = imageStorage.name
output managedIdentityClientId string = appIdentity.properties.clientId
