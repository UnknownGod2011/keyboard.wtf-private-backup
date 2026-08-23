# Chrome Web Store reviewer test instructions

The extension is a companion for the keyboard.wtf Windows desktop app. It does
not perform browser actions without that app’s registered Native Messaging
host.

## Prerequisite

Install and launch keyboard.wtf 1.4.5 for Windows from:

`https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v1.4.5/keyboard-wtf-setup.exe`

The Store package is Chrome Helper 2.1.23 and uses the backward-compatible 2.1.21 native/content protocol shown in keyboard.wtf Settings.

No extension account or test credentials are required for the connection test.
AI-driven browser tasks require a signed-in keyboard.wtf account with an active
app entitlement.

## Connect

1. Install keyboard.wtf Chrome Helper from the review item.
2. Launch keyboard.wtf.
3. Open keyboard.wtf Settings → Browser Automation.
4. Click **Retry / reconnect**.
5. Open a normal HTTP/HTTPS page.
6. Click **Check connection status**.
7. Confirm:
   - Installation: Chrome Web Store
   - Connection: Connected and ready
   - Helper protocol: 2.1.21 / 2.1.21
   - Current tab: Available

## Optional entitled-app functional test

1. Open `https://en.wikipedia.org/wiki/Alan_Turing`.
2. Start Jarvis and ask: “What is on this page?”
3. Confirm the existing tab is used and no separate Chrome window opens.
4. Confirm the tab briefly shows the Jarvis control indicator.
5. Ask: “Summarize this page.”
6. Confirm Jarvis returns a page-grounded summary and the indicator disappears.

## Cancellation test

1. Ask Jarvis to inspect the page and follow two or three visible links.
2. Press Ctrl+Alt+X while it is running.
3. Confirm the task stops, the indicator disappears, and Chrome stays open.

## Safety behavior

- No action runs while no Jarvis browser task is active.
- The helper stops for passwords, OTPs, CAPTCHAs, payments, checkout, banking,
  and account-security steps.
- The extension has no login flow and no test credentials.
- The extension does not send cookies or raw Chrome profile data.

## Reviewer dependency checkpoint

Do not submit until the matching reviewer Windows installer is available at a
stable HTTPS URL. A Store review cannot fully exercise Native Messaging without
that companion app.
