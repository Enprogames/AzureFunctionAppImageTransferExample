targetScope = 'resourceGroup'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Short lowercase prefix used for resource names. Use letters and numbers only.')
@minLength(3)
@maxLength(10)
param resourcePrefix string

@description('Blob container name for uploaded images.')
param imageContainerName string = 'images'

@description('Maximum Flex Consumption instances.')
@minValue(1)
@maxValue(1000)
param maximumInstanceCount int = 20

@description('Memory per Flex Consumption instance in MB.')
@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048

var uniqueSuffix = uniqueString(resourceGroup().id, resourcePrefix)

var functionAppName = '${resourcePrefix}-fn-${uniqueSuffix}'
var planName = '${resourcePrefix}-plan-${uniqueSuffix}'
var identityName = '${resourcePrefix}-id-${uniqueSuffix}'
var logAnalyticsName = '${resourcePrefix}-log-${uniqueSuffix}'
var appInsightsName = '${resourcePrefix}-appi-${uniqueSuffix}'

// Storage account names must be globally unique, lowercase, and <= 24 characters.
var hostStorageName = take('${resourcePrefix}host${uniqueSuffix}', 24)
var imageStorageName = take('${resourcePrefix}img${uniqueSuffix}', 24)

var functionAppRuntime = 'dotnet-isolated'
var functionAppRuntimeVersion = '10'
