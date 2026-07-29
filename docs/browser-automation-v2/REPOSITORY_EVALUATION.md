# Browser automation repository evaluation

Date: 2026-07-26

This evaluation separates an intelligent planner, an existing-browser executor,
visual recovery, verification, and learned workflows. Repository popularity is not
used as the selection criterion.

| Candidate | Role | Existing logged-in Chrome | Strengths | Product risks | Decision |
|---|---|---|---|---|---|
| Browser Use 0.13.6 | Full agent and browser runtime | Yes, through CDP when remote debugging is explicitly enabled | Mature observe/evaluate/next-goal loop, native semantic DOM/accessibility state, screenshots, vision, highlights, multi-action steps, pause/resume/stop, loop detection, stale-element recovery | Everyday-profile CDP is a powerful permission; Python sidecar and direct browser ownership complicate desktop installation | Keep as the reasoning reference and an explicit advanced CDP fallback, not the default existing-Chrome bridge |
| Browser Harness 0.1.8 | Thin persistent CDP executor | Yes, with Chrome remote debugging permission | Fast direct CDP, one persistent connection, current target access, deterministic screenshots/actions, good cancellation boundary | Not an agent; remote debugging onboarding and attach prompts; generated site skills are unsuitable as an implicit production dependency | Retain as executor/reference for advanced mode and benchmarks |
| PageAgent 1.12.2 | In-page Browser Use-derived agent/controller | Yes, through its optional extension | Compact browser-native DOM graph, semantic controls, stable tab IDs, action hooks, abort support, multi-tab extension, MIT | Upstream extension includes side-panel and broad product UI that Jarvis does not need; DOM-only core needs visual recovery | Reuse core/page-controller design principles in a minimal Jarvis helper; do not ship its side panel |
| Stagehand 3.x | Agentic Playwright/CDP runtime | Possible through local CDP | Strong act/observe/extract primitives, action caching and self-healing, good repeat-workflow ergonomics | Node runtime, CDP onboarding, less natural active-tab product integration, another planner/runtime layer | Keep isolated for comparison; do not make the default |
| Unbrowse 11.x | First-party route discovery/cache | Reuses authenticated browser discovery where configured | Potentially very fast warmed read/search/data operations; reusable structured routes | It is not a general visible browser interaction engine; route capture and replay carry authentication/security/website-policy concerns | Future opt-in read-only accelerator only |
| OpenAdapt 1.7.x | Demonstration recording and learned replay | Desktop-wide | Strong demonstrate/learn/execute concepts, semantic abstraction, evidence and repair | Beta, broad desktop capture, heavy stack; not a first-run web agent | Borrow safe workflow-distillation concepts after raw automation is reliable |
| workflow-use 0.2.x | Browser Use trace-to-workflow extraction | Browser Use dependent | Semantic workflow extraction from successful runs | AGPL licensing and early maturity; must not become the primary engine | Architectural reference only |
| Nanobrowser 0.1.x | Extension multi-agent browser product | Yes | Browser Use-derived DOM and broad provider support | Competing UI, side panel, multi-agent latency, provider configuration in the extension | Reject as a product dependency |
| Skyvern 1.x | Hosted/server workflow platform | CDP options exist | Broad workflow and visual automation features | AGPL, database/server/UI footprint, operationally too heavy for local Jarvis | Reject |
| Playwright | Deterministic browser executor | `connectOverCDP`, with reduced fidelity relative to its native protocol | Excellent testing and deterministic actions | No intelligence; direct CDP permission still required | Test/fixture tool only |
| cdp-use / harness-js | Typed low-level CDP transport | Yes | Small direct transport and current-target control | Not a planner | Component/reference only |
| BrowserCode / Browser Use Terminal / Desktop | Complete products or TUI/runtime concepts | Varies | Event logs, durable tasks, pause/resume ideas | Large competing products and runtimes; poor removable integration boundary | Research concepts only |
| Magnitude / visual-only agents | Vision-first agent | Usually controlled browser | Can recover custom-rendered interfaces | Screenshot latency, model cost, weaker deterministic grounding | Recovery benchmark only |

## Browser Use defaults worth preserving

The current Browser Use agent keeps the complete user task in context and supports
planning, thinking, semantic interactive elements, selected/checked state, multiple
safe actions per step, action history, screenshots, visual recovery, loop detection,
pause/resume/stop, and completion actions. Earlier keyboard.wtf wrappers disabled or
reduced several of these capabilities and then compensated with brittle recovery
logic. Browser automation v2 must preserve this intelligence intentionally.

## Existing-session options

### Extension plus native messaging

Best fit for normal users:

- Works in the Chrome profile the user already uses.
- Uses stable Chrome tab IDs rather than whichever tab happens to be active later.
- Does not require a debugging port.
- Chrome restricts the native host to the explicitly allowed extension origin.
- The extension can provide compact live DOM/accessibility state, highlights, action
  receipts, and cropped screenshots.
- A small native host can reconnect the extension to Jarvis after either side
  restarts.

### Direct CDP

Useful advanced/debug fallback:

- Gives Browser Use or Browser Harness their native browser connection.
- Requires the user to enable Chrome remote debugging and approve attachment where
  Chrome requires it.
- Exposes broad page/session capabilities while enabled and therefore must not be
  silently activated.
- Must bind only to loopback and must never copy the user profile.

## Learned workflows

Learned execution is deliberately phase two. A successful verified trace may be
distilled to semantic roles, labels, state transitions, parameters, checkpoints, and
manual-intervention gates. It must never retain credentials, cookies, OTPs, payment
data, raw coordinates, private page dumps, or final result URLs as universal truth.
Every replay must re-observe and revalidate the live page.
