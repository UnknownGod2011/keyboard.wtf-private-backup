# Windows Browser Agent and Structured Memory Foundation

Status: local experimental design for the post-v1.2 Windows build. This work is intentionally not a release commitment.

## Existing architecture to preserve

keyboard.wtf v1.2 is a .NET 8 WinForms tray application. Gemini Live handles the spoken conversation and tool selection. `JarvisAutomationService` owns allowlisted Windows actions, `JarvisPermissionPolicy` separates read-only and side-effecting operations, `SettingsService` persists local settings, and the current local memory is split between intent notes, learned mappings, and workflows.

The browser-agent upgrade must remain an extension of those boundaries:

- Simple app, file, window, desktop, recording, dictation, camera, and screen-guidance actions keep their existing paths.
- Multi-step browser work gets a separate task and browser-agent layer.
- The task layer exposes practical plan/status events, never hidden model reasoning.
- The existing Ask and Auto-execute modes remain authoritative.
- Windows, browser, login, OTP, CAPTCHA, and site permissions are never bypassed.

## Research summary

### Browser automation

- **Microsoft Playwright / Playwright for .NET** — Apache-2.0. It provides deterministic browser control, accessibility-first locators, auto-waiting, file upload support, isolated browser contexts, and support for installed Microsoft Edge and Google Chrome channels. It has an official .NET package, so keyboard.wtf does not need a Python or Node sidecar.
- **browser-use** — MIT and strong as a reference for an LLM action loop, but its primary implementation is Python and production guidance increasingly points toward hosted browser infrastructure. Adding it would create a second runtime and packaging surface.
- **Stagehand** — MIT and useful as a reference for mixing deterministic actions with AI-generated actions and caching. Its main SDK/runtime would still duplicate a large part of the control and policy layer keyboard.wtf needs to own.
- **Skyvern** — capable, but AGPL-3.0 and operationally heavy. It is not suitable as a dependency for the intended future commercial desktop product.
- **Playwright MCP** — Apache-2.0 and useful for research, but keyboard.wtf already has Gemini Live tool calling. An MCP server would add another protocol/process without solving the product-specific safety, task UI, and confirmation requirements.

### Agent memory

- **Mem0** — Apache-2.0 and useful as a reference for multi-signal retrieval and memory lifecycle, but its practical local stack adds Python/TypeScript, embeddings, LLM extraction, and vector-store choices.
- **Zep** — Apache-2.0 examples and cloud SDKs, but the open-source community server is deprecated. The current product direction is cloud-first and does not fit local-first desktop memory.
- **sqlite-vec** — MIT/Apache-2.0 and compact, but still pre-v1. It also requires an embedding model and native extension packaging before it improves this app's small, structured memory set.
- **Deterministic structured local storage** — no new runtime, transparent/editable data, predictable conflict handling, and easy migration from the current JSON files.

## Decision

Use the official `Microsoft.Playwright` .NET library behind an `IBrowserAutomationBackend` abstraction. Launch a controlled, visible Edge or Chrome session with a dedicated keyboard.wtf profile under local app data. Never automate the user's default daily browser profile and never export cookies or browser state.

Gemini Live remains the planner. The browser backend exposes deterministic operations such as observe, navigate, click, fill, select, upload, and verify. keyboard.wtf owns:

- task lifecycle and cancellation;
- locator ambiguity checks;
- confidence and clarification policy;
- confirmation gates;
- irreversible-action blocking;
- receipts and status events;
- browser profile location and cleanup boundaries.

This keeps the backend replaceable. Stagehand or another agent planner can be evaluated later without rewriting the task UI, memory, or safety policy.

For memory, replace the small plain key/value intent store with a versioned structured JSON store and deterministic retrieval. Keep compatibility APIs and migrate old intent entries. Continue to support learned mappings and workflows while recording new user-approved corrections, aliases, preferences, routines, and task patterns in the structured store.

## Browser safety model

Safe without an additional step confirmation after the task itself is approved:

- open and inspect an HTTP(S) page;
- search, navigate, scroll, and read visible content;
- fill reversible non-sensitive fields;
- prepare a form or upload that the user explicitly requested;
- verify page title, URL, visible text, and field values.

Always pause:

- add to cart;
- submit, publish, post, send, delete, remove, subscribe, or change account settings;
- any action whose target is ambiguous;
- any unexpected site/page transition that changes the intended flow.

Always block for manual completion:

- payment, checkout, money transfer, banking, card details;
- OTP, password, CAPTCHA, or anti-bot bypass;
- government, medical, or legal final submission;
- any action that attempts to weaken browser or Windows security.

The agent must stop before payment and must not report success without a verifiable page result.

## Task model

A task contains:

- task id, title, original command, status, permissions, timestamps;
- bounded steps with pending, running, completed, failed, waiting-for-confirmation, waiting-for-clarification, or cancelled state;
- a cancellation token and timeout;
- captured context metadata;
- a practical receipt.

The manager permits at most four active tasks. Simple one-step Jarvis actions do not create task tabs.

## Context model

Context is captured only when the user explicitly asks to use it. Priority:

1. selected text;
2. explicitly requested clipboard text;
3. active window title/process;
4. the current page in a controlled browser-agent session.

Clipboard contents are restored after selected-text capture. Long text is bounded and reduced to a short summary plus deterministic key/value fields before it is sent to the planner. External browser DOM access is not claimed unless the page is inside the controlled Playwright session.

## Memory schema and retrieval

Memory types:

- preference
- correction
- app alias
- file alias
- folder alias
- browser preference
- workflow
- routine
- task pattern
- failed-attempt fix

Each entry stores created/updated timestamps, source phrase, trigger phrase, structured action/target data, confidence, usage count, and last-used timestamp. Exact normalized triggers win. Fuzzy retrieval requires a minimum score and a sufficient score gap; otherwise the result is ambiguous and Jarvis asks the user to choose.

The store is bounded, local-only, atomically written, editable, and deletable. Vector search is deferred until the memory count and measured retrieval quality justify the added model/native dependency.

## Experimental scope for this pass

The foundation will support:

- a visible controlled Edge/Chrome session;
- page observation and bounded accessibility/interactive summaries;
- deterministic navigate/click/fill/select/upload/verify operations;
- task tabs, progress, cancellation, confirmation, clarification, and receipts;
- one harmless public-site verification flow;
- one local mock-form fill/review flow.

It will not claim universal website completion. Login remains manual in the dedicated profile, CAPTCHA/OTP remain manual, and site-specific changes can still require clarification.
