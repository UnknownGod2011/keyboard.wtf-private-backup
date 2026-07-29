# Chrome Web Store privacy disclosures

This is the source-of-truth worksheet for the Privacy tab. The public privacy
policy must say the same thing before the item is submitted.

## Single purpose

Connect the locally installed keyboard.wtf Windows assistant to Chrome tabs the
user asks Jarvis to control, so Jarvis can understand visible page state and
perform the requested browser actions.

## Data categories to disclose

Select the dashboard categories corresponding to:

- **Personally identifiable information:** only when it is present in the
  user-selected page/task, including a name, account identifier, address or
  other identifying content.
- **Web history / browsing activity:** current controlled-tab URL and title.
- **Website content:** relevant visible page text, accessible control metadata,
  selected state, and task-scoped screenshots when visual recovery is needed.
- **User activity:** browser actions and compact before/after action receipts.
- **Form data / user-generated content:** values the user asks Jarvis to fill,
  type, copy, summarize, or paste.
- **Personal communications:** only when the user explicitly asks Jarvis to
  read or draft content in a communications site.
- **Location:** only when a location is present in the user request or visible
  page; the extension does not request Chrome geolocation.
- **Health information:** only when health information is present in the
  user-selected page/task; there is no health-system permission or integration.
- **Financial and payment information:** public prices, cart state or other
  visible financial context may be processed for the task. Payment-card,
  banking, checkout and order-placement steps stay manual.

The helper can encounter personal data present in page content. It does not
build a profile, sell data, or use the data for advertising.

## Data explicitly excluded from automation

- Passwords.
- One-time passwords.
- CAPTCHA solutions.
- Payment-card or banking information.
- Cookie storage, raw Chrome profile data, password values and file contents.
- Background browsing data unrelated to a user-requested task.

The extension does not call cookie or browser-storage APIs. A website can place
a token in its URL or render sensitive data as ordinary visible page content.
The local app receives the full controlled-tab URL; common token/key/code/
signature/password/session parameter values are redacted before that URL is
sent to Gemini.

## Data flow and storage

1. The extension sends task-scoped state to the locally installed keyboard.wtf
   app through Chrome Native Messaging.
2. The extension contains no API keys and does not contact a keyboard.wtf
   analytics or advertising server.
3. The current browser planner sends a bounded task snapshot to the
   user-configured Google Gemini API when AI page understanding is needed. This
   includes the original request, up to 8,500 characters of visible page text,
   relevant ranked control metadata, recent task evidence and a redacted URL.
   A current-viewport screenshot is sent only for visual recovery.
4. Local diagnostics retain compact task status, bounded target labels and
   errors until the user deletes the log/local data. There is currently no
   automatic log expiry. They do not intentionally contain raw cookies,
   credentials, full page dumps, typed values or screenshots.
5. Browser workflow memory is disabled in this release.

## Required dashboard attestations

- Data is not sold to third parties.
- Data is not used or transferred for purposes unrelated to the item’s single
  purpose.
- Data is not used or transferred to determine creditworthiness or for lending.
- Data is not used for personalized advertising.
- Human access is not allowed except with explicit user consent for support,
  for security investigation, when legally required, or when aggregated and
  anonymized for internal operations.
- Information received from Google APIs is handled according to the Chrome Web
  Store User Data Policy, including Limited Use requirements.

## Permission justifications

### nativeMessaging

Connects the extension to the locally installed keyboard.wtf app. The native
host allowlist accepts only the permanent Store ID and the documented unpacked
development fallback ID.

### alarms

Wakes the Manifest V3 service worker periodically so it can restore its local
native connection after Chrome suspends it.

### tabs

Identifies and retains the exact user-authorized tab, opens or activates
task-owned tabs in the same Chrome window, observes navigation, captures a
controlled-tab screenshot when required, and prevents duplicate task tabs.

### scripting

Restores the packaged content script on normal HTTP/HTTPS pages or frames after
navigation or service-worker recovery. It does not download remote code.

### webNavigation

Enumerates frames in the controlled tab so forms and editors embedded in
iframes can be observed and acted on correctly.

### Host access: http://*/* and https://*/*

Users may ask Jarvis to automate any website they choose, so a fixed site list
would break the extension’s single purpose. Page scans and actions occur only
for an active user-requested task. Chrome-protected pages are not automated.

## Remote code

No. The extension executes only JavaScript, HTML, CSS, and images packaged in
the uploaded ZIP.
