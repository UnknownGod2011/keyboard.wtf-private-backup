# Browser automation v2 architecture decision

Status: accepted for implementation

## Decision

Use one Jarvis-owned task coordinator with a minimal Chrome extension and native
messaging bridge as the default product path. Preserve Browser Use's intelligent
observe/evaluate/next-goal behavior in the planner, while the extension remains a
grounded executor rather than a second planner.

Keep direct Browser Use/CDP as an explicit advanced fallback and benchmark adapter.
Do not start it silently. Keep Unbrowse and learned workflow replay disabled until
the raw action path passes broad acceptance tests.

## Runtime

```text
normal Jarvis request
  -> deterministic fast-path classifier
       -> direct URL/search/tool (no browser automation), or
       -> BrowserAutomationCoordinator
            -> planner (full task + compact fresh page state)
            -> Chrome Helper (one task lease, stable tab IDs)
            -> before/after action receipt
            -> deterministic verifier
            -> reobserve/replan or terminal result
```

The coordinator owns the task ID, state, tab leases, cancellation source, pause
gate, progress stream, verification evidence, and cleanup. Only one executor may
mutate a page at a time. Jarvis conversation remains available because starting a
browser task returns immediately with a task ID; status and steering use separate
tools.

## Classification

Deterministic simple commands remain on existing tools:

- open a known site or URL;
- construct a Google, YouTube, GitHub, or site search URL;
- open/close/switch/refresh a browser tab;
- launch an app.

Use browser automation when the requested outcome depends on live webpage state:

- read or summarize the current page when selected text is insufficient;
- click, choose, filter, fill, copy, paste, inspect, or dismiss a page control;
- navigate a multi-step workflow;
- use logged-in state;
- coordinate multiple pages or tabs;
- verify a page mutation.

An explicit automation request and high-confidence page-interaction intent start
immediately. Genuine ambiguity asks one concise clarification.

## Extension

The Manifest V3 helper has:

- a fixed extension identity;
- `nativeMessaging`, `tabs`, `scripting`, and narrowly explained host access;
- a service worker that reconnects with bounded backoff;
- content scripts that remain idle without an active task lease;
- stable tab/window IDs and task-owned tab tracking;
- compact DOM/accessibility control graphs;
- on-demand screenshots/crops;
- orange non-blocking target highlights;
- an unobtrusive controlled-tab indicator;
- structured action receipts and graph epochs.

There is no side panel, devtools panel, full-tab recoloring, provider key, cookie
export, raw page archive, or background tracking.

## Native bridge

The installed keyboard.wtf executable also supports a native-host entry mode. Chrome
starts it over framed stdin/stdout and verifies the extension origin. It relays to
the running main app through a per-user named pipe. If the app is not running, the
host may start the installed app and reconnect. Secrets are never passed on command
lines or to the extension.

## Planner

The planner receives:

- the complete original request;
- current/new-tab intent and task-owned tabs;
- fresh URL/title;
- compact relevant page text;
- indexed controls with role, text, accessible name, label, state, frame, and bounds;
- prior action receipts and concise task history;
- a screenshot crop only when DOM/accessibility grounding is insufficient.

Fast, unambiguous mechanical actions avoid an LLM call. Complex page decisions use
one Gemini planner. Visual ambiguity uses one cropped Gemini vision call. A critic
is allowed only after a no-op, ambiguity, or near-completion uncertainty.

## Safety and manual intervention

Navigation, reading, ordinary field filling, non-destructive clicks, harmless popup
dismissal, media opening, and add-to-cart may run when explicitly requested.

The task pauses before:

- CAPTCHA, password, OTP, two-factor or security challenge;
- checkout, payment, order placement, banking, or account-security change;
- public/destructive submit, send, post, delete, or registration unless explicitly
  requested and allowed by policy.

The progress UI identifies the controlled tab and allows Pause, Resume, and Cancel.
`Ctrl+Alt+X` cancels Jarvis and the browser task through the same task token.

## Verification

Planner text never proves success. Completion requires current-task evidence:

- URL/domain/page state;
- selected/checked/input state;
- page/DOM fingerprint change;
- video/watch state;
- cart state;
- clipboard read-back;
- target document content;
- form values.

Terminal evidence stops planning immediately. Chrome and useful tabs stay open.

## Rollback

The entire v2 feature is gated by `BrowserAutomationEnabled`. Disabling it removes
the browser tool from Jarvis behavior and leaves all stable direct URL/search tools
unchanged. The extension and native-host registration are separately removable. The
verified stable build and pre-restoration safety snapshot remain available outside
the active worktree.
