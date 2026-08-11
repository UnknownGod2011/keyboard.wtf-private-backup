# Launch subscription economics

Validated on 2026-08-11. This document is a launch guardrail, not a promise of
unlimited usage or a replacement for billing reconciliation.

## Launch catalog

- Product: `Jarvis Monthly`
- SKU: `monthly_standard_inr_999_v1`
- Price: INR 999 per month
- Included provider-cost allowance: 350 credits
- Maximum modeled provider cost: INR 350
- Provider-cost share of gross price: 35.04%
- Catalog version: `launch-monthly-inr999-v1`
- Pricing version: `vertex-standard-global-fx100-2026-08-11`
- Fixed accounting conversion: INR 100 per USD
- Top-ups: disabled for initial public launch
- Checkout: disabled until Live credentials, a real Razorpay plan ID, and an
  explicitly approved canary are present

Production validation rejects enabled products above a 40% provider-cost share.
The fixed FX value is intentionally more conservative than the current market
rate and is versioned server-side rather than calculated by the browser.

## Official prices represented by the pricing version

The server routes ordinary work to Standard PayGo Gemini 3.5 Flash-Lite at the
global endpoint: USD 0.30 per million input tokens, USD 0.03 per million cached
input tokens, and USD 2.50 per million response/reasoning tokens. Complex work
may use Gemini 3.5 Flash at USD 1.50 input, USD 0.15 cached input, and USD 9.00
output per million tokens. Gemini 2.5 Flash Live is priced at USD 0.50 per
million input text tokens, USD 3.00 input audio/image tokens, USD 2.00 output
text tokens, and USD 12.00 output audio tokens.

Primary source: https://cloud.google.com/gemini-enterprise-agent-platform/generative-ai/pricing

The Live API reports tokens used by the session so far, so accounting retains
monotonic per-modality high-water marks instead of summing repeated snapshots.
Live is server-to-server, has a default ten-minute maximum session, and bills
accumulated session context on later turns. Transcription output is billable.

Primary source: https://docs.cloud.google.com/gemini-enterprise-agent-platform/reference/models/multimodal-live

## Deterministic 30-day simulation

Run:

```powershell
.\scripts\production\simulate-plan-economics.ps1
```

The simulation deliberately uses conservative fixed traffic shapes. Live audio
is modeled at 32 input-audio and 25 output-audio tokens per second for planning;
real charging always uses provider `usageMetadata` when available.

| Profile | Fast | Strong | Browser | Screen | Live minutes | Provider cost (INR) | Fits allowance |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Light | 200 | 10 | 10 | 3 | 5 | 44.23 | Yes |
| Normal | 600 | 30 | 60 | 15 | 30 | 182.43 | Yes |
| Heavy | 3,000 | 200 | 400 | 120 | 180 | 1,106.18 | No |
| Live-voice-heavy | 600 | 30 | 30 | 10 | 600 | 1,523.85 | No |
| Browser/screen-heavy | 600 | 60 | 1,800 | 300 | 30 | 952.08 | No |
| Abuse | 10,000 | 2,000 | 5,000 | 2,000 | 2,000 | 10,997.00 | No |

The allowance therefore supports the modeled normal user with headroom. Heavy
users stop at the server-owned allowance; they do not create unlimited provider
exposure. This must be recalibrated from closed-window provider billing before
Live payments are enabled and whenever a model, price, FX policy, or routing
version changes.

## Customer-visible behavior

- A new account has zero usable allowance.
- A verified monthly charge creates one cycle bucket exactly once.
- The UI shows total usable credits separately from monthly percentage.
- Temporary reservations do not look like permanent consumption.
- Only settled provider-backed usage reduces the monthly percentage.
- A verified renewal payment creates the next cycle bucket once.
- Failed, authenticated, paused, halted, or cancelled subscription state alone
  never grants allowance.
