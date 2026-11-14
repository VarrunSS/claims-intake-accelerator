// The Application Insights workbook (OBS-03): latency, model tokens, pounds per claim and failure rate. The workbook body is
// infra/workbooks/claims-intake.workbook.json, so it can be edited in the portal editor and pasted back as JSON.

param location string
param tags object
param applicationInsightsId string

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, 'claims-intake-workbook')
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'Claims intake: latency, tokens, cost and failures'
    category: 'workbook'
    sourceId: applicationInsightsId
    version: 'Notebook/1.0'
    serializedData: replace(loadTextContent('../workbooks/claims-intake.workbook.json'), '__APPLICATION_INSIGHTS_ID__', applicationInsightsId)
  }
}

output id string = workbook.id
