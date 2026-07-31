// Hermes Teams Relay — infrastructure
//
// Provisions:
//   - An Azure Relay namespace + Hybrid Connection (the tunnel that replaces the dev tunnel)
//   - Two SAS authorization rules on the Hybrid Connection: one scoped to "Listen"
//     (used by the on-prem listener sitting next to Hermes) and one scoped to "Send"
//     (used by the Azure Function that Teams/Bot Framework actually calls)
//   - A .NET isolated-worker Function App (Consumption plan) that is the public
//     HTTPS endpoint registered with Bot Framework, wired with the Send SAS values
//
// Deploy with:
//   az deployment group create \
//     --resource-group <rg> \
//     --template-file infra/main.bicep \
//     --parameters namePrefix=hermesteams
//
// After deployment, the Function App's messaging endpoint is:
//   https://<namePrefix>-func.azurewebsites.net/api/messages
// Set that URL as the Messaging endpoint on the Azure Bot resource
// (see teams-app/README.md for the Portal-based bot registration steps).

@description('Short, globally-unique-ish prefix used to derive resource names (lowercase alphanumeric, <=11 chars recommended).')
@minLength(3)
@maxLength(11)
param namePrefix string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Name of the Hybrid Connection created on the Relay namespace.')
param hybridConnectionName string = 'hermes-teams-messages'

@description('Tags applied to all resources.')
param tags object = {
  solution: 'hermes-teams-relay'
}

var relayNamespaceName = '${namePrefix}-relay'
var functionAppName = '${namePrefix}-func'
var appServicePlanName = '${namePrefix}-plan'
// Storage account names must be <=24 chars, lowercase letters/numbers only.
var storageAccountName = toLower(replace('${namePrefix}stg${uniqueString(resourceGroup().id)}', '-', ''))
var appInsightsName = '${namePrefix}-appi'

resource relayNamespace 'Microsoft.Relay/namespaces@2021-11-01' = {
  name: relayNamespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

resource hybridConnection 'Microsoft.Relay/namespaces/hybridConnections@2021-11-01' = {
  parent: relayNamespace
  name: hybridConnectionName
  properties: {
    requiresClientAuthorization: true
    userMetadata: 'Hermes Agent Microsoft Teams messaging relay (replaces dev tunnel)'
  }
}

resource listenRule 'Microsoft.Relay/namespaces/hybridConnections/authorizationRules@2021-11-01' = {
  parent: hybridConnection
  name: 'listen-only'
  properties: {
    rights: [
      'Listen'
    ]
  }
}

resource sendRule 'Microsoft.Relay/namespaces/hybridConnections/authorizationRules@2021-11-01' = {
  parent: hybridConnection
  name: 'send-only'
  properties: {
    rights: [
      'Send'
    ]
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2022-09-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {}
}

resource functionApp 'Microsoft.Web/sites@2022-09-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp'
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'RELAY_NAMESPACE'
          value: '${relayNamespaceName}.servicebus.windows.net'
        }
        {
          name: 'RELAY_HC_NAME'
          value: hybridConnectionName
        }
        {
          name: 'RELAY_SEND_KEYNAME'
          value: sendRule.name
        }
        {
          name: 'RELAY_SEND_KEY'
          value: sendRule.listKeys().primaryKey
        }
      ]
    }
  }
}

@description('The public messaging endpoint to set on the Azure Bot resource (Configuration > Messaging endpoint).')
output functionMessagingEndpoint string = 'https://${functionApp.properties.defaultHostName}/api/messages'

@description('Relay namespace host name, e.g. hermesteams-relay.servicebus.windows.net.')
output relayNamespaceHost string = '${relayNamespaceName}.servicebus.windows.net'

output hybridConnectionNameOutput string = hybridConnectionName

@description('Name of the Listen-scoped SAS rule. Fetch its key separately (it is not printed here) with the az command in the README.')
output listenRuleName string = listenRule.name

@description('Function App name, for `func azure functionapp publish`.')
output functionAppName string = functionApp.name
