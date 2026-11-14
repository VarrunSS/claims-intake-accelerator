# Runbook: dead-lettered claims and events

Alert: "A message is in the dead-letter queue of the review queue" (or "inbox events dead-lettered"). Every dead letter is a document a
handler has not seen. Retention: review and purge within 14 days (ADR 0010). Never run on Azure.

1. Open the Service Bus namespace, queue `claims-for-review`, Dead-letter. Read the dead-letter reason (`InvalidProposedClaim` means the
   message was not a valid claim; `MaxDeliveryCountExceeded` means the API failed five times; `TTLExpiredException` means nobody read it
   for 7 days: check readiness, `/health/ready` reports the consumer).
2. Find the claim by its message id (the claim id) in Application Insights: one trace spans the Function, the queue and the API.
3. Fix the cause. To resubmit, copy the message body to the queue again (the id makes it idempotent after the duplicate-detection window)
   or re-drop the original blob in `inbox`.
4. Purge the dead letter once resolved. The body holds full document text: do not copy it into tickets or chat.
5. Event Grid dead letters are blobs in `eventgrid-deadletter` on the host storage account. Each names the blob that was not delivered:
   re-drop it, then delete the dead-letter blob.
