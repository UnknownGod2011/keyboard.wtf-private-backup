# keyboard.wtf Web Deployment Notes

## Production

- Canonical public URL: `https://keyboardwtf.com`
- Permanent compatibility URL: `https://keyboard-wtf.vercel.app`
- Vercel project: `site`
- Production deployment: `site-5fyb3tgdm-tanush-shahs-projects-5e868e6e.vercel.app`
- Site directory: `site`
- Framework: static HTML
- Deployment Protection: disabled for this public download project

Vercel does not allow this account to claim `keyboard.wtf.vercel.app`. That host is a nested subdomain of `wtf.vercel.app`, which is reserved for another Vercel account. The available first-party Vercel URL is `keyboard-wtf.vercel.app`.

## Windows Installer

- Build script: `scripts/build-windows-release.ps1`
- Inno Setup definition: `installer/keyboard-wtf.iss`
- Website download: `site/downloads/keyboard-wtf-setup.exe`
- Public download: `https://keyboardwtf.com/downloads/keyboard-wtf-setup.exe`
- Size: 56,714,119 bytes
- SHA-256: `090A81F71788BBBFFCE1BD475EA34E7C1C071F1089CDEB27157F88A5D58FAF74`

The installer is self-contained and installs per user under `%LOCALAPPDATA%\Programs\keyboard.wtf`. It registers the installed executable under the current user's Windows Run key and launches the app after interactive installation.

The app downloads missing Vosk, Whisper, and Piper assets into `%LOCALAPPDATA%\keyboard.wtf` and retries incomplete setup on later launches.

## First Run

- New users default to `AlwaysAsk` for routine Jarvis actions.
- The local settings page opens at `Set API Key` when Gemini is not configured.
- `Get API Key` opens `https://aistudio.google.com/api-keys`.
- API keys are encrypted with Windows DPAPI and are never submitted to the website.
- The setup page reports Gemini, speech model, and Windows startup readiness.
- The setup page includes the default hotkeys and mode summary.

## Verification

Verified on June 7, 2026:

- Release build: zero warnings and zero errors.
- Installer: exit code 0.
- Installed executable runs from `%LOCALAPPDATA%\Programs\keyboard.wtf`.
- Windows startup registration points to the installed executable.
- Existing settings and the local `AutoExecute` development preference survive upgrades.
- A clean profile defaults to `AlwaysAsk`.
- Vosk, Whisper, and Piper report ready.
- Live Gemini API test succeeds.
- Production page is publicly accessible without Vercel authentication.
- Public installer response is HTTP 200 with attachment disposition.
- Downloaded public installer matches the release SHA-256 exactly.
