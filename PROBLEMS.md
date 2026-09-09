# keyboard.wtf 1.4.19 stabilization contract

Installed checkpoint: source `d278766a7cda4f7babdd3bea5673cdc780946bc5`; installer `5479458E2D27EB579FE5DB8E48AD1B600A0574D237F84DFC253B10961644BAB4` (84,283,278 bytes); installed executable `1B217B33A4AE6FBE3F4CC9D1303D015B9946C449720DFC6F4A055A9B0D1644C0` (314,744,270 bytes); Helper 2.1.26 / protocol 2.1.21. Human evidence overrides internal evidence.

## P01 — Startup consistency
Status: FIXING
Expected: Jarvis normally reaches Listening near 2.5–3s, with reliable cold/warm/restart behavior and no unexplained failures.
Latest evidence: Human runs were ~6–7s and 3s; logs measured 6.979s and 2.505s.
Root cause: Slow sample occurred after the app idled past access-token expiry, adding a 2.936s refresh to the normal backend/provider handshake.
Fix: Refresh signed-in access tokens shortly before expiry in the background; keep the existing on-demand authenticated fallback.
Evidence: `keyboard.wtf.log` sessions `144b85...` and `cf9358...`.
Touches: Paid account refresh; Live startup transport.
Protect: 2.37–3s happy path, microphone readiness, reliable connection.

## P02 — Exactly-once completion and action ownership
Status: FIXED-SOURCE
Expected: One logical request may execute several actions but produces one coherent completion and no repeated mutation.
Latest evidence: Current-source TEST Live opened two requested folders through two mutations and spoke one combined completion; closing both likewise executed exactly two native closes and spoke one combined completion.
Root cause: An authoritative pre-route receipt was deleted after the first provider acknowledgement, allowing another tool call from the same logical turn to regain mutation ownership.
Fix: Keep the turn receipt authoritative until the next user turn; only its first acknowledgement carries the result and all later calls receive one silent suppression receipt.
Evidence: Original duplicate action-history entries `be932...`/`9e6c...`; current TEST run at 06:14–06:15 opened and closed two CRISHIRT folders once each with one spoken response per request. Final production-installed microphone proof remains pending.
Touches: Transcript pre-routing; tool dispatch/result publication; action dedupe.
Protect: Clarification/barge-in and multi-action batching.

## P03 — Jarvis popup topmost
Status: FIXED-SOURCE
Expected: A visible Jarvis overlay remains above ordinary apps without stealing focus.
Latest evidence: The current TEST overlay stayed visibly above the foreground app and its live HWND retained `WS_EX_TOPMOST | WS_EX_NOACTIVATE` through Chrome, Edge and Explorer activity.
Root cause: Topmost was only a managed property; recreated native handles did not carry the topmost extended style.
Fix: Every overlay HWND now owns `WS_EX_TOPMOST | WS_EX_NOACTIVATE` directly.
Evidence: Focused native-style test plus physical HWND/style and desktop screenshot proof; final production-installed multi-app acceptance pending.
Touches: `VoiceOverlayForm` window lifecycle.
Protect: No-focus-steal interaction and active-app typing.

## P04 — First-utterance farewell
Status: OPEN
Expected: A clear recognized farewell ends locally before tools, Browser Automation or model speech.
Latest evidence: Human `goodbye` failed again while a visible Jarvis process was active; that process may have been intermediate TEST/production, so exact process/transcript ownership must be proven before source changes.
Root cause: The local path was reliable only when input transcription arrived; a dedicated provider end tool was rejected when that independent transcript was absent.
Fix: Keep transcript-first local authority; after its bounded wait, accept the dedicated end tool only if no contradictory transcript exists.
Evidence: Human report, current TEST physical shutdown, and focused ordering tests for transcript-first, absent-transcript fallback and contradictory-transcript rejection; final repeated production microphone proof pending.
Touches: Live transcription/event ordering; session stop.
Protect: Audio/VAD/barge-in and immediate local shutdown.

## P05 — Conversation/random-action safety
Status: VERIFIED-INSTALLED
Expected: Conversation never mutates the computer; explicit actions execute once.
Latest evidence: Exact installed build passed deterministic routing and repeated input-safety tests; no new unrelated mutation in latest human run.
Root cause: Earlier model/direct/input ownership allowed stale or unsafe mutations.
Fix: Final authoritative transcript, serialized action owner, balanced input and stale-generation rejection.
Evidence: Full 922 desktop tests and 1,500 race repetitions at `7824b192...`.
Touches: Live dispatch; command routing; trusted input.
Protect: Direct-action speed and normal conversation.

## P06 — Deterministic Explorer close
Status: VERIFIED-HUMAN
Expected: Close a fixed set of actual Explorer windows only, verify disappearance, never kill the shell or affect browsers.
Latest evidence: Human close of two windows was immediate/correct; physical 1- and 3-window tests preserved Chrome.
Root cause: Retired fallback used browser/foreground shortcuts instead of Explorer HWND ownership.
Fix: Fixed native Explorer handle snapshot, identity validation, native close and verification.
Evidence: Human session; `native-window-acceptance` including mid-close and pre-cancel cases.
Touches: Native window closer.
Protect: No blind Ctrl+W/Alt+F4 and no `explorer.exe` termination.

## P07 — Modifier and foreground safety
Status: VERIFIED-INSTALLED
Expected: User-held modifiers, focus changes and cancellation cannot redirect or contaminate injected input.
Latest evidence: Exact installed build passed balanced-input, HWND validation and 1,500 cancellation/race repetitions.
Root cause: Earlier key injection lacked one authoritative key/target owner.
Fix: Balanced batches, held-modifier refusal, target validation and final cancellation checks.
Evidence: Full gate and adversarial race harness at `7824b192...`.
Touches: Trusted keyboard/input boundary.
Protect: Fast explicit direct actions.

## P08 — Cancellation and stale-runtime safety
Status: VERIFIED-INSTALLED
Expected: Cancelled work cannot later post native close, send keys, publish results or overwrite a newer task.
Latest evidence: Exact build contains final pre-mutation/pre-publication cancellation checks and passed focused/full gates.
Root cause: Cancellation could arrive during window validation or socket wait, after an earlier check.
Fix: Propagate one cancellation token through mutation and Live publication boundaries.
Evidence: 922 desktop tests; private CI; 1,500 race repetitions.
Touches: Native close; trusted input; Live dispatcher.
Protect: Non-blocking Live control events.

## P09 — Loud/full-scale PCM processing
Status: VERIFIED-INSTALLED
Expected: Valid loud PCM never throws or drops microphone chunks.
Latest evidence: Exact installed build includes overflow-safe metering and exhaustive full-scale regression tests.
Root cause: Squared 16-bit samples could overflow signed arithmetic.
Fix: Widen before multiplication/accumulation.
Evidence: Focused audio tests and full 922-test pass.
Touches: Microphone level metering.
Protect: Audio quality, VAD and barge-in.

## P10 — Browser context and intent ownership
Status: FIXED-SOURCE
Expected: Context-now/action-later waits; fresh selection replaces stale; selected-text reasoning stays conversational.
Latest evidence: A later TEST turn supplied fresh clipboard text before an explicit selected-text task; the start owner initially accepted that wrong source. Task `d14cb8...` now attached the actual selected text and visibly completed the form without submission/reset.
Root cause: Browser start first depended on an optional model capture, then treated any fresh captured source as satisfying an explicitly named source.
Fix: The existing Browser start owner captures the explicitly named source and retained context is accepted only when its source identity matches the request.
Evidence: Failure task `5d631d...`; corrected real-Helper task `d14cb8...` carried `AUTHORITATIVE ... SELECTED TEXT`, filled all known fields with correct group identity, skipped the optional field, and matched the visible page; 466 focused tests pass. Final production-installed verification pending.
Touches: Command routing; selected context; Browser coordinator.
Protect: Direct actions and selected-text reasoning.

## P11 — Browser page/task ownership
Status: VERIFIED-HUMAN
Expected: Explicit current-page handoff resumes the same task and invalidates stale page actions/results.
Latest evidence: Current TEST/real-Helper task `3419b6...` paused on page A, adopted the explicitly navigated page B with the same task ID, filled the requested field, and cancelled cleanly.
Root cause: Task remained pinned to an obsolete tab generation.
Fix: Explicit re-grounding advances page generation and rejects older work.
Evidence: Human acceptance plus regression tests.
Touches: Browser coordinator/runtime generation.
Protect: Never adopt unrelated foreground tabs automatically.

## P12 — Form write correctness
Status: VERIFIED-HUMAN
Expected: Context values target the correct question/group once; replace/append are explicit; no duplicate write or Clear/Reset.
Latest evidence: Disposable Google Form was visibly completed with correct repeated-option identity, replace and append behavior.
Root cause: Helper metadata loss and non-atomic field assignment/reset inference.
Fix: Helper 2.1.26 metadata plus atomic typed field assignments and visible verification.
Evidence: Human Google Form acceptance; Helper Store 2.1.26 installed.
Touches: Helper metadata; planner/executor/verification.
Protect: Do not submit test forms.

## P13 — NeedsUserAction bridge
Status: FIXED-SOURCE
Expected: Ask only for genuinely missing required data; relevant natural answer resumes the same task; optional skip works; unrelated talk is isolated.
Latest evidence: TEST task `3419b6...` exposed a second scope defect: after a one-field edit it asked for an unrelated required form field.
Root cause: Required-field planner/verifier rules treated any field edit that mentioned a form as whole-form completion.
Fix: Preserve requested scope; require every required question only for explicit whole-form completion, while exact intervention matching still prevents answered source controls impersonating missing targets.
Evidence: Task `88267a...` changed only Full name, completed without asking for Email or submitting, and matched the visible page; 460 focused tests pass. Final installed verification pending.
Touches: Jarvis/Browser intervention bridge.
Protect: Normal conversation while paused.

## P14 — Browser cancellation in every state
Status: FIXED-SOURCE
Expected: Planning/Running/Waiting/NeedsUserAction/Partial cancel once, clear UI/task/intervention, and execute nothing stale.
Latest evidence: run6 real-Helper tasks cancelled cleanly during Reading, Planning, Executing, WaitingForPage and NeedsUserAction; only already-completed mutations remained and no delayed mutation/intervention survived. Terminal Partial cancellation remains covered deterministically for final installed verification.
Root cause: Late mutation/result boundaries could outlive cancellation.
Fix: Single task cancellation authority propagated to executor, input and publication.
Evidence: 1,500 race repetitions/full gates at `7824b192...`; run6 tasks `1c32a8...`, `333b2a...`, `c66ca8...`, `2ec162...`, `861691...` and `32b6cd...` covered active cancellation and precise intervention cleanup.
Touches: Browser coordinator/executor; Live/input boundary.
Protect: Jarvis remains usable after cancel.

## P15 — Browser correctness constraints and visible completion
Status: FIXED-SOURCE
Expected: Latest-by-publisher verifies publisher/freshness; useful page is preserved; Completed matches visible state.
Latest evidence: Current source reached YouTube from unrelated Devpost, waited through first-navigation readiness, then completed on the verified official Logitech upload from five hours ago; the local form also matched its completion visibly.
Root cause: The readiness wait applied only when the original tab was protected/blank, not when the task opened its first destination in a new tab from an ordinary page.
Fix: Apply the existing one-time readiness wait to the task's first successful navigation regardless of the original tab type.
Evidence: Failed tasks `0c821f...`/`9e5a01...`; passing current-source task `2f3216...` reached official video `q0fH3eQ6RsQ`; form task `6ef918...` visibly matched completion.
Touches: Browser planner/coordinator/verifier.
Protect: Generic behavior; no site-specific rules.

## P16 — Direct Settings and Browser opt-out
Status: FIXED-SOURCE
Expected: Internal Settings sections, URLs/apps/search/mappings remain direct; explicit Browser Automation opt-out is authoritative.
Latest evidence: Full TEST Jarvis incorrectly started Browser Automation for a direct request to switch to the last Chrome tab.
Root cause: Browser-tab grammar was duplicated; both the direct parser and classifier missed a browser qualifier after the ordinal, so generic page-section routing won.
Fix: Make the direct browser parser the classifier's single authority and accept an optional browser qualifier around an ordinal tab selection.
Evidence: Failing task `7e87ce...`; current TEST physically switched the second Chrome tab, searched Devpost once, opened a URL under explicit BA opt-out, and opened internal Settings with no Browser task; 460 focused tests pass. Final installed verification pending.
Touches: Direct-action intent/routing.
Protect: Browser Automation still starts for real interactive webpage work.

## P17 — Production Browser credit capacity
Status: OPEN
Expected: Final bounded production Browser acceptance can reserve and settle normally.
Latest evidence: Browser planning and later production Live reconnect/start attempts returned HTTP 402 while access-token refresh remained HTTP 200; available credits are below the required reservation.
Root cause: Acceptance account capacity, not Helper/native messaging.
Fix: Use isolated non-shipping test capacity now; run bounded production acceptance when capacity exists.
Evidence: Task `eadf43...`; Live session `35295324...` ended after four sequential sockets, then reconnects and fresh sessions `13e5cc...`/`a4bde09...` received 402 after successful auth refresh.
Touches: Test execution only.
Protect: Production billing rules and balances.

## P18 — Isolated Gemini/Jarvis/Browser test harness
Status: VERIFIED-INSTALLED
Expected: Separate Ctrl+Alt+T TEST process/profile exercises real Jarvis UI/mic plus coordinator/executor/Helper without production billing/provider/data mutation.
Latest evidence: The disposable TEST process exercised real Jarvis, Browser Automation, Helper and long Smart Writing while production executable/profile/native-host hashes and billing stayed unchanged; all TEST processes are now stopped.
Root cause: Production credit reservation blocks repeated acceptance.
Fix: Compile current source into an ignored TEST-only executable/profile; load the key through masked process memory, compile registration bypass out of production, and register only explicit TEST hotkeys.
Evidence: Ctrl+Alt+T launched the isolated process; production settings SHA remained `8506D843...`, installed EXE SHA `1B217B33...`; Store Helper 2.1.26 connected without registry rewrite and paid/auth stayed off.
Touches: Compile-time TEST seam plus ignored test project only.
Protect: Production auth, provider, Firestore and billing.

## P19 — No test provider in shipping artifacts
Status: FIXED-SOURCE
Expected: Final source/package/install/site/logs contain no test keys, hotkey, provider selector, URL or billing bypass.
Latest evidence: The temporary compile-time provider, profile, hotkey and diagnostic seams have been removed from tracked source; all TEST processes, local server/browser and temporary encrypted-key backup are gone.
Root cause: Preventive release requirement.
Fix: Remove the disposable seam after acceptance and scan tracked source plus final package/install independently.
Evidence: Tracked source scan reports no TEST symbol/provider/hotkey/diagnostic seam; final package/install scans remain pending.
Touches: Packaging/release verification.
Protect: No BYOK or hidden production bypass.

## P20 — Long Smart Writing
Status: FIXED-SOURCE
Expected: Roughly 10-minute-plus capture has no arbitrary app cutoff; successful audio is preserved as ordered full or clearly partial output, inserted once, with correct clipboard/UI/settlement.
Latest evidence: A real 605-second WAV completed through TEST Smart Writing and pasted once; a forced invalid-provider run then recovered and pasted the full 8,337-character local transcript once instead of losing it.
Root cause: A five-minute desktop cap and whole-request provider size/deadline made valid long capture impossible and discarded provider-failure results.
Fix: Remove Smart Writing auto-cap, allow long/manual Dictation, segment cloud audio under one operation, and recover captured text without duplicate insertion.
Evidence: 561 focused tests cover segmentation/cancellation/recovery; TEST desktop operations `604887910331` and `3c920af06fb1` ended Done with one paste each. Final exact installed microphone/settlement acceptance remains pending.
Touches: Smart Writing transport/UI/clipboard/settlement.
Protect: Existing writing quality.

## P21 — Learned mappings and workflows
Status: VERIFIED-HUMAN
Expected: A selected URL or valid hostname plus a custom trigger stores one durable Link that executes after conversation/app restart and outranks generic lookup.
Latest evidence: Exact installed `d278766...` captured selected bare hostname `keyboardwtf.com`, stored/read back one normalized Link and no Workflow, executed it once, survived a real app restart, then executed it once again.
Root cause: The selected/deictic Link form still depends on optional model tool selection; unlike explicit-URL mappings, it has no deterministic local pre-route owner.
Fix: Route only an explicit single selected-Link mapping grammar through the existing Link tool owner, which captures, validates, normalizes and read-backs the selection once.
Evidence: Persisted store contains exactly one `Link` targeting `https://keyboardwtf.com/` and zero matching Workflows; visible Chrome/action-history runs at 07:06:03 and 07:10:39 opened exactly one destination each; restart changed process IDs; private CI `34176954928` passed.
Touches: Learned mapping parser/store/route.
Protect: Workflow/tombstone behavior.

## P22 — Live lease/resumption/accounting
Status: VERIFIED-INSTALLED
Expected: 720s authoritative lifecycle, 120s cleanup grace, Cloud Run 900s, sequential resumption, no duplicate socket/409 and exactly-once settlement.
Latest evidence: Current config/revision and long-session tests preserve post-1.4.17 lease fix; latest sessions settled cleanly.
Root cause: 1.4.17-era reservation ownership used a retired short lifetime.
Fix: Server-owned long lease with bounded grace and sequential physical sockets.
Evidence: Backend 262, Firestore 12, prior >8m continuity and production settlement checks.
Touches: Live backend/session accounting.
Protect: Do not alter Live/audio/VAD architecture.

## P23 — Website Pricing layout and auth state
Status: FIXED-SOURCE
Expected: Compact hero, card/notice aligned, FAQ then reviews, no real mobile overflow, and signed-out/no-plan/active-plan hydration without re-sign-in.
Latest evidence: The unreleased page has no DOM overflow at 390/768/1440px; card, shell and notice have identical bounds; isolated auth fixtures render the intended signed-out, signed-in/no-plan and active-plan states.
Root cause: Public production remains 1.4.18; mobile discrepancy not yet proven as DOM overflow.
Fix: Keep the existing alignment/auth hydration correction; deploy only after final application acceptance.
Evidence: Computed Playwright matrix at 390/768/1440px, all three auth states, exact card/notice bounds and zero overflow; 984 desktop + 262 backend tests pass; public site remains intentionally old.
Touches: Production site Pricing CSS/JS/auth hydration.
Protect: Razorpay identifiers and billing semantics.

## P24 — Public website/release still old
Status: OPEN
Expected: Canonical/fallback sites and download metadata match the final immutable 1.4.19 build only after acceptance.
Latest evidence: Production site config/stable manifest still intentionally advertise 1.4.18.
Root cause: Release is deliberately withheld.
Fix: Synchronize only after final acceptance.
Evidence: `production/site/config.js` and `releases/stable.json`.
Touches: Release metadata/sites/updater.
Protect: No premature public release.

## P25 — Core installed feature smoke
Status: OPEN
Expected: Raw Dictation, Screen Reasoning, selected text, app/URL/file/folder/search, Settings, Account/Auth, Helper and updater all work together.
Latest evidence: Individual gates/past acceptance are green; final affected-build smoke is incomplete.
Root cause: Final release acceptance gap.
Fix: Bounded installed checks, reusing unaffected evidence.
Evidence: Final matrix pending.
Touches: Test execution; fix only proven defects.
Protect: Stable subsystem architecture.

## P26 — Long-duration mixed session
Status: OPEN
Expected: Extended mixed use has no stale state, duplicate speech/tasks/sockets, recognition degradation, leaks, reconnect loops or orphan reservations.
Latest evidence: Individual long Live evidence exists; final mixed-session sequence is incomplete.
Root cause: Final release acceptance gap.
Fix: One realistic installed sequence after source freeze.
Evidence: Final timeline/resources/accounting pending.
Touches: Whole installed product.
Protect: Avoid repeated paid sessions.

## P27 — Final security/reliability audit
Status: OPEN
Expected: No release-impacting auth/isolation/secret/input/Helper/updater/dependency/config/accounting defect.
Latest evidence: Earlier audits and gates are green; one bounded final review remains after fixes.
Root cause: Final release gate.
Fix: Review only material attack/failure paths and repair proven defects.
Evidence: Final report pending.
Touches: Whole project.
Protect: No speculative rewrite.

## P28 — Exact current installer/install provenance
Status: OPEN
Expected: Installed bytes derive from the frozen source and exact hashed installer.
Latest evidence: The installed `d278766...` package remains internally matched, but the accepted source fixes are newer and invalidate it as the final candidate.
Root cause: Release-integrity requirement.
Fix: Freeze the current source, rebuild once, install exact bytes and independently verify installer/executable provenance.
Evidence: Installed checkpoint hashes at the top of this file are retained only as the last known-good package.
Touches: Build/install/provenance.
Protect: Do not rebuild without a source defect.

## P29 — Final immutable release synchronization
Status: OPEN
Expected: Accepted SHA, installer, GitHub, updater, canonical/fallback sites, backend, Helper, CI and judge snapshot agree.
Latest evidence: Current source is unreleased; public stable remains 1.4.18.
Root cause: Release intentionally blocked by open acceptance items.
Fix: Publish once after all critical items reach required verification.
Evidence: Final independent hash/size and live endpoint checks pending.
Touches: Release/deployment metadata.
Protect: Ship one candidate only.

## P30 — Accounting and test-state cleanup
Status: OPEN
Expected: No active/orphan test task/session; reservations settle once or are validly reclaimable; no test secrets/config remain.
Latest evidence: No active Live/browser/TEST process or local QA server remains; temporary encrypted-key backup was deleted; historical holds and account totals still require final read-only reconciliation.
Root cause: Final cleanup/accounting gate.
Fix: Reconcile without changing billing rules or balances; clean temporary test state.
Evidence: Final backend/account report pending.
Touches: Test state and accounting inspection.
Protect: Billing math, plan allowance and existing balances.

## P31 — Referential direct-action context
Status: FIXED-SOURCE
Expected: A contextual close resolves the recent successfully opened target, asks once if ambiguous, and never broadens to unrelated windows.
Latest evidence: Real TEST Live now closes a single recent folder through `close it`/`close the one I just opened`, preserves both targets after an ambiguous singular reference, then closes only the clarified target; explicit `close File Explorer` remains broad.
Root cause: Successful open receipts lacked a canonical native target, and a multi-target tool batch did not expose its cardinality to the semantic owner.
Fix: Attach one generic referential target to successful opens, annotate multi-target batches at the existing result owner, and canonicalize folder paths to Explorer titles without foreground fallback.
Evidence: run21/run22 real Gemini Live + native Explorer proved single target, intervening conversation, two-target ambiguity, precise clarification and explicit broad close; 95 focused tests protect the boundary. Final production-installed acceptance pending.
Touches: Live tool instruction/context; recent action ownership.
Protect: Deterministic native Explorer close and general Gemini reasoning.

## P32 — Silence must preserve the logical Jarvis session
Status: FIXED-SOURCE
Expected: Silence alone never dismisses Jarvis or loses context; provider rollover/recovery stays internal until explicit exit.
Latest evidence: run16 kept one logical session through provider GoAway: socket 1 closed safely at 9m01s, socket 2 connected in 1.1s with the same logical ID and `resumed=True`, a later turn was accepted, and local goodbye closed it once.
Root cause: Provider-close continuation and buffered reconnect were gated to paid transport even though direct TEST has the same physical socket limit.
Fix: Keep logical-session authority provider-independent while leaving paid lease/account refresh behavior paid-only.
Evidence: 561 focused tests preserve paid rollover; run16 transport `9b02ac...` proves sequential recovery. A fully input-silent >10-minute final-installed run remains pending.
Touches: Logical session/provider-close UI boundary.
Protect: 720s lease, 120s grace, paid sequential resumption, audio/VAD.

## P33 — Production/TEST hotkey ownership
Status: VERIFIED-INSTALLED
Expected: Production owns its configured shortcuts; TEST owns only Ctrl+Alt+T and TEST cancel, and settings changes cannot cross that boundary.
Latest evidence: run16 writes only `%LOCALAPPDATA%\keyboard.wtf-test`, registers only TEST cancel, and physical Ctrl+Alt+, opened production Settings on port 8091; Ctrl+Alt+T launched TEST Live through the isolated launcher.
Root cause: TEST filtering exists only in initial registration; the settings replacement path bypasses it and registers production bindings.
Fix: Apply the same compile-time TEST allow-list at the single hotkey replacement owner, preserving both profile files.
Evidence: TEST log `02:15:48` shows only Ctrl+Alt+Shift+X; production settings SHA stayed `8506D843...`; the replayed reset could not take any production binding or mutate production/candidate profiles.
Touches: `HotkeyService` TEST seam and focused tests.
Protect: All production hotkey values and unrelated user settings.
