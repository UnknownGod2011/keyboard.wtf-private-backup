# keyboard.wtf production candidate

This directory contains the isolated production website candidate. It is not
the public keyboard.wtf website and must remain fail-closed until the owner-only
real-money canary is approved.

Current production contract:

- Firebase project: `keyboard-wtf-prod`
- Cloud Run region: `asia-south1`
- Website: `https://keyboard-wtf-production-candidate.vercel.app`
- Plan: ₹999 monthly
- Monthly provider-backed allowance: 500 credits
- Browser checkout success is never authoritative
- Razorpay webhooks and provider reconciliation are authoritative
- AI and payment kill switches remain enabled before the canary

The website contains public Firebase web configuration only. Razorpay secrets,
webhook secrets, Firebase private credentials, and provider credentials belong
in Secret Manager and must never be added to this directory.
