# Chrome Web Store submission checkpoint

This directory prepares the keyboard.wtf Chrome Helper for review. It does not
authorize submitting or publishing the item.

## Fixed item identity

- Permanent Store ID: `gojdebdikepkpapmnggeljneamjdigbh`
- Initial visibility: **Unlisted**
- Publishing: **Deferred publishing**
- Store URL:
  `https://chromewebstore.google.com/detail/gojdebdikepkpapmnggeljneamjdigbh`

The Store ZIP intentionally has no manifest `key`. Chrome Web Store signs it
under the permanent item ID above. The source manifest retains a development
key so the unpacked fallback keeps its stable development identity.

## Local deliverables

- Store ZIP:
  `dist/chrome-web-store/keyboard-wtf-chrome-helper-2.1.23.zip`
- Store listing: `store/STORE_LISTING.md`
- Dashboard fields: `store/STORE_DASHBOARD_FIELDS.md`
- Privacy disclosures: `store/PRIVACY_DISCLOSURES.md`
- Public privacy-page draft: `store/PRIVACY_POLICY_PUBLIC_DRAFT.md`
- Reviewer instructions: `store/REVIEWER_TEST_INSTRUCTIONS.md`
- Support copy: `store/SUPPORT_AND_TROUBLESHOOTING.md`
- Submission checklist: `store/SUBMISSION_CHECKLIST.md`
- Asset provenance: `store/ASSET_PROVENANCE.md`
- Assets: `store/assets/`

## Approval boundary

The owner has authorized uploading this package to the existing Store item and
submitting the update for review. Do not create a new listing or change the
existing item identity.

Before submission, verify the privacy-policy and support URLs listed in the
dashboard fields are publicly reachable and use the matching Windows 1.4.5
companion build.
