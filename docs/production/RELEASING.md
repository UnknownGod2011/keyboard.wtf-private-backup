# Releasing keyboard.wtf safely

This is an operator checklist, not an automated permission to publish. A paid
release remains blocked until every applicable item in `GO_LIVE_GATES.md` is
closed and a human explicitly approves the production operation.

## 1. Prepare one immutable candidate

1. Work only on the intended private release branch and review the complete
   diff against its approved base.
2. Bump the desktop/installer version and add plain-text release notes.
3. Require a clean committed worktree. Record the commit SHA.
4. Run a locked restore, Release build, unit tests, the real isolated Firestore
   gate, frontend/extension syntax checks, secret scan, and transitive
   vulnerability scan.
5. Run `scripts/production/configure-runtime-identity.ps1` for the production
   project and verify the Cloud Run runtime has
   `roles/firebaseauth.viewer`. This read-only permission is required by the
   server's revocation-aware Firebase ID-token validation; do not work around a
   missing role by disabling `checkRevoked`.
6. Verify staging payment reconciliation, usage reconciliation, ledger
   invariants, kill switches, backups, monitoring, and the release gates.

## 2. Build and authenticate the candidate

```powershell
.\scripts\build-windows-release.ps1 -SkipSiteHashUpdate
.\scripts\release\generate-sbom.ps1 `
  -BuildDirectory .\dist\release\keyboard-wtf-win-x64 `
  -Version VERSION
```

For a local paid-staging installer gate, use
`build-windows-release.ps1 -SideBySideStaging`. It emits a distinct app ID,
installation directory, uninstaller registration, and filename, and it does
not register itself for Windows startup. It therefore cannot replace the
stable installation. Never distribute that staging artifact as the public
installer.

Generate the signed update manifest with the offline private key exactly as
documented in `RELEASE_SIGNING.md`. The private key must stay outside the clone,
CI, command logs, artifacts, and application. Record the installer SHA-256,
byte size, SBOM digest, source commit, build-tool versions, and manifest digest.

## 3. Validate Beta before Stable

1. Publish only to the approved private/staging Beta destination.
2. Verify the uploaded bytes match the locally tested installer hash.
3. Install side-by-side on a clean Windows test profile; do not overwrite the
   known-good production installation during validation.
4. Exercise install, launch, Google sign-in, paid-account refresh, hotkeys,
   browser helper reconnect, AI debit, cancellation, restart, update consent,
   rollback, and uninstall.
5. Observe a bounded canary and confirm no payment, authentication, balance,
   crash, latency, or privacy regression.

Stable promotion reuses the exact Beta-validated installer bytes. Never rebuild
between validation and promotion. Ordinary feature updates require explicit
user consent; do not silently install or automatically downgrade users.

## 4. Publish or stop

Before publication, confirm production and staging projects, databases,
domains, secrets, catalogs, OAuth settings, Razorpay mode, backups, alerts, and
kill switches are distinct and correct. Confirm business approval for pricing,
tax, invoices, renewals, cancellations, refunds, and support obligations.

If any artifact, signature, deployment revision, source SHA, environment, or
manual gate differs from the approved evidence, stop. Preserve the evidence and
follow `INCIDENT_RESPONSE.md`; do not improvise a replacement release.
