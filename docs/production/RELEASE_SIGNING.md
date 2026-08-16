# Release signing and updater trust

keyboard.wtf update manifests are authenticated with ECDSA P-256 and SHA-256.
The desktop embeds only public keys. The current key ID is
`kwt-release-2026-01`; its private key must remain outside the repository,
GitHub Actions, build artifacts, logs, and the installed application.

The signed payload binds the channel, version, minimum compatible version,
publication time, approved GitHub Release URL, installer SHA-256 and byte size,
release-notes digest, and signing-key ID. The updater rejects unknown keys,
modified metadata, downgrades/replays, stale manifests, non-HTTPS downloads,
unapproved hosts, redirect escapes, oversized/incomplete packages, and hash
mismatches. An update is downloaded only after manifest verification and is
installed only after a user explicitly chooses Install.

## Signing a candidate

Build and test a clean commit first. Keep the private key at a restricted path
outside the clone. Then run:

```powershell
dotnet run --project tools/KeyboardWtf.ReleaseSigner -c Release -- `
  --package dist/installer/keyboard-wtf-setup.exe `
  --output staging/site/releases/stable.json `
  --private-key "$env:USERPROFILE/.keyboard-wtf-release-signing/kwt-release-2026-01-private.pem" `
  --release-notes docs/releases/1.4.0.md `
  --channel stable `
  --version 1.4.0 `
  --minimum-version 1.2.0
```

The signer derives the public key and verifies the completed manifest before
writing it atomically. Review the manifest, installer hash, CI provenance and
SBOM before publishing. Never sign an artifact rebuilt after Beta validation.

## Rotation and compromise

For normal rotation, ship a desktop version containing both the old and new
public keys, wait for adoption, sign with the new key ID, and remove the old key
only in a later release. For suspected private-key compromise, stop publishing
updates, remove the compromised key from the feed and next trusted desktop,
preserve GitHub/audit evidence, rotate repository and release credentials, and
distribute the recovery build through an independently verified installer path.
Changing an installer hash without a valid non-revoked manifest signature must
never be accepted as recovery.
