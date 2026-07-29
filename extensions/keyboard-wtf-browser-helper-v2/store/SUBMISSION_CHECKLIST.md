# Chrome Web Store submission checklist

## Package

- [ ] Version is 2.1.21 in manifest, service worker, content script, and app.
- [ ] ZIP contains no manifest key.
- [ ] ZIP contains no API key, token, log, browser data, or user data.
- [ ] JavaScript syntax validation passes.
- [ ] Manifest validation passes.
- [ ] Permanent Store ID is allowed by the native host.
- [ ] Unpacked development ID remains a labelled fallback only.

## Assets and listing

- [ ] 128×128 icon is uploaded.
- [ ] Three 1280×800 screenshots are uploaded and contain no private data.
- [ ] 440×280 small promo tile is uploaded.
- [ ] Name, summary, description, category, support, and homepage are complete.

## Privacy

- [ ] Public privacy-policy URL is live.
- [ ] Public support URL is live.
- [ ] Dashboard data categories match `PRIVACY_DISCLOSURES.md`.
- [ ] Every permission has a specific justification.
- [ ] Remote code is declared No.
- [ ] Limited Use and data-use attestations are accurate.

## Reviewer access

- [ ] Matching Windows reviewer build is available at a stable HTTPS URL.
- [ ] Native Messaging registration works after a clean install.
- [ ] Reviewer instructions require no credentials.
- [ ] Safe Wikipedia and cancellation tests pass.

## Release validation

- [ ] Fresh Windows install passes.
- [ ] Upgrade from the previous installer passes.
- [ ] Jarvis starts after Windows restart.
- [ ] Hotkeys work after restart.
- [ ] Store Helper reconnects after Jarvis and Chrome restarts.
- [ ] Browser automation uses the existing Chrome session.
- [ ] Manual fallback instructions work and are clearly labelled.
- [ ] Restart starts exactly one Jarvis instance.
- [ ] Uninstall invokes the registered uninstaller.
- [ ] Preserve-data and remove-data choices are both tested.

## Approval gates

- [ ] User approved Store upload.
- [ ] User approved Submit for Review.
- [ ] Visibility is Unlisted.
- [ ] Deferred publishing is enabled.

Stop at any unchecked release blocker. Saving a draft is not approval to submit
or publish.
