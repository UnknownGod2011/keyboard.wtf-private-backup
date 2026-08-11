# Paid production gates

Current status: **NOT READY**. Staging implementation can advance without
enabling Razorpay Live or replacing the stable installed Jarvis.

## Automated gates

- Clean locked restore, Release build and complete tests.
- Dependency vulnerability and tracked-source secret scans.
- Signed-updater tamper/replay/downgrade tests and offline manifest signing.
- Payment idempotency, entitlement buckets, account isolation, concurrency,
  reservation settlement/reconciliation and ledger invariants.
- Frontend/extension syntax checks, packaged side-by-side desktop smoke, SBOM and
  source/artifact provenance.
- Mock-provider load/soak results with no leaked sessions or reservations.

## External/manual gates

- Separate production Firebase/GCP/Firestore/Cloud Run/Vercel environment.
- External-user Google OAuth verification and account deletion on a synthetic
  production user.
- Razorpay business/KYC approval, Live keys in Secret Manager, Live webhook,
  real SKUs/amounts, one explicitly approved minimal canary and refund.
- Business/CA decision on GST, tax inclusion, receipts/invoices, renewal and
  cancellation disclosures.
- Company-controlled monitoring destination with a received test alert.
- Firestore backup/PITR configuration and isolated restore drill.
- Vertex Live calibration against delayed Cloud billing and an approved safety
  margin.
- A detailed Cloud Billing export table, least-privilege BigQuery access, one
  completed closed-day provider-cost reconciliation, and a received harmless
  provider-cost drift alert.

No public Live checkout or market-launch status is allowed while any external
gate is incomplete.
