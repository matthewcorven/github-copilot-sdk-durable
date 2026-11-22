// Log Analytics + Application Insights.
//
// A hosted agent's stdout is only reachable through the platform
// (`azd ai agent monitor`), which reads the Application Insights resource the
// project is linked to. Without these two resources a deployed run is
// unobservable, which is the opposite of what D1 is supposed to produce.
// Application Insights requires a Log Analytics workspace to exist first.
param logAnalyticsName string
param appInsightsName string
param location string
param tags object = {}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

output appInsightsName string = appInsights.name
output appInsightsResourceId string = appInsights.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
output logAnalyticsWorkspaceId string = logAnalytics.id
