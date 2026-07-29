# keyboard.wtf Chrome Helper v2.1

This minimal Manifest V3 extension lets the local keyboard.wtf app control only the
tabs assigned to an active browser-automation task.

It intentionally has:

- no side panel;
- no provider/API keys;
- no cookie export;
- no page-history collection;
- no background action when there is no active Jarvis task;
- no separate browser profile.

It exposes compact semantic controls, stable Chrome tab/window IDs, exact target
rectangles, on-demand screenshots, action receipts, a small controlled-tab badge,
and temporary orange target highlights.

## Development installation

1. Open `chrome://extensions`.
2. Enable Developer mode.
3. Choose **Load unpacked**.
4. Select this directory.
5. In keyboard.wtf Settings, use **Connect Chrome Helper** to register the
   per-user native messaging host.

Chrome verifies the fixed extension identity
`gfndpohemndahmmeealkdobepjacblnf` before starting the native host.
