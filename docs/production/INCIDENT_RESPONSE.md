# keyboard.wtf paid-system incident response

This runbook applies to paid production. Preserve provider IDs, immutable ledger
records, Cloud Run revision/digest, release provenance and audit logs. Never copy
prompts, audio, screenshots, bearer tokens, card data or secrets into tickets.

## Immediate containment

1. Enable the narrowest server kill switch: payments, AI, Live, model route, or
   rollout admission. Do not delete records or edit old ledger entries.
2. Freeze deployment and release publication. Record UTC time, source commit,
   Cloud Run revision/image digest and the operator initiating containment.
3. Preserve Cloud Logging, Razorpay event/payment IDs, Vertex request IDs,
   reconciliation reports and signed-release metadata.
4. Reconcile before re-enabling. Corrections are new audited ledger entries,
   never rewrites of historical entries.

## Credential incidents

- Razorpay key: disable checkout, rotate the API key in Razorpay and Secret
  Manager, deploy a new revision, verify webhook retrieval and reconcile all
  payments since the earliest possible exposure.
- Webhook secret: disable payment fulfilment, rotate the secret, reject the old
  signature immediately, replay verified provider events through reconciliation.
- Firebase/Google credential: disable new sessions and AI, revoke affected
  credentials/service-account permissions, revoke desktop sessions, rotate, and
  inspect authentication anomalies without logging tokens.
- Release signing key: stop update feeds, revoke the key ID, preserve release and
  GitHub audit evidence, rotate through an independently trusted desktop path;
  follow `RELEASE_SIGNING.md`.

## Financial correctness incidents

- Duplicate/missing credits: disable payment fulfilment, run payment
  reconciliation and ledger invariants, quarantine ambiguous ownership or
  amount mismatches, and correct with an idempotent adjustment linked to the
  provider payment/refund ID.
- Incorrect AI charge: disable the affected model route, preserve provider usage
  evidence and pricing version, run reservation reconciliation, and adjust only
  after the billable provider outcome is known.
- Refund/chargeback: stop spending from the payment-specific allowance bucket,
  record debt when already consumed, and never debit unrelated top-ups.
- Runaway Vertex spend: activate global AI and Live kill switches, lower rollout
  capacity, disable the route, inspect per-user/global sharded counters and
  provider usage, then rotate credentials if abuse is suspected.

## Availability and data incidents

- Model outage: disable the route or canary, use only a preconfigured compatible
  fallback with explicit pricing, and never retry an ambiguous billable request
  with a new request ID automatically.
- Backend outage: keep checkout success in Processing until provider-authoritative
  reconciliation succeeds; never trust the browser callback as fulfilment.
- Firestore deletion/corruption: stop writes, restore the newest verified backup
  into an isolated recovery project, run payment/usage reconciliation and ledger
  invariants, compare counts/hashes, then plan a reviewed cutover. Never restore
  over the active database during diagnosis.
- Bad desktop update: stop the signed feed, move rollout to zero, publish a newer
  signed rollback/fix (never a downgrade), and verify package/source provenance.

## Recovery gate

Re-enable only when: kill-switch reason is understood; affected secrets are
rotated; reconciliation backlog is zero or explicitly quarantined; ledger
invariants pass; one synthetic transaction/request succeeds; monitoring is
green; and an incident owner records scope, timeline, root cause, corrective
changes and remaining user remediation.
