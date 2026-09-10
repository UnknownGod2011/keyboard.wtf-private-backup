# keyboard.wtf 1.4.19 stabilization contract

Installed checkpoint: source `e01f33a1476f4a016230d60936ec590e1d7c6e41`; installer `1EE1B60203BB277A24A7BDEB5771D57CE8BA8793A2885B8AA10CAD7D95A95D8F` (84,274,021 bytes); installed executable `DE6B25AF596514641C0E4904EC74001786C7A14C1428CED6EA10CBAE2D236798` (314,781,134 bytes); Helper 2.1.26 / protocol 2.1.21. Human evidence overrides internal evidence.

## P01 — Startup consistency
Status: FIXED-SOURCE
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
Status: VERIFIED-INSTALLED
Expected: A visible Jarvis overlay remains above ordinary apps without stealing focus.
Latest evidence: Exact installed overlay retained `WS_EX_TOPMOST | WS_EX_NOACTIVATE` while Chrome kept foreground focus; prior TEST proof covered Chrome, Edge and Explorer transitions.
Root cause: Topmost was only a managed property; recreated native handles did not carry the topmost extended style.
Fix: Every overlay HWND now owns `WS_EX_TOPMOST | WS_EX_NOACTIVATE` directly.
Evidence: Focused native-style test plus TEST multi-app proof and installed HWND `2296258` style verification.
Touches: `VoiceOverlayForm` window lifecycle.
Protect: No-focus-steal interaction and active-app typing.

## P04 — First-utterance farewell
Status: FIXED-SOURCE
Expected: A clear recognized farewell ends locally before tools, Browser Automation or model speech.
Latest evidence: A late-session TEST `goodbye` ended the active logical session once, but exposed a Browser Partial-task cancellation announcement attempting local speech after Live shutdown.
Root cause: The end tool lacked an absent-transcript fallback, and conversation shutdown reused the user-visible Browser cancellation path.
Fix: Keep transcript-first local authority with the bounded end-tool fallback, and make conversation-owned Browser cleanup silent.
Evidence: 473 focused tests plus rebuilt TEST session `ebcaa5...`: one socket shutdown, empty context, cancelled Browser state, and no model/local/TTS response; final production microphone proof pending.
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
Expected: Cancelled work cannot later open the microphone, post native input, publish results or overwrite a newer task.
Latest evidence: Exact installed `821a12f...` cancelled during microphone startup, discarded the late open, emitted no Listening/recording state, and cleared the overlay.
Root cause: Capture ownership was published only after blocking microphone startup, so cancel could not invalidate the in-flight start.
Fix: Make microphone startup hold the existing operation generation and discard/close any start that loses it.
Evidence: Failing trace at 23:37:47–23:37:55; passing installed trace at 23:53:32–23:53:33; 1,024 desktop tests and prior 1,500 race repetitions.
Touches: Voice capture operation ownership.
Protect: Native/input/Live cancellation checks and fast voice-mode startup.

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
Latest evidence: Real Helper task `02f3e726...` asked once for an optional field, accepted the internal skip on the same task, executed zero browser actions and completed with the live field still blank.
Root cause: Planner-required labels were not reconciled with exact live optional metadata, and a repeated skipped intervention could loop because a no-mutation outcome had no terminal receipt.
Fix: Reconcile exact field metadata and let the task owner verify a single-field authorized no-op only when that field remains unchanged on the same tab with zero action receipts.
Evidence: 284 focused tests plus TEST Gemini/Store Helper/Chrome task `02f3e726...`; AX read-back proved blank field and no submit. Final production-installed acceptance pending.
Touches: Jarvis/Browser intervention bridge.
Protect: Normal conversation while paused.

## P14 — Browser cancellation in every state
Status: FIXED-SOURCE
Expected: Planning/Running/Waiting/NeedsUserAction/Partial cancel once, clear UI/task/intervention, and execute nothing stale.
Latest evidence: Rebuilt TEST task `85894bf…` reached Partial, accepted "Cancel Browser Automation now" locally, cleared intervention/cancel authority and remained Cancelled after a delayed check.
Root cause: Late mutation/result boundaries could outlive cancellation; additionally, the locally authoritative parser rejected ordinary urgency modifiers after an explicit Browser-task target.
Fix: Keep one cancellation authority through executor/input/publication and accept ordinary urgency modifiers in the existing explicit task-control grammar.
Evidence: 1,500 race repetitions/full gates at `7824b192...`; run6 covers active states; 280 protocol tests and real Store-Helper Partial cancellation task `85894bf…` pass. Exact production-install verification pending.
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
Status: VERIFIED-INSTALLED
Expected: Final source/package/install/site/logs contain no test keys, hotkey, provider selector, URL or billing bypass.
Latest evidence: Tracked source, package, installer and exact installed executable contain no TEST symbol/provider/hotkey or supplied test-key material.
Root cause: Preventive release requirement.
Fix: Remove the disposable seam after acceptance and scan tracked source plus final package/install independently.
Evidence: Full secret gate plus package, installer and installed-tree scans at `e01f33a...`; only expected public Firebase browser configuration remains on the sites.
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
Latest evidence: Exact installed `e01f33a...` is signed in, owns all five production hotkeys, opens the microphone diagnostic, connects only Store Helper, preserves mappings/workflows, and reports the updater current; final human voice/writing smoke remains.
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
Status: VERIFIED-INSTALLED
Expected: Installed bytes derive from the frozen source and exact hashed installer.
Latest evidence: Installer `1EE1...5D8F` installed source `e01f33a...`; packaged and installed executables are byte-identical at `DE6B...6798`.
Root cause: Release-integrity requirement.
Fix: Freeze source, build once, hash, install exact bytes and independently verify provenance.
Evidence: Local and private-CI clean-profile runtime gates passed; installer exit 0, 84,274,021-byte installer and 314,781,134-byte executable independently hashed.
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
Latest evidence: Runtime/test state is clean; seven expired Live reservations are explicitly quarantined and exactly equal the 10.7325-credit reserved balance.
Root cause: Pre-`bd1d248` Live shutdown cancelled in-flight Firestore budget/checkpoint transactions after provider dispatch, leaving no final completion evidence.
Fix: Preserve the deployed shutdown-safe accounting fix; resolve historical quarantines only through audited operator reconciliation with provider evidence.
Evidence: Firestore/account totals match exactly; all seven traces show cancelled accounting RPCs, and the healthy six-hour maintenance job reports seven quarantines rather than silently releasing them.
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
Latest evidence: Human saw Reconnecting for roughly 130s; rebuilt TEST recovered the same logical session in 1.70s, cleared Reconnecting immediately, and recalled pre-rollover context.
Root cause: Sequential transport recovery restored socket/session ownership but did not clear the reconnect UI revision.
Fix: Keep provider-independent logical recovery and restore Listening only when the reconnect revision still owns the overlay; never overwrite newer speech/tool UI.
Evidence: Rebuilt TEST session `1df62f...` logged UI clear 2ms after recovery and retained the pre-rollover codeword; revision-race tests pass. Final production-installed proof pending.
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

## P34 — Active Browser task guidance and failure context
Status: FIXED-SOURCE
Expected: Only relevant answers/corrections/guidance reach the same live task; terminal tasks lose routing authority, while concise diagnostic context still explains the latest failure.
Latest evidence: Rebuilt TEST accurately explained the latest real Helper task failure without creating, reviving or mutating a Browser task.
Root cause: A retrospective Browser Automation mention was treated as explicit start, and the task owner retained only the newest terminal snapshot.
Fix: Route outcome questions to read-only status, retain four concise terminal outcomes in the existing owner, clear them at conversation end, and keep terminal guidance rejection.
Evidence: Real task `2c8f97...` stayed Partial with zero actions while its follow-up explained the missing exact control; 473 focused tests pass. Final production-installed proof pending.
Touches: Browser coordinator guidance guard; Live tool instructions.
Protect: Unrelated Jarvis turns, direct browser actions, cancellation and dead-task cleanup.

## P35 — Bounded personal memory
Status: VERIFIED-INSTALLED
Expected: Store only explicit/useful concise non-sensitive facts and retrieve a small relevant subset; expose view/delete/clear, adding a disable toggle only if it remains a single clean owner.
Latest evidence: Exact installed Settings exposes the existing bounded memory owner separately from four mappings and two workflows; one durable entry is loaded within a 20-entry/280-character cap.
Root cause: This was an acceptance gap, not a missing architecture; the existing owner already enforces explicit save intent, bounded retrieval and same-key update.
Fix: Keep the existing owner and avoid a late competing memory system; no separate disable state was added because memory is explicit-only and clearable.
Evidence: 29 focused persistence/dedupe/relevance/privacy tests pass; prompt digest is bounded to 10 entries/900 characters and installed Settings supports view/delete/clear.
Touches: Existing intent memory and Settings only.
Protect: Startup latency, selected-text privacy, mappings/workflows and prompt bounds.

## P36 — Routine OS notification spam
Status: VERIFIED-INSTALLED
Expected: Routine success stays in Jarvis UI/speech; only attention-worthy completion, warning or failure creates an OS notification.
Latest evidence: Exact installed `e01f33a...` contains the single notification boundary; routine success has no system-publication path while explicit attention/failure retains one.
Root cause: `NotificationService.Info` and `InfoTransient` unconditionally publish a Windows balloon regardless of purpose.
Fix: Make routine information silent at the system boundary and require explicit attention severity for useful non-error notifications.
Evidence: 3 focused boundary tests, 1,027 desktop, 262 backend and 12 Firestore tests pass; private CI `34424810051`, clean-profile runtime, byte-identical install and no-bypass scan are green.
Touches: Notification publication boundary; timer and long Browser-task completion classification.
Protect: Jarvis status/speech, actionable NeedsUserAction/failure notices and persistent logging policy.
