# Stable baseline restoration

Date: 2026-07-26

## Source baseline

- Active branch: `codex/browser-automation-bakeoff`
- Active HEAD was preserved; no reset, rebase, checkout, merge, or history rewrite was used.
- Stable reference: `UnknownGod2011/keyboard.wtf` `main` at
  `946a156e132a04ef4353152fd1ed2f744c3c1b91`.
- The 129 tracked files in the active workspace were compared with an independent
  clean clone of that reference. There were no missing files and no content hash
  differences after restoration.
- The previous browser-automation implementation, bake-off harness, sidecar, helper
  extension, generated runtime files, UI, settings, and product tests were removed
  from the active product.

## Safety copy

The pre-restoration dirty tree was preserved outside the worktree at:

`C:\Users\Admin\OneDrive\Desktop\keyboard.wtf-browser-automation-snapshot-20260726-155046`

The snapshot contains a tracked patch, an untracked-file manifest, checksums, and a
copy of the previously installed application. It intentionally excludes credentials,
browser profiles, cookies, generated build trees, and private browsing data.

Browser-automation AppData was moved without reading its contents to:

`C:\Users\Admin\AppData\Local\keyboard.wtf-browser-automation-backup-20260726-1605`

## Verified stable product

- Release build: 0 warnings, 0 errors.
- Automated tests: 68 passed, 0 failed, 0 skipped.
- Installed executable:
  `C:\Users\Admin\AppData\Local\Programs\keyboard.wtf\keyboard.wtf.exe`
- Installed executable SHA-256:
  `F17951BFC0E3A4531A2E896D79F812C30EC34592CC29B50E9DFB0EA16B27B55C`
- The installed executable is byte-for-byte identical to the stable published
  executable.
- Start Menu and Windows startup registration target the installed executable.
- The app starts, remains alive, serves Settings on port 8090, registers all normal
  hotkeys, and exposes no browser-automation runtime fields.
- A real `Ctrl+Alt+J` / `Ctrl+Alt+X` probe started and cancelled a Jarvis
  conversation successfully.
- No Python browser sidecar remained running.
- Chrome remained at one existing window during restoration and verification.

## Stable routing behavior

The stable `BrowserLauncherService` remains the fast path for direct URL and search
commands. Its tests verify safe HTTP(S) URL handling and deterministic Google,
YouTube, and GitHub search URLs. No smart browser-automation process can start in
this baseline.

This verified state is the rollback point for browser automation v2.
