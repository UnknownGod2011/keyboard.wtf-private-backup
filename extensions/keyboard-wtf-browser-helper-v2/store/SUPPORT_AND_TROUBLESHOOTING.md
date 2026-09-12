# keyboard.wtf Chrome Helper support

## Install

1. Install and launch keyboard.wtf for Windows.
2. Install keyboard.wtf Chrome Helper from the Chrome Web Store.
3. Open keyboard.wtf Settings → Browser Automation.
4. Click **Retry / reconnect**.
5. Open a normal website and click **Check connection status**.

## Status meanings

- **Connected and ready:** Jarvis can use the current normal web page.
- **Connected; tab not ready:** switch to an HTTP/HTTPS page and try again.
- **Not connected:** make sure keyboard.wtf is running, then click
  **Retry / reconnect**.
- **Version mismatch:** update both keyboard.wtf and Chrome Helper.

## Manual fallback

Settings includes an unpacked-extension fallback for Store-installation
problems and development. It requires Chrome Developer mode and does not receive
normal Store updates. Prefer the Chrome Web Store version for regular use.

## Safety

The helper acts only during an active Jarvis browser task. It stops for
passwords, one-time codes, CAPTCHAs, payments, checkout, banking, and
account-security steps. Ctrl+Alt+X cancels a running task and keeps Chrome open.

## Contact

- support@keyboardwtf.com
