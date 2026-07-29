# Store asset provenance

All Store assets were generated locally for keyboard.wtf. No third-party brand
art, private browser data, account content, or credentials are embedded.

## Icon and promo tile

- `assets/icon-128.png`
- `assets/small-promo-440x280.png`

Generated deterministically by `scripts/generate-store-assets.ps1` from the
keyboard.wtf orange control-orb/cursor visual language.

## Screenshots

### screenshot-01-connection-1280x800.png

The actual keyboard.wtf Settings HTML at the Browser Automation section. It
shows the Store installation, connection check, retry, and manual fallback UI.
No user settings or live page data are present.

### screenshot-02-controlled-tab-1280x800.png

The actual 2.1.20 content script running against keyboard.wtf’s deterministic
safe browser fixture. The content script produced the visible controlled-tab
badge and exact orange outline around the semantic `Oldest` control. No external
website or account was used.

### screenshot-03-how-to-use-1280x800.png

The actual keyboard.wtf Settings HTML at the How to use Jarvis section. The API
field below it is empty and contains only the application’s generic placeholder.

All screenshots were captured at 1280×800 CSS pixels with Playwright.
