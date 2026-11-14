// Alert rules (OBS-03, REL-06). Dead letters, Function failure rate, legacy availability, time to the review queue, and approvals stuck before the
// legacy write (guardrail G1). They notify one action group; add its email address with the alertEmailAddress parameter.
//
// The metric names and span names these queries use are the contract with the code: claims.legacy_submissions, claims.approved_stuck,
// the claims.intake consumer span and its claims.time_to_review_queue_s tag (src/ClaimsIntake.Api/Telemetry). They compile in CI. Whether
// each one fires on real telemetry can only be proven on Azure: docs/threat-model.md and the README list it as not verified.

param location string
param tags object
param applicationInsightsId string
param serviceBusId string

@description('Review queue name, for the dead-letter alert.')
param reviewQueueName string = 'claims-for-review'

@description('Resource id of the Event Grid system topic. Empty until the second stage of the blob trigger is deployed.')
param eventGridTopicId string = ''

@description('Email address that receives alerts. Empty creates the action group with no receiver, which is enough to see the alerts in the portal.')
param alertEmailAddress string = ''

@description('Percent of Function invocations or legacy writes that fail before the rule fires.')
@minValue(1)
@maxValue(100)
param failureRatePercent int = 5

@description('Seconds from a document being received to its claim waiting for review (success metric M5: p95 under 90 s).')
param timeToQueueSeconds int = 90

@description('Minutes an approved claim may wait before the legacy write counts as stuck. Must match ApiMetrics.StuckApprovalAfter.')
param stuckApprovalMinutes int = 10

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-claims-intake'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'claimsintk'
    enabled: true
    emailReceivers: empty(alertEmailAddress) ? [] : [
      {
        name: 'claims-ops'
        emailAddress: alertEmailAddress
        useCommonAlertSchema: true
      }
    ]
  }
}

// Any message in the dead-letter queue is a claim a person has not seen (ADR 0001). The run book is docs/runbook-dead-letters.md.
resource deadLetters 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-claims-dead-letters'
  location: 'global'
  tags: tags
  properties: {
    description: 'A message is in the dead-letter queue of the review queue. A claim is not waiting for a handler. See docs/runbook-dead-letters.md.'
    severity: 1
    enabled: true
    scopes: [
      serviceBusId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'DeadLetteredMessages'
          metricNamespace: 'Microsoft.ServiceBus/namespaces'
          metricName: 'DeadletteredMessages'
          dimensions: [
            {
              name: 'EntityName'
              operator: 'Include'
              values: [
                reviewQueueName
              ]
            }
          ]
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Maximum'
        }
      ]
    }
    autoMitigate: false
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

// An event the inbox could not deliver to the Function after its retries: a document that will not be processed.
resource eventGridDeadLetters 'Microsoft.Insights/metricAlerts@2018-03-01' = if (!empty(eventGridTopicId)) {
  name: 'alert-inbox-events-dead-lettered'
  location: 'global'
  tags: tags
  properties: {
    description: 'Event Grid could not deliver a blob-created event to the Function and wrote it to the dead-letter container. A document is not being processed.'
    severity: 1
    enabled: true
    scopes: [
      eventGridTopicId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'DeadLetteredCount'
          metricNamespace: 'Microsoft.EventGrid/systemTopics'
          metricName: 'DeadLetteredCount'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Total'
        }
      ]
    }
    autoMitigate: false
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

var functionFailureQuery = 'requests\n| where timestamp > ago(15m) and name == "ClaimDocumentReceived"\n| summarize total = count(), failed = countif(success == false)\n| where total > 0 and failed * 100.0 / total > ${failureRatePercent}'

var legacyUnavailableQuery = 'customMetrics\n| where timestamp > ago(15m) and name == "claims.legacy_submissions"\n| extend outcome = tostring(customDimensions["outcome"])\n| summarize total = sum(value), unavailable = sumif(value, outcome == "unavailable")\n| where total > 0 and unavailable * 100.0 / total > ${failureRatePercent}'

var timeToQueueQuery = 'requests\n| where timestamp > ago(30m) and name == "claims.intake"\n| extend seconds = todouble(customDimensions["claims.time_to_review_queue_s"])\n| summarize p95 = percentile(seconds, 95), n = count()\n| where n >= 5 and p95 > ${timeToQueueSeconds}'

var stuckApprovalsQuery = 'customMetrics\n| where timestamp > ago(15m) and name == "claims.approved_stuck"\n| summarize stuck = max(value)\n| where stuck > 0'

var queryAlerts = [
  {
    name: 'alert-function-failure-rate'
    displayName: 'Claims intake: Function failure rate above ${failureRatePercent}%'
    description: 'More than ${failureRatePercent}% of blob-triggered invocations failed in 15 minutes. Documents may be poisoned: check the Function logs by claim id.'
    severity: 2
    query: functionFailureQuery
    windowSize: 'PT15M'
  }
  {
    name: 'alert-legacy-unavailable'
    displayName: 'Claims intake: legacy system unavailable for more than ${failureRatePercent}% of writes'
    description: 'The legacy claims system did not answer more than ${failureRatePercent}% of approved-claim writes in 15 minutes. Approved claims are waiting.'
    severity: 2
    query: legacyUnavailableQuery
    windowSize: 'PT15M'
  }
  {
    name: 'alert-time-to-review-queue-p95'
    displayName: 'Claims intake: p95 time to the review queue above ${timeToQueueSeconds} s'
    description: 'Success metric M5: 95% of documents should be waiting for a handler within ${timeToQueueSeconds} seconds of arriving.'
    severity: 3
    query: timeToQueueQuery
    windowSize: 'PT30M'
  }
  {
    name: 'alert-approvals-stuck'
    displayName: 'Claims intake: approved claims not written to the legacy system after ${stuckApprovalMinutes} minutes'
    description: 'A claim was approved but is not in the legacy system (guardrail G1, REL-08). A handler can press Approve again; the write is idempotent.'
    severity: 2
    query: stuckApprovalsQuery
    windowSize: 'PT15M'
  }
]

resource queryRules 'Microsoft.Insights/scheduledQueryRules@2022-06-15' = [for alert in queryAlerts: {
  name: alert.name
  location: location
  tags: tags
  properties: {
    displayName: alert.displayName
    description: alert.description
    severity: alert.severity
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: alert.windowSize
    scopes: [
      applicationInsightsId
    ]
    criteria: {
      allOf: [
        {
          query: alert.query
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
  }
}]

output actionGroupId string = actionGroup.id
