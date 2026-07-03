# Jarvis Capability Boundaries

keyboard.wtf favors verified actions over brittle UI clicking. Every tool must report what actually happened, and sensitive actions must remain locally confirmation-gated.

## Shipped

| Area | Capability |
| --- | --- |
| Windows startup | Current-user `Run` registration, settings/model reload, and a single-instance guard |
| Desktop | Open common apps and Start menu entries; switch, minimize, maximize, or restore windows |
| Browser | Open safe URLs; control foreground-browser tabs; run experimental multi-step tasks in a dedicated Playwright-controlled Edge/Chrome profile |
| Media | Open Spotify desktop search or YouTube search; open Spotify Liked Songs |
| YouTube | Open Liked Videos, Subscriptions, or History |
| Amazon | Open an Amazon India product search |
| Camera | Open Windows Camera after confirmation |
| Capture | One-shot Gemini screen guidance, desktop screenshots, and webcam photos |
| Windows recording | Xbox Game Bar toggle and Windows screen-region recording shortcuts |
| Productivity | Notes, to-dos, timers, permission-gated selected text/clipboard context, file search, structured memory, routines, and workflows |
| System | Volume, mute, settings pages, battery, charging, uptime, OS, machine, and user status |

## Requires Provider Authorization

- Spotify direct playback requires a Spotify developer app, OAuth, the `user-modify-playback-state` scope, an active playback device, and Spotify Premium.
- Removing YouTube likes is available through `videos.rate` with `rating=none`, but requires Google OAuth and a known video ID. Bulk changes must show a preview and require confirmation.
- Discord messaging requires a registered Discord app or Social SDK integration, OAuth communication scopes, and a stable Discord user ID. Standard user-account self-bot automation is not allowed.
- Webcam photos use a one-shot OpenCV capture after the user's explicit request and save under `Pictures\keyboard.wtf\Camera`. Windows desktop camera privacy access remains authoritative.
- `Win+Alt+R` is a toggle owned by Xbox Game Bar. keyboard.wtf can send it but cannot independently prove the resulting recording state.

## Experimental Browser Agent

The local post-v1.2 build includes a replaceable Playwright for .NET backend. Gemini Live plans actions, while keyboard.wtf owns task state, ambiguity checks, confirmation, cancellation, receipts, and safety.

- A dedicated browser-agent profile is stored under local app data. The normal Chrome/Edge profile is not automated or exported.
- Visible content, accessibility structure, and interactive controls are bounded before being returned to the planner.
- Locator ambiguity and unexpected page changes pause in a `Waiting for clarification` task state.
- Add-to-cart, submit, publish, post, send, delete, and account-changing actions require confirmation.
- Payment, checkout, banking, passwords, OTP, CAPTCHA, and government/medical/legal final submissions are manual-only.
- The task manager permits four active tasks, 30 browser actions per task, and a 15-minute timeout.

This is a usable experimental foundation, not a claim of universal website automation. Site changes, unsupported controls, manual login, and anti-bot challenges can still stop a task.

## References

- Windows Run keys: https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys
- Spotify playback: https://developer.spotify.com/documentation/web-api/reference/start-a-users-playback
- YouTube ratings: https://developers.google.com/youtube/v3/docs/videos/rate
- Discord OAuth and bot accounts: https://discord.com/developers/docs/topics/oauth2
- Windows desktop camera capture UI: https://learn.microsoft.com/windows/apps/develop/camera/cameracaptureui
