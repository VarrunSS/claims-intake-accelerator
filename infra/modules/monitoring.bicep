// Log Analytics workspace and a workspace-based Application Insights component.

param logAnalyticsName string
param applicationInsightsName string
param location string
param tags object

@description('Days to keep logs. Logs must not hold claim content or personal data (see docs/00-discovery.md).')
@minValue(30)
@maxValue(730)
param retentionInDays int = 30

@description('Set to true once every exporter signs in with Entra ID (Milestone 6). Until then ingestion needs the connection string.')
param disableLocalAuth bool = false

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
    DisableLocalAuth: disableLocalAuth
  }
}

output logAnalyticsId string = logAnalytics.id
output applicationInsightsName string = applicationInsights.name
output applicationInsightsId string = applicationInsights.id
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
