// Log Analytics workspace and a workspace-based Application Insights component.
// Ingestion needs Entra ID (DisableLocalAuth), so every exporter signs in with its managed identity and holds Monitoring Metrics
// Publisher on this component (modules/roles.bicep). The instrumentation key in the connection string alone no longer works.

param logAnalyticsName string
param applicationInsightsName string
param location string
param tags object

@description('Days to keep logs and telemetry. Telemetry must not hold claim content or personal data (docs/adr/0010-retention.md).')
@minValue(30)
@maxValue(730)
param retentionInDays int = 30

@description('Reject ingestion that is not signed in with Entra ID. Leave true: the Function, the API and the watcher all use a credential.')
param disableLocalAuth bool = true

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    features: {
      disableLocalAuth: true
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    RetentionInDays: retentionInDays
    DisableLocalAuth: disableLocalAuth
  }
}

output logAnalyticsId string = logAnalytics.id
output applicationInsightsName string = applicationInsights.name
output applicationInsightsId string = applicationInsights.id
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
