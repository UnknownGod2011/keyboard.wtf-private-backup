# Paid staging operations and recovery

This document applies only to `keyboard-wtf-staging`. It does not authorize
Razorpay Live Mode or production deployment.

Staging administrator identities are deployment configuration, not source
data. Configure the explicit allowlist with the Cloud Run environment setting
`PaidSystem__AdminEmails__0` (and subsequent numbered entries if required).
Production rejects the staging credit-grant endpoint regardless of this list.

## Financial source of truth

- `accounts/{uid}/ledger` is append-only and is the source for reconstructing
  available and reserved credit. The account document is a transactional
  materialized view.
- Payment credit is keyed by Razorpay event and, for subscriptions, by the
  unique recurring payment ID. Browser callbacks never grant credit.
- Account subscription state is owned by one provider subscription ID. A
  historical cancelled subscription cannot deactivate a newer active one, and
  a different active subscription can take ownership only with a later verified
  billing-cycle end.
- Refund reconciliation keeps a stable provider-refund idempotency key across
  code revisions. Disputes reverse allowance only after Razorpay reports a
  `lost` status with a positive authoritative `amount_deducted`; ambiguous
  dispute transitions remain an operator reconciliation item.
- AI usage first reserves the complete approved task budget. Provider dispatch
  and completion evidence are written durably before settlement. A provider
  completion cannot be settled with different usage or pricing evidence.
- Ambiguous provider or payment states remain quarantined in
  `reconciliationIssues`; they are never silently refunded or credited.

## Automated maintenance

The backend image supports isolated Cloud Run Job entry points:

```text
--maintenance-job=payments
--maintenance-job=usage
--maintenance-job=invariants
--maintenance-job=provider-cost
--maintenance-job=all
```

Each job obtains a Firestore lease, writes a `maintenanceRuns` audit record and
is safe to run repeatedly. Payment reconciliation reads Razorpay Test Mode,
repairs only provider-authoritative state and quarantines identity/amount
mismatches. Usage reconciliation settles durable provider completions, releases
only reservations that provably never dispatched, and quarantines ambiguous
dispatches. The invariant job reconstructs balances from the ledger and checks
cross-account payment ownership.

Provider-cost reconciliation is a separate, low-frequency audit. Realtime
allowance settlement continues to use Vertex `usageMetadata`; each settlement
also increments the existing per-day global usage shard with its server-priced
provider cost. The maintenance job compares that bounded sharded total with a
closed UTC day from the detailed Google Cloud Billing export in BigQuery. It
records the aggregate variance and opens an audited issue when both the
absolute and percentage thresholds are exceeded. It never changes an
individual balance.

Keep `PaidSystem__ProviderCostReconciliationEnabled=false` until the detailed
billing export is active and contains only the intended billing account data.
Then configure a fully qualified table through
`PaidSystem__BillingExportTable=project.dataset.table`, grant the maintenance
runtime `bigquery.jobs.create` on the query project and read-only access to that
dataset, and enable the job. The query is parameterized, limited to the local
GCP project, `Vertex AI`, and a UTC day older than the configured billing lag;
it also has a server-owned maximum-bytes-billed ceiling.
Enable it only after the provider-cost shard writer has been deployed for at
least that lag period so a rolling deployment cannot create a partial day.

The staging Cloud Scheduler job `keyboard-wtf-paid-maintenance-hourly` invokes
the Cloud Run Job on an hourly cadence with a dedicated service account that
has only Cloud Run invoker permission on that job. Operators can run an
additional audited pass without changing production configuration:

```powershell
gcloud run jobs execute keyboard-wtf-paid-maintenance `
  --region asia-south1 --project keyboard-wtf-staging --wait
```

The maintenance job is intentionally not an automatic financial correction
engine. An ambiguous payment, provider dispatch, refund, or ownership mismatch
remains quarantined until an operator verifies provider evidence and makes an
append-only adjustment.

## Monitoring and alerts

Staging has log-based metrics for backend failures, rejected Razorpay webhook
signatures, financial warnings and maintenance-job failures. The deployment
script `scripts/staging/configure-monitoring.ps1` creates alert policies only
when passed a verified Cloud Monitoring notification-channel resource. This
prevents silently routing financial alerts to an unapproved destination.

Before Live Mode is considered, create and verify a monitored destination, then
run:

```powershell
scripts/staging/configure-monitoring.ps1 `
  -ProjectId keyboard-wtf-staging `
  -NotificationChannel projects/keyboard-wtf-staging/notificationChannels/CHANNEL_ID
```

Alert payloads must stay free of prompts, bearer tokens, card data, provider
credentials, and user page content.

## Backup and retention

- Firestore point-in-time recovery is enabled for the default staging database.
- A daily scheduled Firestore backup is retained for seven days and a weekly
  backup for fourteen weeks when supported by the selected database edition.
- Financial ledger, account-cycle aggregate, allowance-bucket, reservation,
  idempotency, purchase, payment-event, reconciliation and audit records have no
  TTL policy.
- Only `accounts/{uid}/usage/{requestId}` itemized Recent Usage details carry the
  `detailExpiresAt` TTL field. New settled details expire thirty days after the
  later of settlement or the account's current cycle end. Legacy settled details
  are backfilled conservatively to sixty-one days after settlement. Pending,
  ambiguous and quarantined operations never receive this field.
- Removing a settled usage-detail document is display-history cleanup only. The
  ledger, account cycle total, allowance buckets, daily/global aggregates,
  reservation settlement and idempotency evidence remain authoritative and
  online.
- Quarterly, restore the newest backup into an isolated staging recovery
  project, run `--maintenance-job=invariants`, and record the result. Never test
  restore by overwriting the active database.

## Recovery sequence

1. Activate the AI or payment kill switch in `config/catalog` if correctness is
   uncertain. This blocks new work but does not discard existing state.
2. Preserve Cloud Run and Firestore audit logs; never log prompts, bearer tokens,
   card data or provider secrets.
3. Run payment reconciliation, usage reconciliation, invariants, and the
   provider-cost audit.
4. Resolve only quarantined records with provider dashboard evidence. Record any
   manual adjustment as a new ledger event; never edit old ledger documents.
5. Re-enable work only when the invariant report is clean and alerting is green.

## Live audio limitation

Vertex Live sessions reserve a hard server budget, have a maximum duration and
traffic limit, checkpoint measured usage, and are closed at the budget boundary.
When exact modality usage is absent, the server estimates native audio at 25
tokens per second from measured PCM traffic and applies the configured safety
margin. Live paid production must remain disabled until recorded staging usage
is compared with delayed Google Cloud billing data and the observed variance is
within the approved safety margin.

If a provider setup has already been dispatched but supplies no authoritative
usage metadata, the reservation is quarantined instead of released. This keeps
the account and financial ledger safe after cancellation, disconnect, timeout,
or an incomplete provider response.
