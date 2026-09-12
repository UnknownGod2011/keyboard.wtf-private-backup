# keyboard.wtf 1.4.19 stabilization contract

Rejected human-test checkpoint: source `785a48a15c627cb3139715a56e857305769b78e8`; installer `623DBFB2A31B12FB34F14E7E8F6977DC6929919FD078BF5771E936F2A7E3B878` (84,274,021 bytes); installed EXE `DE6B25AF596514641C0E4904EC74001786C7A14C1428CED6EA10CBAE2D236798`. Helper 2.1.26 / protocol 2.1.21. Human evidence overrides receipts. Logs preserved in `dist/verification/human-20260910-rejected-785a48a`. Keep the repaired release private until explicit human approval; public download must temporarily use the original signed 1.4.17 artifact.

## P01 — Startup consistency
Status: OPEN
Expected: Jarvis normally reaches Listening near 2.5–3s, with reliable cold/warm/restart behavior and no unexplained failures.
Latest evidence: Latest human sessions measured 3.482s / 2.909s; token refresh 0ms, backend 2.225s / 2.063s. No unexplained local 3s wait in these samples; assess consistency without changing audio/VAD.
Root cause: Slow sample occurred after the app idled past access-token expiry, adding a 2.936s refresh to the normal backend/provider handshake.
Fix: Refresh signed-in access tokens shortly before expiry in the background; keep the existing on-demand authenticated fallback.
Evidence: `keyboard.wtf.log` sessions `144b85...` and `cf9358...`.
Touches: Paid account refresh; Live startup transport.
Protect: 2.37–3s happy path, microphone readiness, reliable connection.

## P02 — Exactly-once completion and action ownership
Status: FIXING
Expected: One logical request may execute several actions but produces one coherent completion and no repeated mutation.
Latest evidence: Failing→passing tests prove transcript-only mutation, cancelled/superseded request execution, scalar re-publication and receipt expiry within one logical request. Installed duplicate speech still requires replay.
Root cause: Ordinary effects ran before a model tool owned their result; scalar receipts bypassed acknowledgement, and a 12s cache expiry also released still-current result authority.
Fix: Keep observation read-only; execute through the existing tool dispatcher, acknowledge every receipt once, and release route authority on ordered turn completion/new input rather than a same-turn timeout.
Evidence: 1,080 desktop/262 backend pass; isolated same-model success/failure probes choose saved-Link tools and ground their replies in returned outcomes. Full installed audible replay and multi-target follow-up remain pending.
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
Status: OPEN
Expected: A clear recognized farewell ends locally before tools, Browser Automation or model speech.
Latest evidence: A late-session TEST `goodbye` ended the active logical session once, but exposed a Browser Partial-task cancellation announcement attempting local speech after Live shutdown.
Root cause: The end tool lacked an absent-transcript fallback, and conversation shutdown reused the user-visible Browser cancellation path.
Fix: Keep transcript-first local authority with the bounded end-tool fallback, and make conversation-owned Browser cleanup silent.
Evidence: 473 focused tests plus rebuilt TEST session `ebcaa5...`: one socket shutdown, empty context, cancelled Browser state, and no model/local/TTS response; final production microphone proof pending.
Touches: Live transcription/event ordering; session stop.
Protect: Audio/VAD/barge-in and immediate local shutdown.

## P05 — Conversation/random-action safety
Status: OPEN
Expected: Conversation never mutates the computer; explicit actions execute once.
Latest evidence: Previous installed safety evidence retained; P02 changes the shared dispatch boundary, so affected installed replay is required. New tests prove observation alone cannot mutate or persist.
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
Status: FIXED-SOURCE
Expected: Cancelled work cannot later open the microphone, post native input, publish results or overwrite a newer task.
Latest evidence: Earlier installed startup-cancel proof remains valid, but recorder drain/recovery now changed this dependency. New failing-then-passing test prevents interrupted drain from overwriting Cancelled; affected installed replay is required.
Root cause: Capture ownership was published only after blocking microphone startup, so cancel could not invalidate the in-flight start.
Fix: Make microphone startup hold the existing operation generation and discard/close any start that loses it.
Evidence: Failing trace at 23:37:47–23:37:55; passing installed trace at 23:53:32–23:53:33; 1,024 desktop tests and prior 1,500 race repetitions.
Touches: Voice capture operation ownership.
Protect: Native/input/Live cancellation checks and fast voice-mode startup.

## P09 — Loud/full-scale PCM processing
Status: FIXED-SOURCE
Expected: Valid loud PCM never throws or drops microphone chunks.
Latest evidence: Installed Smart Writing microphone callbacks failed at 09:00:27 and 09:04:44 on September 10, leaving Listening visible.
Root cause: Shared recorder and Settings meters still call Math.Abs(short.MinValue), which throws; previous Live arithmetic fix did not cover these paths. Recorder failure is only logged, not delivered to the capture owner.
Fix: Shared overflow-safe metering; existing capture monitor processes interrupted audio; await final input callback before WAV creation and retain cancellation ownership across drain.
Evidence: All PCM16 values plus drain/tail/interruption/cancel tests pass. Real short capture drains cleanly; 823s prerecorded desktop replay and 412s interrupted recovery each paste once. Long installed microphone acceptance remains open.
Touches: AudioRecorderService, VoiceCaptureService, WebSettingsService.
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
Status: OPEN
Expected: Explicit current-page handoff resumes the same task and invalidates stale page actions/results.
Latest evidence: Real isolated Selenium→TestPages task `81b15ff4...` adopted the explicitly selected new page on the same task, wrote only there, completed, and rejected terminal guidance; old page remained untouched.
Root cause: Task remained pinned to an obsolete tab generation.
Fix: Explicit re-grounding advances page generation and rejects older work.
Evidence: Human acceptance plus regression tests.
Touches: Browser coordinator/runtime generation.
Protect: Never adopt unrelated foreground tabs automatically.

## P12 — Form write correctness
Status: FIXED-SOURCE
Expected: Context values target the correct group once; replace/append explicit; no duplicate writes or unrequested Clear/Reset. Release proof spans different real-site DOMs, not fixtures alone.
Latest evidence: Current source/current isolated Helper completed real Selenium and TestPages forms; native Reset→repeat passed. Exactly 4/3/3 input events, no duplicate writes, untouched unrelated fields, zero submits, terminal Helper/UI cleanup verified.
Root cause: Exact unnamed controls scored 42 against a 45-point fuzzy-name threshold; adjacent visible labels were absent from the graph. Earlier write/group fixes remain protected.
Fix: Keep exact field/role ownership independent of fuzzy-name score; capture bounded adjacent legacy labels without borrowing other controls' text. Field/generation/cancellation checks stay enforced.
Evidence: Earlier human Google Form proof retained. Final installed/Store matrix must cover multiple public/demo/user-controlled forms: navigation/destination, handoff, corrections, required/optional/groups, pause/resume/cancel, continuity and visible state. Authorized Clear/Reset→repeat only where safe; no submissions.
Touches: Helper metadata; planner/executor/verification.
Protect: Do not submit test forms.

## P13 — NeedsUserAction bridge
Status: FIXED-SOURCE
Expected: Ask only for genuinely missing required data; relevant natural answer resumes the same task; optional skip works; unrelated talk is isolated.
Latest evidence: Real TestPages task `07f29f79...` asked once, accepted optional skip on the same task, completed with default text unchanged and zero writes/submits. Cross-site handoff and subsequent cancellation also passed.
Root cause: `973ef70` equated nonempty with the requested answer; exact optional-label matching also incorrectly required printed trailing punctuation.
Fix: Suppress only explicit current-plan expected states proved by the existing verifier; normalize trailing label punctuation while retaining unique-field/protected-step checks. Preserve authorized unchanged-field skip.
Evidence: Both failing real traces preserved in `dist/verification/p13-*`; 342 focused cases pass, including failing→passing default-value and punctuation tests. `output/playwright/browser-real-guidance` proves corrected isolated source/Helper flow; final installed/Store replay pending.
Touches: Jarvis/Browser intervention bridge.
Protect: Normal conversation while paused.

## P14 — Browser cancellation in every state
Status: FIXED-SOURCE
Expected: Planning/Running/Waiting/NeedsUserAction/Partial cancel once, clear UI/task/intervention, and execute nothing stale.
Latest evidence: Real isolated Chromium/native-bridge test reproduced a write after desktop cancellation when the renderer resumed at the mutation boundary; worker heartbeat stayed healthy and its task lease was already cleared.
Root cause: Cancellation was checked before queuing the renderer commit, not at actual page mutation; queued work could outlive the worker/desktop waiter. Earlier human control/publication defects also remain in this coherent repair.
Fix: Parse existing control clauses, reach the existing coordinator immediately, queue only the receipt, and validate task/session/socket/turn ownership at publication; background notices cannot interrupt a live user turn.
Evidence: Real isolated Chromium/native-bridge failing→passing paused-renderer replay; cancelled value never written, fresh task/replace/append/repeated groups pass with zero submit/reset. Source rejects stale Helper/content capabilities and wrong renderer identity. Desktop 1,071/backend 262 pass; installed Store/Jarvis acceptance remains open.
Touches: Browser coordinator/executor; Live/input boundary.
Protect: Jarvis remains usable after cancel.

## P15 — Browser correctness constraints and visible completion
Status: FIXED-SOURCE
Expected: Latest-by-publisher verifies publisher/freshness; useful page is preserved; Completed matches visible state.
Latest evidence: Failing Selenium task `db996700...` preserved; repaired task `bd0b5de...` and two TestPages tasks reached Completed with independently matching field values and no submit/reset mistakes.
Root cause: Verifier counted action words in labels as goals; Helper matched a select's stored value against its visible label. Both defects also exist in 1.4.17.
Fix: Existing planner declares bounded final field checks; verifier checks every state on the current page, retaining earlier relevant receipts. Select evidence distinguishes selected label/value from available choices. Preserve first-navigation readiness fix.
Evidence: `output/playwright/browser-real-sites`; 1,113 desktop/274 backend test cases pass; 11 adversarial field-state tests and Helper identity/label/option tests pass. Schema reduced after real-provider rejection, then both sites passed twice. Installed/Store acceptance pending.
Touches: Browser planner/coordinator/verifier.
Protect: Generic behavior; no site-specific rules.

## P16 — Direct Settings and Browser opt-out
Status: OPEN
Expected: Internal Settings sections, URLs/apps/search/mappings remain direct; explicit Browser Automation opt-out is authoritative.
Latest evidence: Full TEST Jarvis incorrectly started Browser Automation for a direct request to switch to the last Chrome tab.
Root cause: Browser-tab grammar was duplicated; both the direct parser and classifier missed a browser qualifier after the ordinal, so generic page-section routing won.
Fix: Make the direct browser parser the classifier's single authority and accept an optional browser qualifier around an ordinal tab selection.
Evidence: Failing task `7e87ce...`; current TEST physically switched the second Chrome tab, searched Devpost once, opened a URL under explicit BA opt-out, and opened internal Settings with no Browser task; 460 focused tests pass. Final installed verification pending.
Touches: Direct-action intent/routing.
Protect: Browser Automation still starts for real interactive webpage work.

## P17 — Production Browser credit capacity
Status: VERIFIED-INSTALLED
Expected: Final bounded production Browser acceptance can reserve and settle normally.
Latest evidence: User restored capacity; latest installed human session established paid Live and received successful HTTP 200 Browser plans. Later HTTP 409 rate limit is not insufficient-credit failure.
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
Status: OPEN
Expected: Roughly 10-minute-plus capture has no arbitrary app cutoff; successful audio is preserved as ordered full or clearly partial output, inserted once, with correct clipboard/UI/settlement.
Latest evidence: Installed human microphone froze twice through P09; a 605s prerecorded TEST pass did not exercise that callback. Reverify real capture, processing, interruption, tail preservation, one insertion, clipboard and terminal UI.
Root cause: A five-minute desktop cap and whole-request provider size/deadline made valid long capture impossible and discarded provider-failure results.
Fix: Remove Smart Writing auto-cap, allow long/manual Dictation, segment cloud audio under one operation, and recover captured text without duplicate insertion.
Evidence: 561 focused tests cover segmentation/cancellation/recovery; TEST desktop operations `604887910331` and `3c920af06fb1` ended Done with one paste each. Final exact installed microphone/settlement acceptance remains pending.
Touches: Smart Writing transport/UI/clipboard/settlement.
Protect: Existing writing quality.

## P21 — Learned mappings and workflows
Status: FIXING
Expected: A selected URL or valid hostname plus a custom trigger stores one durable Link that executes after conversation/app restart and outranks generic lookup.
Latest evidence: Durable selected-hostname mapping and execution remain proven, but newest human run executed a mapping while Jarvis denied knowing its destination. Reopen result/context ownership; preserve durable Link storage.
Root cause: Eager local mapping execution could precede a model refusal; the open_url schema hid its existing alias capability and setup advertised workflows but not Links.
Fix: Resolve/save mappings inside the existing tool execution owner; advertise bounded Link aliases without target data, and return the resolved URL as authoritative follow-up context. Persistence architecture is unchanged.
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
Status: OPEN
Expected: Compact hero, card/notice aligned, FAQ then reviews, no real mobile overflow, and signed-out/no-plan/active-plan hydration without re-sign-in.
Latest evidence: Canonical card/notice bounds align at 390/768/1440px without overflow. September 12 real Account/Google flow is signed out in the controlled browser; requested account login is required for no-plan/active-plan hydration proof.
Root cause: Old deployed Pricing shell differed from corrected source; real authenticated-state failure still needs tracing.
Fix: Deployed existing shell alignment/session-hydration correction; app.js, account.js and desktop-login.js are byte-equivalent to prior deployment. Payment/auth logic unchanged.
Evidence: Canonical deployed signed-out DOM verified at 390/768/1440px; prior preview/mock auth evidence does not prove real signed-in states. Public binary is temporarily original 1.4.17.
Touches: Production site Pricing CSS/JS/auth hydration.
Protect: Razorpay identifiers and billing semantics.

## P24 — Public website/release still old
Status: VERIFIED-INSTALLED
Expected: Public canonical/fallback/download/updater/GitHub latest use the original verified 1.4.17 installer until a repaired candidate is manually accepted.
Latest evidence: Canonical and fallback live config/signed manifests plus GitHub latest now advertise 1.4.17. Actual canonical Download CTA downloaded 84,251,836 bytes matching original SHA 812aaa43...ded6cd; local 1.4.19 install preserved.
Root cause: User rejected newer candidate and explicitly requested temporary public rollback only.
Fix: Restore original signed download metadata, preserve local repair branch/install, verify actual public downloaded bytes; never rebuild/relabel 1.4.17.
Evidence: Deployment `site-1kxoy9cdc`; original signature validated by production verifier, hashes independently matched, replay/downgrade rejected for installed 1.4.17/18/19; `dist/verification/baseline-1.4.17`.
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
Latest evidence: One bounded final review remains. A prior hotkey-test teardown timeout has not recurred in four full desktop runs; its cause is still unproven and diagnostic phase reporting remains enabled.
Root cause: Final release gate.
Fix: Review only material attack/failure paths and repair proven defects.
Evidence: Final report pending.
Touches: Whole project.
Protect: No speculative rewrite.

## P28 — Exact current installer/install provenance
Status: VERIFIED-INSTALLED
Expected: Installed bytes derive from the frozen source and exact hashed installer.
Latest evidence: Frozen `785a48a...` installer `623D...878` and installed EXE `DE6B...6798` remain unchanged. This proves rejected-candidate provenance, not repair acceptance.
Root cause: Release-integrity requirement.
Fix: Freeze source, build once, hash, install exact bytes and independently verify provenance.
Evidence: Local and private-CI clean-profile runtime gates passed; installer exit 0, 84,274,021-byte installer and 314,781,134-byte executable independently hashed.
Touches: Build/install/provenance.
Protect: Do not rebuild without a source defect.

## P29 — Final immutable release synchronization
Status: OPEN
Expected: Accepted SHA, installer, GitHub, updater, canonical/fallback sites, backend, Helper, CI and judge snapshot agree.
Latest evidence: Repair source is unreleased; original 1.4.17 is now the temporary public stable. New release requires explicit manual approval.
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
Latest evidence: Real isolated Live model, dispatcher, registry, Helper and Chrome opened/closed two-tab and four-tab workflow sets with one combined result per request; intervening conversation retained target context. Installed audible replay remains pending.
Root cause: Loss of structured outcome/target identity at workflow and batch publication boundaries; a URL is not an exact opened-tab handle.
Fix: Retain successful workflow targets, annotate only uncancelled published results, and use the existing Helper command channel for Chrome tab handles/verified close sets without BA or foreground fallback.
Evidence: `output/playwright/direct-tab-context/model-evidence.json`; exact tab read-back, manual-close/cancel/reset probes and full 1,108 desktop gate pass. No production credits or native-window changes; final installed proof pending.
Touches: Live tool instruction/context; recent action ownership.
Protect: Deterministic native Explorer close and general Gemini reasoning.

## P32 — Silence must preserve the logical Jarvis session
Status: OPEN
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
Latest evidence: Real isolated Live/registry/Helper task `ab1444a7...` remained paused on its form during one direct URL open, accepted skip on the same task, completed without writes/submission, and accurately explained the outcome. One result batch per turn.
Root cause: Tool execution still let active-task heuristics override a semantically selected direct tool; URL punctuation plus broad page-word overlap could qualify as field guidance.
Fix: Let only Browser start/control tools enter active-task guidance; keep immediate local cancellation and read-only recent outcome context. No additional phrase grammar.
Evidence: Failure `27c075eb...` preserved in `dist/verification/p34-*`; corrected model trace in `output/playwright/browser-model-guidance`; 355 focused cases pass. Muted audio/text input is not installed microphone proof; final installed replay pending.
Touches: Existing CommandRegistry tool-routing boundary; Browser coordinator.
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

## P37 — Long Raw Dictation and capture failure recovery
Status: FIXED-SOURCE
Expected: Meaningful long input preserves all captured audio/tail, returns usable partial output on failure, inserts once, and leaves no stale UI.
Latest evidence: 823s/70-section prerecorded desktop replay and forced 412s interruption retain 70/35 sections, paste once at the cursor, preserve clipboard and clean UI. Short real speaker→mic recognition remains unreliable; final microphone proof pending.
Root cause: Engine failures return empty text; exception-only preview recovery never sees them.
Fix: Recover the current Raw preview at the existing completion owner for empty final results too; never recover executable commands.
Evidence: Empty-result regression fails→passes; 63 focused tests. `dist/verification/hardware-audio/long-control/desktop*.json`. Production retains its saved 30s Raw limit; isolated replay used existing manual/unlimited mode without changing that profile.
Touches: AudioRecorderService, VoiceCaptureService, delivery/clipboard.
Protect: No arbitrary duration cap; no duplicate settlement or paste.

## P38 — Natural audio, barge-in and prompt/tool intelligence
Status: OPEN
Expected: Smooth speech/playback; compact consistent prompt/tools; semantic app/file/tab references and practical tool composition.
Latest evidence: Both versions use 59 tools/same Live model; system text 13,133→15,166 chars, tools 23,867→25,147 bytes. Growth is not causation. Combined-frame stress test is defensive only: Google's documented server union does not promise such frames.
Fix: Pending proven differential causes; no phrase grammars or competing owners.
Evidence: Latest preserved log/history; full differential required.
Touches: Live prompt/tools/audio/dispatch and recent action context.
Protect: Safe native actions, bounded memory, sequential resumption.

## P39 — Intermittent Helper availability
Status: FIXED-SOURCE
Expected: Direct tabs/Browser tasks share the real Store Helper reliably; failures are truthful and cancellable.
Latest evidence: Real isolated Chromium kept native heartbeat healthy while the renderer was debugger-paused; cancellation cleared the old lease and the next task worked. A separate old-heartbeat race initially cleared a newly adopted lease and is now regression-tested.
Root cause: One poll awaits page status and full command execution before the next heartbeat; an unresponsive page blocks liveness and cancellation delivery.
Fix: Existing poll owner keeps heartbeat alive, bounds page waits and validates original port/command; page mutation rechecks the same desktop waiter. Reset clears old leases, and old heartbeat/cleanup cannot revoke a resumed lease.
Evidence: Worker races plus real isolated native messaging/renderer checks. Source Helper 2.1.27 / same protocol and identity; stale Store/content paths fail with update/refresh guidance. Installed Store 2.1.26 remains unchanged; final Store acceptance required.
Touches: Helper communication and action ownership only if proven.
Protect: Protocol 2.1.21, native trust/registration, original 1.4.17 compatibility; do not publish unaccepted Helper bytes.

## P40 — Screen Analysis and provider-neutral UI
Status: FIXED-SOURCE
Expected: Installed screen analysis works; ordinary progress says analyzing/polishing without internal provider branding.
Latest evidence: VoiceCaptureService exposes 'Gemini is cleaning up your words'; full shipping UI search and screen acceptance pending.
Fix: Removed provider branding from ordinary screen/writing/command progress and Jarvis voice labels; diagnostic model/config/log identities retained. Full screen acceptance pending.
Evidence: Actual installed screen/writing UI proof required.
Touches: User-facing resources/progress UI.
Protect: Internal logs, billing labels/prices and provider routing.

## P41 — Bounded model and long-term cost audit (after core repair)
Status: OPEN
Expected: Inventory every actual AI route; benchmark compatible alternatives only after architecture stabilizes; use verified January 2027 standard rates including thinking/output usage.
Latest evidence: User requested later evaluation including Gemini 3.8 Flash; availability, compatibility and pricing not yet verified.
Fix: Deferred audit; change model only on representative task/latency/reliability/cost evidence, preserve Live native audio and customer billing unless proven internal metering adjustment is necessary.
Evidence: Actual route/config inventory, official pricing and comparative task receipts required.
Touches: Model configuration only if justified.
Protect: Regression priority, billing math/reservation/settlement/history/exactly-once charge.

## P42 — Real payment identity and canonical Razorpay domain
Status: OPEN
Expected: Latest ₹999 purchase belongs exclusively to the authenticated Firebase UID for the user-designated account; customer-facing payment output uses keyboardwtf.com.
Latest evidence: User reports purchase under unknowngod2024@gmail.com and two Razorpay emails displaying the fallback Vercel domain; ownership/domain cause not yet verified.
Fix: At account/payment acceptance, trace UID→customer/subscription/payment→webhook→entitlement/credits→restart; inspect real merchant/checkout/callback configuration and output before changing it.
Evidence: Read-only production chain and actual customer-facing output required; do not generate another charge without authorization.
Touches: Existing payment identity/config boundary only if proven.
Protect: Firebase UID authority, account isolation, internal infrastructure URLs and existing billing semantics.
