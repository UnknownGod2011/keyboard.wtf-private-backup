# Data, privacy and paid entitlement policy

## Processing and retention

The Windows app sends an authenticated user request to the keyboard.wtf Cloud
Run API. The server validates the Firebase identity and device session, reserves
server-owned allowance, and calls Vertex AI using its Cloud Run service account.
No Vertex key, service-account key, Razorpay secret, authoritative price or
ledger write is given to the desktop or website.

Prompts, selected text, task-relevant page context, screenshots, images and audio
may be transmitted transiently when the user requests a feature that needs
them. Application logs must contain only structured IDs/hashes and operational
status—not prompt bodies, screenshots, audio, browser communications, tokens or
payment data. The backend stores usage totals, model/pricing version, provider
request identifiers, task category, timestamps and settlement state. It does
not need to retain raw prompts or media for billing.

Account deletion requires recent Google reauthentication. It removes Firebase
login access, desktop sessions/devices and direct profile fields. Financial
ledger, purchases, refunds and minimal usage/audit evidence remain under a
pseudonymous UID for accounting, fraud, dispute and legal obligations. The
website must disclose this retention before confirmation.

## Allowance rules (`allowance-v1`)

- Subscription allowance is a bucket for one paid billing cycle and expires at
  the authoritative cycle end. It is spent before non-expiring top-ups.
- One-time top-ups do not expire under the current policy.
- Administrative staging grants are separate, audited and disabled in
  production.
- A reservation records exact source-bucket allocations. Release restores only
  non-expired source allowance.
- A refund or chargeback reverses only the bucket tied to that Razorpay payment.
  If consumed already, it creates visible credit debt rather than taking an
  unrelated top-up or silently allowing a negative available balance.
- Failed, paused, halted, cancelled or merely authenticated subscriptions do not
  grant allowance. Each verified recurring payment ID may grant one cycle once.
- Legacy aggregate staging balance migrates once to an auditable non-expiring
  compatibility bucket. Production starts with no legacy balance.

Browser callbacks, local files, reinstalling, logout, a second device and client
reported token counts never create or reset allowance. The immutable ledger is
the financial source of truth; the account balance is a transactional materialized
view that must reconstruct from the ledger.
