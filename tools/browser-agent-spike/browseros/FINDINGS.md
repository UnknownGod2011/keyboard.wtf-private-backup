# BrowserOS external-backend spike findings

Date: 2026-06-26

BrowserOS was evaluated only as an external backend candidate. Its upstream repository is AGPL-3.0, so keyboard.wtf must not vendor, copy, link, or merge BrowserOS source code without a separate licensing decision.

## What ran

- `npx browseros-cli@0.3.2 --help`
- `npx browseros-cli info --json`
- `npx browseros-cli status --json`
- `npx browseros-cli health --json`
- `npx browseros-cli launch --json`

## Result

- CLI installed and ran externally.
- CLI describes BrowserOS as an MCP-controlled browser automation runtime.
- BrowserOS app/runtime is not installed locally.
- `browseros-cli launch` returned: BrowserOS is not installed.
- `browseros-cli status/health` returned: server URL is not configured.
- The official Windows installer download from `https://files.browseros.com/download/BrowserOS_installer.exe` timed out and left an unsigned file in `%TEMP%`; it was not executed.

## Integration shape if revisited

- User installs BrowserOS manually.
- User opens BrowserOS Settings > BrowserOS MCP and copies the server URL.
- keyboard.wtf treats BrowserOS as an external optional backend through CLI/MCP only.
- No BrowserOS source enters `src/`.
- BrowserOS remains disabled by default because of AGPL/commercial risk and installation complexity.

## Current decision

BrowserOS cannot be benchmarked on this machine yet. It is useful to study as architecture inspiration for browser-native/MCP control, but it is not ready to become keyboard.wtf's default backend until the installed app/server can be verified and licensing is resolved.
