# keyboard.wtf Chrome Helper privacy policy

Public preview source: `site/privacy.html`
Planned URL: https://keyboard-wtf.vercel.app/privacy
Last updated: July 29, 2026

## Scope and single purpose

The keyboard.wtf Chrome Helper has one purpose: connect the locally installed
keyboard.wtf Windows assistant to Chrome tabs the user explicitly asks Jarvis
to control. It supports reading a selected page, grounding visible controls,
performing requested browser interactions, retaining stable task-owned tabs,
showing a controlled-tab indicator, verifying results, and cancelling safely.

It does not act on a page when there is no active Jarvis browser task.

## Information processed

For an active browser task, the helper can process:

- the controlled tab's full URL/title, tab/window IDs, navigation and task-tab
  state;
- bounded visible page text and selected text;
- visible control roles, accessible names, labels, types, enabled/selected/
  checked state, bounded non-restricted values, safe actions, frames and bounds;
- the user's task and text requested for typing, copying, summarizing or pasting;
- compact before/after action evidence, fingerprints, verification, retries,
  errors and cancellation; and
- a current-viewport screenshot when visual recovery or task evidence requires
  one.

This information can include personally identifiable information, personal
communications, health/location/financial context, or other sensitive content
when it is present in a task or page deliberately selected by the user.

## Sensitive categories and exclusions

- The extension has no geolocation, cookies, downloads, Chrome storage or
  clipboard permission.
- It does not call `document.cookie`, Chrome cookie APIs, local storage or
  session storage.
- It may see visible login state or account names, but password values are
  redacted and cannot be typed/clicked by the executor.
- OTPs, CAPTCHAs, banking, payment, checkout, order placement and
  account-security steps are manual.
- It does not read file names, paths or bytes and cannot select/upload a file.
- It does not intentionally collect session/authentication tokens. Because a
  site may put a token in a URL or visible page, the local app can receive that
  raw URL/page snapshot. Common token, key, code, signature, password and
  session parameter values are redacted from the URL before it is sent to the
  AI provider.

## Data transfer

The extension transfers task-scoped data to the keyboard.wtf desktop app on the
same Windows account through Chrome Native Messaging. It contains no API key
and does not contact a keyboard.wtf advertising or analytics server.

For normal paid users, the browser planner sends the original request, a
bounded page snapshot, relevant ranked controls, recent task evidence, and a
redacted URL through the authenticated keyboard.wtf backend to the configured
Google Vertex AI model when AI understanding is needed. A current-viewport
image is included only for visual recovery. Google Cloud's terms and retention
policy apply to data sent to Vertex AI.

On keyboardwtf.com and its documented Vercel fallback, the website may ask the
helper for a connection-status handshake. The response contains only helper
installation/version, the fixed extension identity, and whether a current live
native connection to Jarvis and the active page exists. It does not include the
page URL, title, text, controls, screenshot, account identity, or browsing
history.

## Storage and retention

- The extension does not store a browsing history, page dump, screenshot
  archive, cookie or credential.
- Active snapshots, screenshots, receipts and prepared text stay in process
  memory for the task and are not written as workflow memory in this release.
- Browser settings and consent version remain in
  `%LOCALAPPDATA%\keyboard.wtf\settings.json` until changed/deleted.
- Compact diagnostic metadata remains in
  `%LOCALAPPDATA%\keyboard.wtf\logs\keyboard.wtf.log` until the user deletes the
  log/local data. There is currently no automatic log expiry. Browser log
  entries do not intentionally include full page text, screenshots, cookies,
  credentials or typed values.
- Requested clipboard output remains in the Windows clipboard until replaced.
  Temporary editor-paste use attempts to restore the previous clipboard.

## Use, sale and Limited Use

Browser-task data is used only to provide, secure, debug and verify the
user-requested browser task. It is not sold or used for personalized
advertising, creditworthiness/lending, unrelated profiling, tracking or other
unrelated purposes.

Human access is not allowed except with explicit user consent for support, for
a necessary security investigation, when legally required, or after data is
aggregated and anonymized for internal operations.

Use and transfer of information received from Google APIs complies with the
Chrome Web Store User Data Policy, including Limited Use requirements.

## Security and user controls

- Browser automation is disabled by default and requires a prominent
  disclosure and affirmative consent before the first task.
- The native host allowlist accepts only the permanent Store ID and verified
  fixed unpacked fallback ID.
- If both helpers are enabled, commands fail closed until one is disabled.
- Tasks use stable tab ownership, fresh page epochs, semantic re-resolution,
  serialized mutations, bounded retries, evidence and cancellation checks.
- Users can cancel with Ctrl+Alt+X, disable Browser Automation, withdraw
  consent, remove the extension, delete the local log, or delete all
  `%LOCALAPPDATA%\keyboard.wtf` data.

## Contact

- keyboard.wtf@gmail.com
- tanushshah2006@gmail.com
