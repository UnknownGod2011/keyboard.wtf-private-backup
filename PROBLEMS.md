# keyboard.wtf 1.4.19 stabilization contract

Installed checkpoint: source `933c194ead8098b28dd974859510de6e5bb6d825`; installer `08B6D16DE3B64BB5CA0E5D9832243529B1C076E85777515EF1F8816BD942E326` (84,292,428 bytes); Helper 2.1.26 / protocol 2.1.21. Human evidence overrides internal evidence.

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
Latest evidence: Human heard duplicate Explorer completion; logs show FIBO opened at 09:04:40 and again at 09:04:48, while close-all executed once.
Root cause: A semantically duplicate provider retry received a normal second tool response; raw argument JSON ordering could also evade the turn guard.
Fix: Canonicalize tool arguments and mark required duplicate acknowledgements `SILENT`; retain one combined response per real batch.
Evidence: Latest human/action history plus 87 focused Live/order/UI tests.
Touches: Transcript pre-routing; tool dispatch/result publication; action dedupe.
Protect: Clarification/barge-in and multi-action batching.

## P03 — Jarvis popup topmost
Status: FIXED-SOURCE
Expected: A visible Jarvis overlay remains above ordinary apps without stealing focus.
Latest evidence: Human screenshot/session showed maximized Chrome covering Jarvis.
Root cause: Topmost was only a managed property; recreated native handles did not carry the topmost extended style.
Fix: Every overlay HWND now owns `WS_EX_TOPMOST | WS_EX_NOACTIVATE` directly.
Evidence: Focused native-style test passes; installed Chrome/Edge/Explorer acceptance pending.
Touches: `VoiceOverlayForm` window lifecycle.
Protect: No-focus-steal interaction and active-app typing.

## P04 — First-utterance farewell
Status: FIXED-SOURCE
Expected: A clear recognized farewell ends locally before tools, Browser Automation or model speech.
Latest evidence: Latest human `goodbye` failed while `go away` succeeded; another same-build run logged immediate local farewell.
Root cause: The local path was reliable only when input transcription arrived; a dedicated provider end tool was rejected when that independent transcript was absent.
Fix: Keep transcript-first local authority; after its bounded wait, accept the dedicated end tool only if no contradictory transcript exists.
Evidence: Human report plus focused ordering tests for transcript-first, absent-transcript fallback and contradictory-transcript rejection.
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
Status: VERIFIED-HUMAN
Expected: Context-now/action-later waits; fresh selection replaces stale; selected-text reasoning stays conversational.
Latest evidence: Earlier real installed acceptance passed all three; later changes did not replace the context owner.
Root cause: Duplicate pre-route/model ownership and stale selected-context reuse.
Fix: One fresh-context owner plus explicit deferred-action authority.
Evidence: Human form/context acceptance before current cancellation-only changes.
Touches: Command routing; selected context; Browser coordinator.
Protect: Direct actions and selected-text reasoning.

## P11 — Browser page/task ownership
Status: VERIFIED-HUMAN
Expected: Explicit current-page handoff resumes the same task and invalidates stale page actions/results.
Latest evidence: Earlier real installed acceptance passed same-task handoff and stale-result suppression.
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
Latest evidence: Real Store Helper task asked only for missing email, rejected unrelated conversation, accepted the natural answer, resumed the same task and completed the untouched local form visibly.
Root cause: The planner received accepted same-task guidance, while the action safety policy still evaluated only the original request.
Fix: Evaluate each planned action against the existing authoritative user-constraint context: original request plus only accepted same-task guidance.
Evidence: Isolated tasks `23d686...` (failure) and `6ef918...` (pass); 285 focused policy/coordinator tests.
Touches: Jarvis/Browser intervention bridge.
Protect: Normal conversation while paused.

## P14 — Browser cancellation in every state
Status: FIXED-SOURCE
Expected: Planning/Running/Waiting/NeedsUserAction/Partial cancel once, clear UI/task/intervention, and execute nothing stale.
Latest evidence: Source and exact installed deterministic tests pass; full physical state-matrix acceptance remains.
Root cause: Late mutation/result boundaries could outlive cancellation.
Fix: Single task cancellation authority propagated to executor, input and publication.
Evidence: 1,500 race repetitions/full gates at `7824b192...`; current-source real Helper NeedsUserAction task `9dd27a...` cancelled once with no write or intervention.
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
Status: VERIFIED-INSTALLED
Expected: Internal Settings sections, URLs/apps/search/mappings remain direct; explicit Browser Automation opt-out is authoritative.
Latest evidence: Installed routing tests pass Account & Billing/Browser Automation section ordering and opt-out clauses.
Root cause: Later feature words could override the requested local section or negation.
Fix: Preserve requested Settings target and explicit mechanism constraint.
Evidence: Full 922-test pass.
Touches: Direct-action intent/routing.
Protect: Browser Automation still starts for real interactive webpage work.

## P17 — Production Browser credit capacity
Status: OPEN
Expected: Final bounded production Browser acceptance can reserve and settle normally.
Latest evidence: Latest task reached Helper/page read then failed HTTP 402 at planner reservation; available credits are below the 25-credit reservation.
Root cause: Acceptance account capacity, not Helper/native messaging.
Fix: Use isolated non-shipping test capacity now; run bounded production acceptance when capacity exists.
Evidence: Task `eadf43...`; HTTP 402 log.
Touches: Test execution only.
Protect: Production billing rules and balances.

## P18 — Isolated Gemini/Jarvis/Browser test harness
Status: VERIFIED-INSTALLED
Expected: Separate local test process/profile exercises real coordinator/executor/Helper without production billing/provider/data mutation.
Latest evidence: Ignored local process exercised the real coordinator/executor with Store Helper 2.1.26 and Chrome while production app/auth/backend/billing/Firestore stayed unloaded.
Root cause: Production credit reservation blocks repeated acceptance.
Fix: Use an ignored process-only harness with an isolated local settings root and in-memory developer key; no production hotkey or product integration.
Evidence: Real page handoff, latest-by-publisher, NeedsUserAction/resume/form verification and cancellation tasks passed; final artifact/key scan remains P19.
Touches: Non-shipping test project/config only.
Protect: Production auth, provider, Firestore and billing.

## P19 — No test provider in shipping artifacts
Status: OPEN
Expected: Final source/package/install/site/logs contain no test keys, hotkey, provider selector, URL or billing bypass.
Latest evidence: Current tracked source, installer and installed binary scans are clean; new test harness has not been added.
Root cause: Preventive release requirement.
Fix: Keep harness outside shipping inputs and rescan after removal/isolation.
Evidence: Final tracked/artifact binary scans pending.
Touches: Packaging/release verification.
Protect: No BYOK or hidden production bypass.

## P20 — Long Smart Writing
Status: OPEN
Expected: Long and repeated writing, including after extended use, inserts once at the cursor, restores clipboard, clears overlay and settles once.
Latest evidence: Isolated 4m35s model request passed after timeout fix; exact installed UI/insertion lifecycle remains unverified.
Root cause: Earlier transport deadline was shorter than legitimate long generation.
Fix: Existing extended transport ownership; complete installed acceptance.
Evidence: `.artifacts/isolated-gemini/long-smart-writing.json`.
Touches: Smart Writing transport/UI/clipboard/settlement.
Protect: Existing writing quality.

## P21 — Learned mappings and workflows
Status: FIXED-SOURCE
Expected: A selected URL or valid hostname plus a custom trigger stores one durable Link that executes after conversation/app restart and outranks generic lookup.
Latest evidence: Exact installed `933c194...` again stopped after the workflow pre-route skip; no Link tool call or persistence event occurred and the old Gmail Link remained.
Root cause: The selected/deictic Link form still depends on optional model tool selection; unlike explicit-URL mappings, it has no deterministic local pre-route owner.
Fix: Route only an explicit single selected-Link mapping grammar through the existing Link tool owner, which captures, validates, normalizes and read-backs the selection once.
Evidence: Sessions `2db82030...` and `54e586...` prove the installed failures; 56 focused, 949 desktop and 262 backend tests plus release/security/Helper gates pass; installed acceptance remains pending.
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
Latest evidence: Unreleased desktop/tablet preview looks correct; 390px overflow and authenticated state need computed/real verification; public screenshot is old layout.
Root cause: Public production remains 1.4.18; mobile discrepancy not yet proven as DOM overflow.
Fix: Measure DOM bounds first; change only proven CSS/auth defect.
Evidence: Preview screenshots under `.artifacts/qa/site-preview-7824` and latest human public screenshot.
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
Latest evidence: The installed `7824b192...` package remains internally matched, but the accepted source fixes are newer and invalidate it as the final candidate.
Root cause: Release-integrity requirement.
Fix: Freeze the current source, rebuild once, install exact bytes and independently verify installer/executable provenance.
Evidence: Prior installer 84,276,700 bytes/SHA above is retained only as the last known-good checkpoint.
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
Latest evidence: No active Live/browser work; historical quarantined holds and current available/reserved totals require final read-only reconciliation.
Root cause: Final cleanup/accounting gate.
Fix: Reconcile without changing billing rules or balances; clean temporary test state.
Evidence: Final backend/account report pending.
Touches: Test state and accounting inspection.
Protect: Billing math, plan allowance and existing balances.
