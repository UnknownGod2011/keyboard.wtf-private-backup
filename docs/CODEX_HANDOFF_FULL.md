# keyboard.wtf — authoritative recovery handoff
## Read this first: scope, authority, and stop conditions

Prepared **2026-09-16**, Windows/Asia–Kolkata, from the real private repair worktree, installed bytes, local evidence, Git history/diffs, and bounded read-only production checks. This handoff run is **analysis/documentation only**. No new product fixes, builds, installs, payment changes, deployments, or release publication were authorized in this run. The pre-existing dirty P15 source/test edits are preserved.

**The product is NOT public-ready and NOT final-human-acceptance-ready.** A passing test suite is not a passed user flow. User manual failures override older green records. The next implementation thread must finish remaining problems one at a time, not start another broad rewrite.

**Public stable MUST remain the original 1.4.17 artifact until the user explicitly accepts a repaired candidate.** “Make it public ready” is not permission to publish before manual approval. Stop at FINAL HUMAN ACCEPTANCE READY with exact candidate hashes and a short manual checklist; publish only after subsequent explicit approval.

This document is self-contained technical context, not a claim that every historical receipt has been rerun. Evidence labels:
- **NOW**: independently read/verified during this handoff.
- **SOURCE**: actual source or deterministic test establishes a bounded property.
- **INSTALLED**: real packaged executable/effects/provider/speaker evidence, with limitations stated.
- **HUMAN**: user's physical microphone/UI report; authoritative for perceived behavior.
- **HISTORICAL**: preserved evidence from an older run; not fresh validation of every dependency.
- **HYPOTHESIS**: plausible but not established. Never convert this to a root cause without reproduction.

The full numbered ledger below retains all 44 original P IDs. Status words in the old ledger are coarse: FIXED means source corrected, not necessarily physically closed; PHYSICALLY VERIFIED can describe an older unchanged subsystem, not the whole current candidate. Handoff corrections override stale snapshot fields. Keep PROBLEMS.md concise when implementation resumes; do not copy this long packet back into it.

## 1. Exact checkpoint and machine/release identity

### Workspaces — do not confuse them

Authoritative repair checkout:
`C:\Users\Admin\OneDrive\Desktop\keyboard.wtf-paid-staging`
- Branch: `paid-staging`.
- HEAD **5fddef4068b45677d7aa209203d9d22790d94c14**.
- Private origin: https://github.com/UnknownGod2011/keyboard.wtf-private-backup.git.
- Version: **1.4.19**; several very different candidates reuse this version, so VERSION ALONE IS NOT IDENTITY.
- Default thread directory `C:\Users\Admin\OneDrive\Desktop\keyboard.wtf` is a DIFFERENT public checkout. Do not edit/reset it accidentally.
- Detached reference worktree: `dist/verification/reference-1.4.17` at `6a2c0fc...`.
- Older separate TEST worktree: `C:\Users\Admin\OneDrive\Desktop\keyboard-wtf-test-p34`, detached `442b3b6`. It is not current production source.

Pre-handoff dirty changes (NOW, preserve):
1. PROBLEMS.md: 15 added/15 removed lines of evidence/status updates.
2. src/Services/BrowserAutomationV2/BrowserAutomationVerifier.cs: 6 added/1 removed lines, P15 extraction correction.
3. tests/KeyboardWtf.Tests/BrowserAutomationVerifierTests.cs: 40 added lines, P15 regressions.
This handoff adds only docs/CODEX_HANDOFF_FULL.md. Do not commit the existing repair merely because this file exists; finish review/installed proof.

### Exact installed/private package (NOW hashes independently recomputed)

| Item | Identity |
|---|---|
| Installed EXE | `C:\Users\Admin\AppData\Local\Programs\keyboard.wtf\keyboard.wtf.exe` |
| Installed/package EXE SHA-256 | **CDADCC46FB51939F74DF4FD503A2AFCF61B25FCBBB2A3C2BFBAE3C3A5BDCEB44** |
| EXE bytes | **314,822,094** |
| Installer | repair checkout `dist/installer/keyboard-wtf-setup.exe` |
| Installer SHA-256 | **3050590E42B079505E1265ED92FD323601459FD61D1B4039AFEE60D46ED93706** |
| Installer bytes | **84,286,392** |
| Package EXE | `dist/release/keyboard-wtf-win-x64/keyboard.wtf.exe`, byte-identical installed |
| Correct provenance | `dist/verification/repair-5fddef4/provenance.json` |
| Private CI | **34979469331**, success, exact HEAD independently confirmed with gh |
| CI link | https://github.com/UnknownGod2011/keyboard.wtf-private-backup/actions/runs/34979469331 |
| Installed acceptance | Partial, not user accepted, not public |
| Dirty P15 fix | **NOT contained in this installed EXE** |

Provenance records 1,163 desktop / 262 backend / 12 real isolated-staging Firestore tests, clean-profile paid runtime, security/Helper/JavaScript/dependency gates, 89 SBOM components. These are historical gates for frozen HEAD, not all gates for dirty source.
The previously running dirty-source desktop suite completed: `dist/verification/repair-5fddef4/p15-full-gates/p15-full.trx` has **1,173 executed/passed, 0 failed** (NOW XML read). This is desktop evidence, not 1,173 production integrations. Focused Browser tests: 547 passed. Do not rerun solely because the previous tool output was truncated.

**Stale records to avoid:** root `dist/release-provenance.json` previously described 3636587; PROBLEMS P28 and COMPARISON header still name 8492daa. Use actual hash/provenance above, not these stale headers. Historical profile hashes also change legitimately with user settings; do not call an old hash current.

### Runtime snapshot (NOW, read-only; do not freeze PIDs as identities)

Installed main PID20232 and second PID15304 both use the exact production EXE. The second command line is the allowed Chrome extension origin plus --parent-window=0, which Program.Main recognizes as native host. It is **not evidence of a duplicate logical Jarvis**. The first naïve check for a literal “native” flag would have misclassified it; source accepts the extension-origin invocation.
- Local Settings API port8090 responds.
- reservedCreditMicros = **0**.
- Store Helper connected = **true**, reported service worker protocol **2.1.21**.
- Task ID empty, canCancel=false; serialized state reads Queued as an empty/default task. **Do not infer an active BA task from that enum alone.**
- Log shows normal paid auth refresh HTTP200, including proactive refresh; no test Live session was started in the handoff.
- Main process restarted at22:41 on September16; revalidate before next acceptance.
- User settings file last modified22:41; older 8506... profile hash is not a current invariant.
- Production profile directory: `%LOCALAPPDATA%\keyboard.wtf`.
- Files include settings.json, learned-mappings.json, intent-memory.json, jarvis-action-history.json, paid-production-session.dat and .device, logs/keyboard.wtf.log.
- Never print full settings/tokens/account JSON. Paid session/device files are DPAPI-protected; do not copy secrets into this packet.

### Backend / models / site (NOW where stated)

Read-only Cloud Run query:
- Project `keyboard-wtf-prod`, region `asia-south1`.
- Service `keyboard-wtf-paid-api`.
- Latest ready revision **keyboard-wtf-paid-api-00026-8kc**.
- Template request timeout **900s**.
- Production desktop compiled backend: https://keyboard-wtf-paid-api-mhrc6c7goa-el.a.run.app/
- Canonical web: https://keyboardwtf.com/
- Compatibility web: https://keyboard-wtf.vercel.app/
No config or billing mutation was made.

Actual Firestore `config/modelRouting` read NOW (route budget columns are configuration, NOT proof of provider request enforcement):
| Route / caller | Primary (location) | Fallback | Output cap / configured thinking |
|---|---|---|---|
| live_audio / Jarvis | gemini-live-2.5-flash-native-audio (us-central1) | none | 1 / 0; do not interpret non-Live output cap as Live speech limit |
| browser_planner / browser-plan, browser-vision-recovery | gemini-3.5-flash-lite (global) | gemini-3.5-flash | 2048 / 512 |
| selected_text / desktop-reformat, smart-writing-audio | gemini-3.5-flash-lite (global) | gemini-3.1-flash-lite | 2048 / 0 |
| screen_reasoning / screen-guidance | gemini-3.5-flash (global) | gemini-3.5-flash-lite | 2048 / 1024 |
| agent_planner / desktop-structured | gemini-3.5-flash (global) | gemini-3.5-flash-lite | 4096 / 1024 |
| fast_text / desktop-health and default | gemini-3.5-flash-lite (global) | gemini-3.1-flash-lite | 2048 / 0 |

Important mismatch: compiled desktop Live constant is **gemini-3.1-flash-live-preview**, but production VertexLiveProxyService.RewriteSetup substitutes the server route and prepends server constraints. Existing developer-key 3.1 probes are **not same-model production evidence**.
VertexAiClient generationConfig currently sends maxOutputTokens, temperature, and optional JSON schema; it does **not send the configured thinkingBudget**. This is a proven config/request mismatch, not proof that turning thinking down improves the product. Secondary P41 must benchmark before any production model change.
Local/nonpaid screen fallback candidates are2.5flash,2.5flash-lite,2.0flash; they are not the production screen route. Raw local recognition uses Vosk/Whisper, not a paid Live socket. README lists generic provider capabilities, not the authoritative paid shipping route.

Canonical and fallback `/releases/stable.json` fetched NOW both advertise1.4.17 and the correct original hash. Public GitHub latest is NOW `v1.4.17`, not prerelease. No new installer downloaded in this handoff; original local baseline bytes rehashed and earlier actual-CTA download proof retained.

## 2. What keyboard.wtf is and where to read the implementation

A Windows .NET8/WinForms tray assistant with global hotkeys, an always-topmost/no-activate voice overlay, local Settings served in a browser, voice writing/dictation, natural Live conversation and allowlisted desktop/browser tools. Production customers sign in using Firebase-backed desktop login and consume server-authoritative paid credits. Real Chrome interaction uses a Store extension plus a native messaging host, not unrestricted model-generated keyboard scripts.

| Layer | Main source / ownership |
|---|---|
| Entry/lifetime | src/Program.cs; Services/KeyboardWtfApp.cs; single app-data lock; native-host invocation branches before tray lock |
| Settings/UI | SettingsService, HotkeyService, WebSettingsService; Models/KeyboardWtfState; VoiceOverlayForm; Resources/web/settings.html; SettingsSectionRegistry |
| Live transport/audio | GeminiLiveConversationService; native provider speech, NAudio input/output; logical session and sequential physical sockets |
| Intent/tools | CommandRegistry owns tool routing/operation decisions; JarvisAutomationService owns allowlisted effects, confirmations and recent action receipts; LiveToolCallDispatcher owns cancellable serialized function results |
| Native safety | NativeWindowCloser, BrowserTrustedInput, BrowserLauncherService, TextInsertionTarget; target identity/cancellation before mutation |
| Files | SmartFileSearchService, AppResolverService, FuzzyMatcher; safe files/folders, no arbitrary executable opening through generic paths |
| Capture/writing | AudioRecorderService, VoiceCaptureService, SpeechRecognitionService, VoskRecognitionService, WhisperRecognitionService; CommandRegistry writing/delivery completion |
| AI facades | Ai/PaidCloudAiProvider and PaidStaging/PaidAccountService in shipping; other provider classes are not evidence of production BYOK |
| Screen | GeminiScreenGuidanceService; one approved in-memory JPEG capture, no continuous watching |
| BA | Services/BrowserAutomationV2: Coordinator, Planner, Verifier, SafetyPolicy, Models, TaskClassifier, SemanticControlRanker, HelperBridge |
| Chrome | extensions/keyboard-wtf-browser-helper-v2/{service-worker.js,content-script.js,popup.*}; Store version2.1.27, protocol2.1.21 |
| Native host | BrowserNativeMessagingHost / BrowserNativeHostInstaller; bounded length-prefixed stdin/stdout bridge to main-app named pipe, extension-origin trust |
| Persistence | LearnedMappingService (Link aliases), workflows in settings/automation, IntentMemoryService (bounded explicit facts), action history; no second memory system |
| Read-back | TtsService, TtsPlaybackLease, Piper/Windows SAPI; distinct from native Live speech |
| Backend | staging/backend/KeyboardWtf.Staging.Api — path name says staging but same code serves production; NEVER assume directory name means disposable infrastructure |
| Backend AI/accounting | ModelRoutingService, AiUsageService, VertexAiClient, VertexLiveProxyService, FirestoreAccountStore, LedgerInvariantService, reconciliation/maintenance services |
| Backend identity/payments | FirebaseIdentityService, DesktopLoginService, SubscriptionManagementService, RazorpayPaymentProvider, PaymentReconciliationService |
| Web | production/site is current canonical source; site and staging/site are separate trees. app.js/account.js/pricing.js/desktop-login.js/config.js |
| Release | scripts/build-windows-release.ps1, installer/keyboard-wtf.iss, scripts/release, Updates/SecureUpdateService and UpdateManifestVerifier; private CI .github/workflows/paid-staging-ci.yml |

README/production README contain old future-tense/precanary statements. Prefer actual code/runtime. The extension already exists; do not implement an imaginary “future browser integration.” Model branding is internal; customer progress should say “Analyzing your screen…” or “Polishing your text…”. Technical diagnostics may retain provider names.

Default documented shortcuts: Ctrl+Alt+Q Jarvis, K Smart Writing, D Raw, X cancel, comma Settings (all Ctrl+Alt). Actual saved bindings and fallback registration are authoritative. Ctrl+Alt+Space belongs elsewhere; do not assign it. Historical TEST Ctrl+Alt+T must never take production Settings/writing/Jarvis bindings.

## 3. End-to-end flows and single-owner invariants

### Startup and logical Live

Hotkey → CommandRegistry → paid-account readiness/token (proactive refresh plus authenticated on-demand fallback) → GeminiLiveConversationService logical session → backend WebSocket/reservation → server-rewritten provider setup → microphone/playback ready → Listening. Trace localPreflightMs, socketConnectedMs/setup and auth timestamps separately; provider/network latency cannot honestly be guaranteed under3seconds.

Input is16kHz microphone PCM; native Live output24kHz. Live VAD/barge-in and playback ordering must remain responsive. Tool work was moved off the socket reader so a long action cannot prevent receiving cancellation, transcripts, audio/control. Immediate farewell and explicit BA cancel stay on the existing immediate control path; ordinary transcript observation should not secretly execute effects before the model/tool owner.

Session scope has cancellation, logical ID, socket sequence, resumption handle, bounded rollover context, UI revision. Paid physical soft target remains9.5min; provider GoAway can arrive earlier. Old socket closes/settles before one replacement starts. Quiet periods do not end the logical conversation. Stopped/dismissed Jarvis must release microphone/socket/work and usage while keeping tray/hotkeys/Helper readiness available. A quiet active conversation and a dismissed assistant are not the same idle state.

### Tool/action/result flow

Authoritative transcript/context → model-selected allowlisted tool → CommandRegistry resolution → JarvisAutomationService or BA owner → verified receipt → LiveToolCallDispatcher functionResponses → native model speech.

The dispatcher serializes effects off the reader, combines calls in one provider batch, retains cancellation until the socket send lock, and materializes only still-live results there. Ordinary results use outer function-response scheduling WHEN_IDLE; duplicate acknowledgements SILENT. These are protocol metadata, not strings nested in tool result data. Per-message batching is not automatically one logical user request: separate model messages/retries still require existing turn/receipt ownership. Do not add an arbitrary “wait until turnComplete” collector; one experimental collector risks deadlock on models awaiting tool results before completing.

Recent action receipts must carry actual object identity and cardinality, not only spoken labels. Folder referents keep canonical full paths. Browser open/workflow receipts keep exact successfully opened tab IDs. Explicit “close File Explorer” may mean a broader safe set; referential “close those” must not broaden beyond the referenced set. Missing/ambiguous identity asks/refuses rather than targeting the foreground by guess.

Native Explorer close enumerates owned Shell windows/paths, snapshots target HWNDs, validates identity/cancel immediately before posting native close, then verifies disappearance. Never kill explorer.exe or use Chrome Search Tabs/blind Alt+F4/Ctrl+W. Keyboard injection balances only its owned keys, refuses user-held modifiers, revalidates foreground and cancellation. A new “last3tabs” implementation must first identify an intended stable tab set; repeated blind keys are not a safe substitute.

### Browser Automation

Explicit BA or genuine webpage interaction → existing coordinator Runtime (task ID, CTS, generation, page/tab identity, constraints/context, pause gate/intervention, receipts/history) → Helper snapshot → bounded planner request → safety and target resolution → renderer/native mutation under current command lease → fresh visible verification → repeat only if genuinely incomplete → terminal state or NeedsUserAction.

Planner uses server browser_planner route and structured JSON, not the Live model. It receives task context/page controls/history and may use bounded vision recovery. Current history budget eight entries/4000characters includes recent verified checkpoints and latest accepted guidance; previously concatenation/truncation lost useful progress.

Service worker/native bridge must remain live while page work waits. Command waiter/lease is authoritative through queueing, dispatch, renderer pre-mutation check, result acceptance. Worker cancellation alone was insufficient when a renderer paused just before writing; content script now checks the existing owner at mutation, with sender/tab/frame/command identity. Capability readiness, queued sends and results must enforce the same supported contract.

Completion requires visible-state proof, not planner Done alone. Forms require question/group identity, exact expected value, explicit replace vs append, required/optional metadata, and no accidental submit/reset. Nonempty defaults are not evidence the requested answer is already supplied. Authorized optional skip means leave it unchanged and continue. Useful pages and explicit destination outrank stale navigation prerequisites.

Only relevant BA start/control tools enter active-task guidance; a semantically selected direct tool must not be intercepted because its URL contains ':' or shares page words. Explicit cancellation preempts queued work. Confirmed page handoff updates the SAME task generation, invalidates old actions/results, never automatically adopts arbitrary foreground tabs. Terminal state removes routing/publication authority immediately; retain only bounded read-only diagnostic outcome for “what happened?” questions. A delayed task announcement must not speak through separate fallback TTS after Live/session/task ownership is gone.

### Selected context, Link aliases, workflows, memory

Capture selected text through existing safe boundary (preserve/restore clipboard), record source identity and operation/turn ownership. Explicit selected text cannot be silently replaced by fresh clipboard content. “Keep this for later” retains bounded deferred context with BA OFF; later real action consumes it. Explicit fresh selection replaces stale selected context.

Learning a valid selected hostname normalizes generically to an openable URL and stores one durable Link; custom trigger resolves ahead of generic app/search and survives restart. Workflows remain separate durable multi-action routines/tombstones. Existing bounded Link alias capability data exposes aliases, not all target/private data, so the model knows a tool can resolve them; actual resolution returns the canonical destination receipt. Do not secretly execute a mapping before model reasoning then allow speech claiming ignorance.

Personal memory is an existing explicit-useful-fact owner, not a transcript archive:20entries/280characters each, prompt subset10entries/900characters; view/delete/clear in Settings. No new memory feature needed late in recovery. Do not automatically persist raw selections/secrets. Stored mapping success does not prove correct conversational confirmation.

### Smart Writing / Raw / clipboard

Capture start publishes operation generation before potentially blocking microphone initialization. NAudio callbacks meter safely (widen PCM16 before abs), preserve audio, report failure to owner; stop awaits final callback/drain before constructing WAV. Cancelled drain/recovery cannot overwrite terminal Cancelled or paste later.

Smart Writing stops by user/mode policy, sends ordered bounded audio segments through production selected_text route under one logical writing operation, preserves usable captured output on failure, and delivers once at the captured cursor/target. Do not oversummarize long speech. Raw uses local recognition; recognizers can return empty on failure, so current completion recovers known cumulative preview as partial text rather than relying only on exceptions. Recovered partial command-mode words must NEVER execute commands.

TextInsertionTarget/DestinationRouter own intended cursor, clipboard/paste semantics and single delivery. Raw restoration and Smart final-output clipboard behavior differ; preserve intended mode contract, not a blanket “restore everything.” An823second prerecorded facade replay with a real paste is not823seconds of real microphone callbacks. Production profile reportedly retains a user-saved30s Raw limit: distinguish configurable existing policy from an arbitrary new hard cap, do not silently alter user settings.

### Screen Analysis

Jarvis inspect_screen → permission/approved capture → virtual desktop JPEG resized max1600width, quality82 → screen-guidance paid route → result receipt → normal Live speech. No persistent screenshot in shipping path. Current installed client allows2048outputtokens and20s waiter deadline, with existing UI revision checks. Underlying backend may still finish/settle after client cancellation; do not promise cancellation means zero provider cost.
Old900shared thinking/output allowance plus70.75s provider wait explains the measured failure better than speculative microphone-volume damage. Timeout/recovery fix does not solve10second success latency.

### Account/billing/web

Browser Firebase auth → desktop login challenge with PKCE/state/nonce → backend desktop session → DPAPI local token store. Backend verified Firebase UID is authoritative; never client-supplied/stale email. Production compiled config locks paid route and disables emergency BYOK.

Backend validates feature/route, reserves credits transactionally, dispatches model, accounts actual usage, settles/releases idempotently. Live leases720s with120s cleanup grace; Cloud Run900s. Historical shutdown cancelled Firestore transactions were fixed separately; do not erase evidence/quarantines or manually credit balances to make tests pass.

Pricing concurrently awaits catalog and existing auth initialization, then loads account/subscription and renders Sign in / Subscribe from Account / Manage current plan. Account creates subscription under server identity; Razorpay webhook/reconciliation—not browser checkout success—activates entitlement/credits. ₹999monthly/500provider-backedcredits currently public. Customer email/domain audit P42 is still open; source ownership checks alone cannot prove the user's specific payment chain.

### Updater/release

Original signed stable manifest + trusted key/hash/size + monotonic version protection → downloaded installer. **Signed release metadata is not Authenticode signing**: original release notes explicitly say installer was not Authenticode-signed. Do not falsely call the EXE Windows-signed.
Self-contained net8Windows package → Inno installer → runtime/provenance/SBOM gates → private CI → exact install/hash → affected physical acceptance → user manual approval → only then public metadata/site/judge synchronization. Do not downgrade the user's installed private1.4.19 just because publicstable is1.4.17.

## 4. Chronology and 1.4.17 behavioral baseline

Exact stable reference: **6a2c0fcbc5ade96bc6feb4717ab284e83b874d04**, source-equivalent parent recorded as63beae98...; use full Git-resolved reference rather than inventing remaining characters.
Original artifact `dist/verification/baseline-1.4.17/keyboard-wtf-setup.exe`:
**84,251,836bytes**, SHA256 **812aaa434a4d6b618764b044a9a5f96b5dc9b85830ce1f205c5b6ad582ded6cd** (NOW rehashed).
Earlier actual public CTA copy `live-site-download.exe` matched. Stable signed manifest keyIDkwt-release-2026-01. GitHub artifact URL:
https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v1.4.17/keyboard-wtf-setup.exe

Why baseline: HUMAN said conversation was natural/smooth, first farewell reliable, startup~3–4sec, ordinary writing and screen reasoning felt fast. These are experiential reports, not complete controlled performance benchmarks.1.4.17 already had useful-page protection, stale-plan rejection, sequential Live resumption/context compression/safe rollover, one bounded malformed planner retry. It was not a tiny command-only assistant. It also retained latent PCM arithmetic and Helper page-blocking defects and a server lease gap: blanket restore is unsafe.

Important chronological commits (NOW Git log; messages describe intent, not proof):
| Date | Commit/group | Purpose and later evidence |
|---|---|---|
| Stable Aug29 metadata | 6a2c0fc | Original1.4.17 behavioral/artifact reference |
| Sep2 | 2532b67 | Fix Live reservation lease ownership; preserve |
| Sep2–3 | 64fe5bd, d42ee4e,7a2a21e,7ba8f23 | Paused BA conversational recovery, context and unrelated-turn isolation; later overbroad guidance still reproduced |
| Sep3 | 2342799,4b28c59,14f0dbb,7af2f56 | Pricing hero/FAQ,1.4.18 metadata, notice alignment; deployed source lagged local |
| Sep3–4 | c894930,aa634f5,ce8d7a2,1a31301,38d9a14,ed9edd4,ca9b5f7 | Reset protection, atomic writes, opt-out/farewell/selection/handoff/group identity |
| Sep4 | 655107f | Move tool execution off socket reader; necessary responsiveness, downstream publication cancellation needed |
| Sep5 | 456b4b6,87f0db4,973ef70,0b2b382 | Task ownership/answers/form-state suppression/mapping parser;973ef70 later proven to suppress legitimate missing-value questions from defaults |
| Sep6 | ea01ff0,aad47ad | Settings constraint precedence, explicit write mode, available/reserved wording |
| Sep6 |123059c,c8d9e55,7824b19 | Native Explorer ownership, Live loud PCM/batched results, compiler-safe input ordering, cancellation propagation; shared recorder remained faulty |
| Sep7–8 |171662e,82592b1,933c194,2b4eba3,d278766 | Mapping/selected Link contract and turn ownership; persistence worked but later eager execution/speech contradicted |
| Sep9 |39880ac,442b3b6,b1e3a5a,db8cabf,c3b3cbd,821a12f | Long-session/task guidance, cancel/skip, diagnostics, microphone-start cancellation |
| Sep10 |e01f33a,785a48a | Routine notification boundary, frozen acceptance; human rejection reopened many items |
| Sep12 |b82acc7 | Coherent action/Helper/cancel/audio recovery repair; still needed production-model/physical validation |
| Sep13 |3636587 | Helper capability authority/public support address; installed multi-folder/speech still failed |
| Sep13 |5d6d101 | Function-response scheduling metadata; installed speech improved; referential close still separate |
| Sep14 |8492daa | Canonical folder/path close identity; actual A/B close and one completion proved |
| Sep15 |5fddef4 | P40 bounded screen recovery, P14 final-sentence cancel, P16 Settings ownership, P15 bounded checkpoint context |
| Sep16 dirty | P15 verifier/tests | Exact NASA replay invented publisher from later sentences; source/test repair passes, not installed |

Diff baseline→HEAD: **99files,12030insertions/1331deletions**, including many tests/docs; coordinator +1844/-approximatelyoldcontent, CommandRegistry +1070 changes, Live531changedlines, JarvisAutomation742changedlines. Large growth is reason to inspect shared ownership, **not proof that line count causes intelligence regression**. Do not infer a fix from complexity alone.

## 5. Latest human evidence and current root-cause map

Latest user says product **improved**, startup under3s, hears/speaks clearly, Explorer closes correctly, workflow tabs closed, NASA oldest task succeeded with one redundant navigation. Preserve that; do not describe all audio as currently broken.
- Highest priority screen: asked detailed description,~14s no normal response, then silent/glitchy. Actual preserved backend trace later establishes70.75s wait and tiny visible answer. It does NOT prove volume was set0 or mic died.
- BA Devpost highest-prize: generic search first, brief reconnect near destinationword, then proper site after correction; sorting guidance not understood, repeated loop, cancel failed. Interruption of destination ASR is plausible, not proven.
- NASA completion loop reproduced installed5fddef4 even after earlierhistoryfix; new extractionfailure proven.
- “last3Chrome tabs” only1 closes: still unresolved, not safe to use3blindCtrlW.
- HowtoUseJarvis local Settings opened and BA also started: sourceownerfix+installedBAOFFproved, actualsectionvisibleproofmissing.
- Folder ambiguity openboth sometimes1 with explanation “samefolder”; could be duplicate canonical result or nesting. No proof yet, user explicitlylowpriority; safe close/provenA/B flowpreserved.
- Workflow learning “initializeJarvis”→searchgetkeyboard.wtf acknowledged, next bare domain blank, later correction worked. Exact causeunproven; barevalueisnotnecessarilytrigger. Checktranscripts/toolresult/persistence beforechangingstore.
- Older repeated/contradictoryspeech, mapping refusal despiteexecuting, wrong-pageBA, stalefailureaftercancel, farewell intermittence, full-scalerecorderfreeze remain in acceptance ledger.
- User confirmed accidental gibberish during earlier9:25screencheck. Do not count that eventas unsolicitedaction from scriptedconversation. Micinputhasbeenmuted/restoredatvarioustimes; measureactualstate at testtime.
- User requires realweb variation (publicdemo/controlledforms), never arbitrarythirdpartysubmission; authorizedreset/repeat isallowedtest, not blanketbanonreset.
- User wants no public providerbranding, support@keyboardwtf.com onlycustomeraddress, realdeployedPricing/auth, boundedsecondarymodel/costaudit andspecificRazorpayUID/domainaudit aftercore.
- Human approval remainsmandatory.

### Proven shared causes vs hypotheses

1. **Async work outliving authority** (proven): nonblocking tools improved reader responsiveness but cancellation had to cross queued tool execution, socket send materialization, Helper waiter, stalled renderer pre-mutation, terminal callbacks and delayed speech. Guarding only UI/coordinator state missed later effects. Correct existing owners at each handoff; do not restore blocking reader.
2. **Model/action knowledge diverged** (proven source+protocol and human contradiction): eager pre-routing could execute Link before a model refusal; receipt cache expired12s while same turn remainedauthoritative; scalarresults bypassedack; schema hidaliascapability. Moveeffectsintotoolowner andreturncanonicalreceipt; notanothernarrator/store.
3. **Object identity collapsed to a label** (proven): multiplecanonicalfolderpaths became identicalbasename, optionaltargetallowedclosewithoutreferent. Keepstablepath/tabsetreceipts. Last3tabsselectionisaseparategap.
4. **False verification evidence** (proven): nonemptydefault treatedasrequestedanswer; actionwordsinfieldlabels inflatedgoalcount; nativeoptionlabel/value mismatch; sourceextractioncrossessentences. Badverifier rejectioncanlooklikeplanner stupidity/looping. Fixmeasurementbeforeenlargingprompt/changingmodel.
5. **Provider budget/latency, not proved audio damage** (P40provenwait/tokenexhaustion):20sboundedwait/UIrevisionprotectsfollow-up,2048headroomhelpsanswer; speednotclosed. Thinkingconfignotwired; isolatedLOWnotconsistentlyfaster.
6. **Latent baselinebugs**: short.MinValue arithmetic existedinthe1.4.17sharedrecorder; workerblockingpagecalls alsoexisted. Newusageexposedthem; notallregressionswereintroducedafterbaseline.
7. **Source≠shipping≠production**: previewCSSneverprovedlivewebsite; desktopdefaultmodelneverprovedproductionVertexmodel; newerextensionfileson disknotequalactiveStoreworker; greenunitcontractsnotASR/speaker/windowproof.
8. **Unproven hypotheses**: promptgrowth, provider variance, backgroundnoise/VAD, reconnect clippingthewordDevpost, multiwordfolderindexlatency. Do notchangeaudio thresholds,models or addintentgrammar withoutmatchinglogs/actualpayload/effects.

### Current P15 dirty correction — exact continuation point

Installed task `f4e54d3b51904eae904fd68e347d76a4`, Live `234628c4c8cd4a34a745521d8170934c`, Sep16 17:54:23–17:55:08, under `repair-5fddef4/nasa-oldest-20260916`.
Request: “Use Browser Automation to open https://www.youtube.com/@NASA/videos and open the oldest video published by NASA. Verify the publisher and oldest ordering. Stop once the video is open; do not repeat navigation.”
Observed realStoreChrome: NASAchannel→Oldest→Mercury7MusicVideo→Done rejected→channel/sort/video repeated→tenplans; agentcancelled, no postcancelmutation, laterreserved0.

ExtractOrderedSourceIdentity prefixregex consumed later sentences as publisher (“...oldest ordering. Stop once the...”). Verifiedcollectioncouldnotmatchinventedidentity, so terminalverifierkeptrejecting. This **proves this replay's bug**, not that every older humanloop hasidenticalcause.

Dirty implementation only: existing by/from extraction accepts optional publication attribution words; reject a sourcecapture crossing sentence punctuation plus whitespace. Existingpublisher/sourcecollectionverification remains required. No site-specificproductioncode/newowner/promptgrowth. ExactNASAstring occurs onlyas regressioninput.
Tests before: p15-exact-source-before.trx fails; p15-attribution-before.trx5fail5pass. After: p15-source-focused.trx547pass; full1173pass. Tests require correctsource, rejectwrongpublisher andmissingcollection.
Reviewrisk: abbreviationpunctuation (“Inc. Labs”) andnameboundaries; don'tturnextractorintoeverlargersemanticgrammar. Installedreplaynotyetperformed. User switchedthisruntohandoffonly—**do not package now**.
PROBLEMS P15 saying “test currentlyFAILSintentionally” isstale. Nextthreadupdateafterreview, notblindlytrustit.

### P40 exact evidence / limits

Human rejected8492 request `desktop_66438bf4a685405489a5b54a23245010`: screenrequest21:32:24, provider70,749.9ms/total71,153.6ms,35visible+861thinkingtokenswithin900; finalfarewell21:33:29arrivedbeforebackendreturn21:33:35. No demonstratedSendInputvolumemutation/PCMexceptioninthisevent.

Installed5fddef4 `desktop_d1fad6654d3e49c499d864f751c2142d`, Sep16 10:02:38.480–48.601:10.12s, oneinspectsuccess, actualspeakerdescribedpublicSeleniumform andanswerednextfile/folderquestion;goodbyeended10:03:37,reserved0,inputrestored. **Recoverypasssample, speedfailurestillopen; finaltextAPIisnotmicrophoneASR.**

Nonshippingdeveloperprobe samepublicimage:2048default8–10sSTOPvs900truncation;LOW/MINIMALinitial5–6ssamples. Secondimage:default17.14s,LOW23.87s,MINIMAL503. Retainall503s; donotretryuntilgreenandclaimupgrade. No reliableevidence toalterproductionthinking/model.20sforcedtimeout/source6tests pass, exactinstalledforcedrecovery/humanmicstillmissing.

## 6. Material differential and preservation decisions

| Area | Baseline→current actual difference | Assessment / next evidence |
|---|---|---|
| Prompt/tools | Compiled comparison13,133→15,166instructionchars;59toolsboth;23,867→25,147JSONbytes. Productionserverrewriteadditionallyapplies. Measurementsfrompreserved differential, notnew fullpayloadmeasurementthisrun | Modestgrowth, notprovedcause. Inspectrelevantpayloads/contradictionsbeforecuttingcapabilities |
| Audio/VAD/nativevoice | Preserve core16/24k/nativeplayback; changedmetering/reconnect/dispatchhandoffs | Arithmeticfixbeneficial; humanbarge-inlate-sessionrequired. Nothresholdguessing |
| Startup | Proactiveauthrefreshadded; localpreflight0–4ms inpreservedevidence | Beneficial, recenthumanunder3s; remotevariationnotappwait |
| Tool dispatch | Sequentialreader→nonblockingserializeddispatcherwithbatchresults/cancellation | Keepresponsiveness; sendboundarymetadata/cancellationnecessary |
| Result speech | SILENTformerlynestedintooldata; nowouterSILENT/WHEN_IDLE | Provenprotocolcorrection; installedA/Bonespeechpassed. Notalllogicalrequestsproved |
| Recentcontext | Canonicaltargets/cardinality, boundedaliascapability, read-onlyobservation | Beneficial, preservestore; last3tabsstillgap |
| BA guidance | Broadroutingheuristicscouldhijackdirecttools; nowonlyBAtoolboundaryacceptsguidance | Failing→passingisolatedrealmodel/Helper; installedmatrixpending |
| BA formstate | Additionalverifier/defaultsuppression; latercorrectedexplicitexpectedvalue |973ef70regressionproven; keepfix+realwebvariation |
| BA progress | Flattenedtruncatedcheckpointstring→boundedindividualrecentcheckpoints/guidance | NecessarybutdidnotalonefixNASArepetition |
| BA publisher | Addedconstraintsavoidoldthird-partyresults; dirtysourceextractionrepair | Keepconstraint; wrongextractioncausesloops; installedpending |
| Helper | Pageblockingpoll→independentheartbeatwithinowner, commandleasechecksrenderer | Safetybeneficial; activeStore2.1.27/protocol2.1.21, don'treopenresolvedupdateblocker |
| Cancellation | Singleearlierstop→existingauthoritypropagatedthroughasyncboundaries | Keep; fullstatecancelandnoresurrectionproofstillrequired |
| Smart/Raw | SafePCM/drain/recovery, segmentedlongSmart, emptyRawpreviewrecovery | Provenlatentbugs; longrealmicstillnotclosed |
| Screen | Sameimagecapture;900→2048headroom,20sdeadline/UIrevision | Recoveryimproved; speednotbaseline, noprovenmodelreplacement |
| UI | NativeTOPMOST|NOACTIVATE, revisionownedterminalstate; routineOSballoonssuppressed | Preserve, don'tforcefocusoverlay |
| Memory | Sameexplicitboundedfactowner; Links/workflowsseparate | Don'tcreateanothermemorysystem |
| Livebilling | Shortlease→720s/120sgrace; sequentialresumptionretained | Mustpreserve; don'trestoreold110s/~230sreclaim |
| Auth/payments | CoreUID/PKCE/webhookauthoritypreserved | SpecificrealpaymenttraceP42unclosed; don'tequatesourcetestswithaccountproof |
| Website | Card/noticecontainerandauthinitializationcorrected/deployed | Realresponsiveandactiveplanpass; realno-planstillpending |
| Updater | Sameverification; publicmetadatarestoredtooriginal1.4.17 | Preserveoriginalartifact; notdeveloperbranchrestore |

## 7. Complete numbered problem ledger, with current interpretation

The following sections reproduce all P01–P44 requirements and preserved evidence from the actual dirty PROBLEMS.md, enriched with user evidence, certainty and remaining checks. Historical Fix lines are descriptions, not new implementation in this run. For each unproven causal claim, the assessment below narrows it; no speculative root cause is asserted merely because the old field says “Root cause.”

### P15 — Browser correctness constraints and visible completion

**User-observed evidence:** Latest human NASA succeeded then repeated; Devpost sorting loop; installed NASA replay reproduced loops.

**Current handoff assessment:** DIRTY FIXED-SOURCE candidate, not installed:1173desktop and547Browser pass; snapshot failing-test wording stale.

**Causal certainty / suspected cause:** Cross-sentence invented publisher proven for exact replay; Devpost may have other causes.

**What still needs verification:** Review extraction boundaries, then exact installed real NASA and distinct-site sorting/guidance; stop as soon as verified.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: INVESTIGATING
Expected: Latest-by-publisher verifies publisher/freshness; useful page is preserved; Completed matches visible state.
Latest evidence: Exact installed `5fddef4` NASA replay again opened Mercury 7 then repeated channel/sort/video, reaching ten plans before agent cancellation. New failing test proves the verifier invents a publisher from the later instruction sentences; source-history fix alone was insufficient.
Recorded root cause (certainty qualified above): Checkpoint/guidance truncation corrected, but ordered-source extraction can consume text across sentences and demand an impossible publisher identity. Exact replay request reproduces this deterministically; older human task may have additional causes.
Fix: Share the existing eight-entry/4,000-character budget between recent events, recent verified checkpoints and the latest accepted task guidance, including vision recovery. No larger prompt, new memory owner or completion heuristic.
Evidence: `repair-5fddef4/nasa-oldest-20260916` real Store Chrome trace/screenshots; `tests/p15-exact-source-before.trx` currently FAILS intentionally. First local cancel stopped active planning; subsequent account refresh shows zero reservations/mute restored. Do not weaken publisher verification to hide the failure; generic extraction/ownership correction and replay remain OPEN.
Touches: Browser planner/coordinator/verifier.
Protect: Generic behavior; no site-specific rules.

### P40 — Screen Analysis and provider-neutral UI

**User-observed evidence:** Latest highestpriority detailed screen request slow then apparent loss ofvoice/listening.

**Current handoff assessment:** OPEN successfulresponse latency and final physicalrecovery; installed10.12s answered nextturn.

**Causal certainty / suspected cause:** 70.75sprovider+900sharedoutput starvation proven; volume/microphonefailure notproven.

**What still needs verification:** Representative fastresponse/fullcontent and forcedinstalledtimeout+laterconversation/humanASR, no sensitive screenshot upload.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: INVESTIGATING
Expected: Screen inspection responds promptly and never blocks subsequent listening/conversation; progress remains provider-neutral.
Latest evidence: Exact installed `5fddef4` real production screen call completed in 10.12s, spoke the public form description, answered the next conversational turn, and stopped on goodbye. Recovery passed this sample; successful latency remains too slow, not CLOSED.
Recorded root cause (certainty qualified above): Provider took 70.75s while the serial tool owner waited; 861 reasoning tokens left only 35 answer tokens within the 900-token allowance. Final farewell still reached the microphone/socket; no volume mutation is evidenced.
Fix: Source bounds screen analysis to 20s and permits the existing server limit of 2,048 output tokens; the existing UI revision protects newer conversation state and ends stale analysis feedback. Fast successful production response and post-screen voice recovery remain OPEN.
Evidence: `repair-5fddef4/screen-recovery-20260916`: actual speaker/next-turn response, Speaking above Chrome, one inspect receipt, no mutations, zero reservations/mute restored; startup 2.014s. Second-image isolated test: default 17.14s, LOW 23.87s, MINIMAL 503; earlier 5.32–5.79s samples do not justify production change. Human ASR, forced installed recovery and speed remain OPEN.
Touches: Screen guidance and existing tool/conversation handoff only where proven.
Protect: Internal logs, billing labels/prices and provider routing.

### P14 — Browser cancellation in every state

**User-observed evidence:** Latest human complete final cancel sentence rejected, actions continued.

**Current handoff assessment:** FIXED-SOURCE and installed queued/executing/Planning samples; full matrix OPEN.

**Causal certainty / suspected cause:** Final unquoted sentence parser cause established for recent failure; renderer lease fix already protected.

**What still needs verification:** Cancel Planning/Reading/Executing/Waiting/NeedsUserAction/Partial; no late effect/UI/speech; Jarvis usable.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Planning/Running/Waiting/NeedsUserAction/Partial cancel once, clear UI/task/intervention, and execute nothing stale.
Latest evidence: September 15 human reopens installed `8492daa`: “Never mind. Cancel browser automation.” was rejected at 21:31:57; actions continued until local cancel at 21:32:10.
Recorded root cause (certainty qualified above): The control parser required the entire utterance to begin with a control; ordinary preceding speech hid a complete final cancellation sentence. Earlier renderer mutation-boundary fix remains protected.
Fix: Reuse the existing control grammar on the final unquoted sentence; no acknowledgement vocabulary, new cancellation owner or renderer change.
Evidence: Exact installed `5fddef4` queued/executing final-text cancellation reached Cancelled in 23/11ms observation; stale ReadingPage rejected, intervention cleared, no resurrection, speaker confirmed cancellation then answered conversation. `repair-5fddef4/*final-sentence`; reserved zero/mute restored. Full state matrix and human ASR remain OPEN.
Touches: Browser coordinator/executor; Live/input boundary.
Protect: Jarvis remains usable after cancel.

### P34 — Active Browser task guidance and failure context

**User-observed evidence:** Human correction/skip not passed, unrelated actions intercepted, dead tasks spoke again.

**Current handoff assessment:** FIXED-SOURCE/isolated realmodel+Helper; final installed relevant guidance/correction stillopen.

**Causal certainty / suspected cause:** URL colon/page overlap hijacked correct direct tool, proven.

**What still needs verification:** Unasked relevant correction while running, pause/skip, unrelated app direct, terminal diagnostic-only, no resurrection.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Only relevant answers/corrections/guidance reach the same live task; terminal tasks lose routing authority, while concise diagnostic context still explains the latest failure.
Latest evidence: Real isolated Live/registry/Helper task `ab1444a7...` remained paused on its form during one direct URL open, accepted skip on the same task, completed without writes/submission, and accurately explained the outcome. One result batch per turn.
Recorded root cause (certainty qualified above): Tool execution still let active-task heuristics override a semantically selected direct tool; URL punctuation plus broad page-word overlap could qualify as field guidance.
Fix: Let only Browser start/control tools enter active-task guidance; keep immediate local cancellation and read-only recent outcome context. No additional phrase grammar.
Evidence: Failure `27c075eb...` preserved in `dist/verification/p34-*`; corrected model trace in `output/playwright/browser-model-guidance`; 355 focused cases pass. Muted audio/text input is not installed microphone proof; final installed replay pending.
Touches: Existing CommandRegistry tool-routing boundary; Browser coordinator.
Protect: Unrelated Jarvis turns, direct browser actions, cancellation and dead-task cleanup.

### P10 — Browser context and intent ownership

**User-observed evidence:** Human selected context sometimes stale or BA started while told to retain/analyze only.

**Current handoff assessment:** FIXED-SOURCE and isolated real Helper evidence, latest installed matrix pending.

**Causal certainty / suspected cause:** Selection vs clipboard source-identity defect proven.

**What still needs verification:** Deferred selection→manual page change→fill; fresh selection wins; explain-only BA OFF.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Context-now/action-later waits; fresh selection replaces stale; selected-text reasoning stays conversational.
Latest evidence: A later TEST turn supplied fresh clipboard text before an explicit selected-text task; the start owner initially accepted that wrong source. Task `d14cb8...` now attached the actual selected text and visibly completed the form without submission/reset.
Recorded root cause (certainty qualified above): Browser start first depended on an optional model capture, then treated any fresh captured source as satisfying an explicitly named source.
Fix: The existing Browser start owner captures the explicitly named source and retained context is accepted only when its source identity matches the request.
Evidence: Failure task `5d631d...`; corrected real-Helper task `d14cb8...` carried `AUTHORITATIVE ... SELECTED TEXT`, filled all known fields with correct group identity, skipped the optional field, and matched the visible page; 466 focused tests pass. Final production-installed verification pending.
Touches: Command routing; selected context; Browser coordinator.
Protect: Direct actions and selected-text reasoning.

### P11 — Browser page/task ownership

**User-observed evidence:** Human 'continue here' remained on old page.

**Current handoff assessment:** OPEN exact installed final handoff despite earlier isolated/human evidence.

**Causal certainty / suspected cause:** Pinned generation/root owner known; no automatic unrelated-tab adoption.

**What still needs verification:** Same task ID, changed page generation, old page untouched, terminal guidance refused.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Explicit current-page handoff resumes the same task and invalidates stale page actions/results.
Latest evidence: Real isolated Selenium→TestPages task `81b15ff4...` adopted the explicitly selected new page on the same task, wrote only there, completed, and rejected terminal guidance; old page remained untouched.
Recorded root cause (certainty qualified above): Task remained pinned to an obsolete tab generation.
Fix: Explicit re-grounding advances page generation and rejects older work.
Evidence: Human acceptance plus regression tests.
Touches: Browser coordinator/runtime generation.
Protect: Never adopt unrelated foreground tabs automatically.

### P12 — Form write correctness

**User-observed evidence:** Human wrong groups, duplicate/appended values, context ignored and reset concerns.

**Current handoff assessment:** FIXED-SOURCE/isolated multi-site evidence; not final Store-installed closure.

**Causal certainty / suspected cause:** Exact target score/metadata and label/value defects proven; don't add site exceptions.

**What still needs verification:** Controlled Google plus different real demo DOMs, explicit replace/append, no submit; authorized reset/repeat.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Context values target the correct group once; replace/append explicit; no duplicate writes or unrequested Clear/Reset. Release proof spans different real-site DOMs, not fixtures alone.
Latest evidence: Current source/current isolated Helper completed real Selenium and TestPages forms; native Reset→repeat passed. Exactly 4/3/3 input events, no duplicate writes, untouched unrelated fields, zero submits, terminal Helper/UI cleanup verified.
Recorded root cause (certainty qualified above): Exact unnamed controls scored 42 against a 45-point fuzzy-name threshold; adjacent visible labels were absent from the graph. Earlier write/group fixes remain protected.
Fix: Keep exact field/role ownership independent of fuzzy-name score; capture bounded adjacent legacy labels without borrowing other controls' text. Field/generation/cancellation checks stay enforced.
Evidence: Earlier human Google Form proof retained. Final installed/Store matrix must cover multiple public/demo/user-controlled forms: navigation/destination, handoff, corrections, required/optional/groups, pause/resume/cancel, continuity and visible state. Authorized Clear/Reset→repeat only where safe; no submissions.
Touches: Helper metadata; planner/executor/verification.
Protect: Do not submit test forms.

### P13 — NeedsUserAction bridge

**User-observed evidence:** Human answers/skip didn't resume or already-known values asked again.

**Current handoff assessment:** FIXED-SOURCE with actual isolated TestPages proof.

**Causal certainty / suspected cause:** 973ef70 default-value suppression and label punctuation proven.

**What still needs verification:** Real Jarvis same-task required answer/optional skip, unrelated conversation isolation.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Ask only for genuinely missing required data; relevant natural answer resumes the same task; optional skip works; unrelated talk is isolated.
Latest evidence: Real TestPages task `07f29f79...` asked once, accepted optional skip on the same task, completed with default text unchanged and zero writes/submits. Cross-site handoff and subsequent cancellation also passed.
Recorded root cause (certainty qualified above): `973ef70` equated nonempty with the requested answer; exact optional-label matching also incorrectly required printed trailing punctuation.
Fix: Suppress only explicit current-plan expected states proved by the existing verifier; normalize trailing label punctuation while retaining unique-field/protected-step checks. Preserve authorized unchanged-field skip.
Evidence: Both failing real traces preserved in `dist/verification/p13-*`; 342 focused cases pass, including failing→passing default-value and punctuation tests. `output/playwright/browser-real-guidance` proves corrected isolated source/Helper flow; final installed/Store replay pending.
Touches: Jarvis/Browser intervention bridge.
Protect: Normal conversation while paused.

### P16 — Direct Settings and Browser opt-out

**User-observed evidence:** Latest human local HowtoUseJarvis also BA; last3Chrome closes1. Simple searches/apps should stay direct.

**Current handoff assessment:** OPEN: Settings owner corrected+BAOFF logged, page visible and last3 unresolved.

**Causal certainty / suspected cause:** Settings semantic result ownership proven; last3 target selection not yet established.

**What still needs verification:** Fixed tab IDs only; visible local section; app/URL/search/mapping/opt-out BA OFF, genuine interaction BA ON.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Internal Settings sections, URLs/apps/search/mappings remain direct; explicit Browser Automation opt-out is authoritative.
Latest evidence: September 15 human: How to Use Jarvis Settings also started BA; 'close last 3 Chrome tabs' closed only one. Workflow exact-tab close passed; last-three selection remains OPEN, not permission for blind repeated Ctrl+W.
Recorded root cause (certainty qualified above): The model-selected internal Settings tool could be reclassified as webpage navigation when the transcript omitted product/settings words; its local result then lacked authoritative ownership.
Fix: Resolve semantic Settings tool arguments through the existing local result owner, while explicit transcript section constraints retain precedence. Preserve earlier direct-tab parser correction and safe fixed-set closure.
Evidence: Exact installed `5fddef4` local Settings pre-route resolved how-to-use once; speaker confirmed opening and answered follow-up, BA stayed OFF, zero reservations/mute restored (`settings-direct-20260916`). Settings page itself was not foreground in the screenshot, so visible navigation and last-three-tab handling remain OPEN. Two owner tests failed before source fix.
Touches: Direct-action intent/routing.
Protect: Browser Automation still starts for real interactive webpage work.

### P21 — Learned mappings and workflows

**User-observed evidence:** Human bare hostname learn failed earlier; later alias opened despite verbal refusal; recent workflow bare-domain blank.

**Current handoff assessment:** INVESTIGATING result/context, not durable store rewrite.

**Causal certainty / suspected cause:** Eager effects/schema capability mismatch proven; latest blank workflow cause unknown.

**What still needs verification:** Selected https/hostname/custom trigger/update/reset/restart, one Link/no Workflow, one coherent confirmation; inspect latest workflow transcript.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: INVESTIGATING
Expected: A selected URL or valid hostname plus a custom trigger stores one durable Link that executes after conversation/app restart and outranks generic lookup.
Latest evidence: Durable selected-hostname mapping and execution remain proven, but newest human run executed a mapping while Jarvis denied knowing its destination. Reopen result/context ownership; preserve durable Link storage.
Recorded root cause (certainty qualified above): Eager local mapping execution could precede a model refusal; the open_url schema hid its existing alias capability and setup advertised workflows but not Links.
Fix: Resolve/save mappings inside the existing tool execution owner; advertise bounded Link aliases without target data, and return the resolved URL as authoritative follow-up context. Persistence architecture is unchanged.
Evidence: Persisted store contains exactly one `Link` targeting `https://keyboardwtf.com/` and zero matching Workflows; visible Chrome/action-history runs at 07:06:03 and 07:10:39 opened exactly one destination each; restart changed process IDs; private CI `34176954928` passed.
Touches: Learned mapping parser/store/route.
Protect: Workflow/tombstone behavior.

### P02 — Exactly-once completion and action ownership

**User-observed evidence:** Human older duplicate open/close acknowledgements and contradictory 'closed/already closed'; latest Explorer completion substantially improved.

**Current handoff assessment:** Protected A/B installed effect-and-speaker evidence; broader late-session duplicate speech remains under P26/P38.

**Causal certainty / suspected cause:** Outer scheduling metadata and receipt authority corrected; separately streamed provider calls may still need exact trace.

**What still needs verification:** Final logical multi-action/workflow run with audible count, not only result-count assertions.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: One logical request may execute several actions but produces one coherent completion and no repeated mutation.
Latest evidence: Exact installed `8492daa` speaker capture and native receipts: ambiguous same-name A/B folders opened once each, unrelated conversation preserved context, close-both closed A/B once with one final spoken completion; unrelated same-name folder and Chrome survived.
Recorded root cause (certainty qualified above): Earlier eager effects/receipt expiry were corrected; the latest wire trace also proves duplicate scheduling was embedded in result data, not the provider's function-response metadata. Per-message result continuations remain broader than a logical request.
Fix: Existing dispatcher now emits outer WHEN_IDLE for ordinary results and SILENT for both guard/command-owner duplicate acknowledgements. No new batching owner, timer, provider switch or cancellation path; remaining multi-target ownership unresolved.
Evidence: `repair-8492daa/p31-ambiguous-pair` original speaker recording, offline transcript, exact action receipts and `p31-observed-mid` native windows; no second completion, no late action, reserved zero. Earlier three installed samples retained. Final candidate mixed/microphone acceptance remains P26/P38.
Touches: Transcript pre-routing; tool dispatch/result publication; action dedupe.
Protect: Clarification/barge-in and multi-action batching.

### P09 — Loud/full-scale PCM processing

**User-observed evidence:** Human Smart Writing froze; exact installed PCM arithmetic exceptions recorded.

**Current handoff assessment:** FIXED-SOURCE with meaningful prerecorded recovery; real long microphone gap.

**Causal certainty / suspected cause:** Math.Abs(short.MinValue) exists in baseline recorder too; not solely a new regression.

**What still needs verification:** Full-scale real capture, final tail/drain, terminal UI and one insertion.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Valid loud PCM never throws or drops microphone chunks.
Latest evidence: Installed Smart Writing microphone callbacks failed at 09:00:27 and 09:04:44 on September 10, leaving Listening visible.
Recorded root cause (certainty qualified above): Shared recorder and Settings meters still call Math.Abs(short.MinValue), which throws; previous Live arithmetic fix did not cover these paths. Recorder failure is only logged, not delivered to the capture owner.
Fix: Shared overflow-safe metering; existing capture monitor processes interrupted audio; await final input callback before WAV creation and retain cancellation ownership across drain.
Evidence: All PCM16 values plus drain/tail/interruption/cancel tests pass. Real short capture drains cleanly; 823s prerecorded desktop replay and 412s interrupted recovery each paste once. Long installed microphone acceptance remains open.
Touches: AudioRecorderService, VoiceCaptureService, WebSettingsService.
Protect: Audio quality, VAD and barge-in.

### P08 — Cancellation and stale-runtime safety

**User-observed evidence:** Human late actions and cancelled/stale work previously survived.

**Current handoff assessment:** FIXED-SOURCE; microphone drain dependency changed, affected installed recovery still needed.

**Causal certainty / suspected cause:** Capture start/drain ownership proven; no speculative new cancellation manager.

**What still needs verification:** Cancel during mic start/drain/processing/send; no later UI/paste or focus.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Cancelled work cannot later open the microphone, post native input, publish results or overwrite a newer task.
Latest evidence: Earlier installed startup-cancel proof remains valid, but recorder drain/recovery now changed this dependency. New failing-then-passing test prevents interrupted drain from overwriting Cancelled; affected installed replay is required.
Recorded root cause (certainty qualified above): Capture ownership was published only after blocking microphone startup, so cancel could not invalidate the in-flight start.
Fix: Make microphone startup hold the existing operation generation and discard/close any start that loses it.
Evidence: Failing trace at 23:37:47–23:37:55; passing installed trace at 23:53:32–23:53:33; 1,024 desktop tests and prior 1,500 race repetitions.
Touches: Voice capture operation ownership.
Protect: Native/input/Live cancellation checks and fast voice-mode startup.

### P20 — Long Smart Writing

**User-observed evidence:** Human Smart Writing frozen/red indicator/no polishing, long speech must not disappear.

**Current handoff assessment:** OPEN real long installed microphone.

**Causal certainty / suspected cause:** Arbitrary cap/provider request and PCM/drain failure paths corrected; quality/long real mic unproven.

**What still needs verification:** ~10min+ real capture, ordered full text/partial recovery, cursor/clipboard, no duplicate settlement.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Roughly 10-minute-plus capture has no arbitrary app cutoff; successful audio is preserved as ordered full or clearly partial output, inserted once, with correct clipboard/UI/settlement.
Latest evidence: Installed human microphone froze twice through P09; a 605s prerecorded TEST pass did not exercise that callback. Reverify real capture, processing, interruption, tail preservation, one insertion, clipboard and terminal UI.
Recorded root cause (certainty qualified above): A five-minute desktop cap and whole-request provider size/deadline made valid long capture impossible and discarded provider-failure results.
Fix: Remove Smart Writing auto-cap, allow long/manual Dictation, segment cloud audio under one operation, and recover captured text without duplicate insertion.
Evidence: 561 focused tests cover segmentation/cancellation/recovery; TEST desktop operations `604887910331` and `3c920af06fb1` ended Done with one paste each. Final exact installed microphone/settlement acceptance remains pending.
Touches: Smart Writing transport/UI/clipboard/settlement.
Protect: Existing writing quality.

### P37 — Long Raw Dictation and capture failure recovery

**User-observed evidence:** User long Raw may truncate/freeze/paste twice/lose captured speech.

**Current handoff assessment:** FIXED-SOURCE+823s/412s prerecorded desktop; real microphone OPEN.

**Causal certainty / suspected cause:** Empty recognizer result bypassed exception-only recovery; shared recorder arithmetic.

**What still needs verification:** Long actual mic and forced partial failure, exactlyonepaste, tail/clipboard/UI; inspect current saved duration.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Meaningful long input preserves all captured audio/tail, returns usable partial output on failure, inserts once, and leaves no stale UI.
Latest evidence: 823s/70-section prerecorded desktop replay and forced 412s interruption retain 70/35 sections, paste once at the cursor, preserve clipboard and clean UI. Short real speaker→mic recognition remains unreliable; final microphone proof pending.
Recorded root cause (certainty qualified above): Engine failures return empty text; exception-only preview recovery never sees them.
Fix: Recover the current Raw preview at the existing completion owner for empty final results too; never recover executable commands.
Evidence: Empty-result regression fails→passes; 63 focused tests. `dist/verification/hardware-audio/long-control/desktop*.json`. Production retains its saved 30s Raw limit; isolated replay used existing manual/unlimited mode without changing that profile.
Touches: AudioRecorderService, VoiceCaptureService, delivery/clipboard.
Protect: No arbitrary duration cap; no duplicate settlement or paste.

### P04 — First-utterance farewell

**User-observed evidence:** Human goodbye intermittently failed; later go-away worked; some clean first-goodbye samples also exist.

**Current handoff assessment:** OPEN: internal/final-text passes do not close mandatory human recognition.

**Causal certainty / suspected cause:** No new ASR cause proven; preserve current local ordering rather than rewriting phrases.

**What still needs verification:** First recognized farewell after normal/tools/cancel/BA/long silence/late session, no speech/restart afterward.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: A clear recognized farewell ends locally before tools, Browser Automation or model speech.
Latest evidence: A late-session TEST `goodbye` ended the active logical session once, but exposed a Browser Partial-task cancellation announcement attempting local speech after Live shutdown.
Recorded root cause (certainty qualified above): The end tool lacked an absent-transcript fallback, and conversation shutdown reused the user-visible Browser cancellation path.
Fix: Keep transcript-first local authority with the bounded end-tool fallback, and make conversation-owned Browser cleanup silent.
Evidence: 473 focused tests plus rebuilt TEST session `ebcaa5...`: one socket shutdown, empty context, cancelled Browser state, and no model/local/TTS response; final production microphone proof pending.
Touches: Live transcription/event ordering; session stop.
Protect: Audio/VAD/barge-in and immediate local shutdown.

### P05 — Conversation/random-action safety

**User-observed evidence:** Human earlier unsolicited shortcuts, Chrome zoom/Search Tabs/Edge/Settings and lock; latest normal speech much better.

**Current handoff assessment:** OPEN final human safety; native/input fixes separately protected.

**Causal certainty / suspected cause:** Do not infer a tool call from background gibberish user admitted speaking.

**What still needs verification:** Real conversation/interruptions and user modifier activity; no unintended mutations.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Conversation never mutates the computer; explicit actions execute once.
Latest evidence: Exact installed `5d6d101` clean conversation replay gave normal speaker replies, zero actions and no Browser task; input was temporarily muted and restored. Earlier screen calls coincided with user-confirmed spoken gibberish and are not evidence against the scripted questions.
Recorded root cause (certainty qualified above): Earlier model/direct/input ownership allowed stale or unsafe mutations.
Fix: Final authoritative transcript, serialized action owner, balanced input and stale-generation rejection.
Evidence: `repair-5d6d101/conversation-isolated-20260914` contains real speaker audio, offline transcript, empty action history and restored-mute proof; startup 2.889s, farewell shutdown 138ms, reserved balance zero. Installed final-text and no-mic provider replays pass; human speech/barge-in remains open under P04/P38.
Touches: Live dispatch; command routing; trusted input.
Protect: Direct-action speed and normal conversation.

### P38 — Natural audio, barge-in and prompt/tool intelligence

**User-observed evidence:** Human lessintelligent/glitchy/contradictory conversation, simple app confusion; latest speech improved.

**Current handoff assessment:** OPEN integrated human audio/tool intelligence; do not equate developer model probe with production.

**Causal certainty / suspected cause:** Some dispatch/context defects proven separately; prompt growth/noise not generic provedcause.

**What still needs verification:** Natural actual mic/speakers/barge-in, general composition and grounded failurefollowup.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Smooth speech/playback; compact consistent prompt/tools; semantic app/file/tab references and practical tool composition.
Latest evidence: Both compiled clients use 59 tools and the same default model, but actual production rewrites setup to Vertex native-audio 2.5; developer probes used 3.1 preview. September 13 route/settled usage confirms that distinction. Prompt growth alone is not causation.
Fix: Pending proven differential causes; no phrase grammars or competing owners.
Evidence: `docs/regression-differential-1.4.17.md`; installed failed speaker run retained. Align diagnostic provider/setup before interpreting isolated reasoning passes; do not upgrade production to hide a test mismatch.
Touches: Live prompt/tools/audio/dispatch and recent action context.
Protect: Safe native actions, bounded memory, sequential resumption.

### P32 — Silence must preserve the logical Jarvis session

**User-observed evidence:** Human long silence caused disappearance/130sec reconnect UI earlier; latest brief rollover fast.

**Current handoff assessment:** FIXED with642s quiet/same-context speaker evidence; final human/visible proof pending.

**Causal certainty / suspected cause:** UI revision recovery established; providerGoAway may precede10min.

**What still needs verification:** Long silence→resumed utterance/context→farewell; don't force physical socket duration beyond provider.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Silence alone never dismisses Jarvis or loses context; provider rollover/recovery stays internal until explicit exit.
Latest evidence: Exact installed `5d6d101` survived 642s silence, provider GoAway and 1.6s sequential resumption on the SAME logical session, then correctly spoke the prior project name/number. Final-text go-away stopped in 71ms; zero actions/tasks/reservations and microphone restored.
Recorded root cause (certainty qualified above): Sequential transport recovery restored socket/session ownership but did not clear the reconnect UI revision.
Fix: Keep provider-independent logical recovery and restore Listening only when the reconnect revision still owns the overlay; never overwrite newer speech/tool UI.
Evidence: `repair-5d6d101/quiet-rollover-20260914`: real speaker recording, timestamped input, resumption logs, offline first/last audio excerpts. TEST `1df62f...` UI revision proof retained. Final visible-overlay/human-microphone continuity remains pending while Windows control cannot initialize; not a human-ASR pass.
Touches: Logical session/provider-close UI boundary.
Protect: 720s lease, 120s grace, paid sequential resumption, audio/VAD.

### P01 — Startup consistency

**User-observed evidence:** Human latest: startup under3 seconds, clear speech/listening; earlier ~6 seconds inconsistent.

**Current handoff assessment:** Protected installed startup evidence, not a guaranteed remote SLA.

**Causal certainty / suspected cause:** No additional cause established beyond measured refresh/network stages.

**What still needs verification:** Final frozen mixed-session start/restart; do not chase cosmetic milliseconds.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Jarvis normally reaches Listening near 2.5–3s, with reliable cold/warm/restart behavior and no unexplained failures.
Latest evidence: Exact installed `5d6d101` after installation/repeated starts/overnight idle: 2.911s / 2.826s / 2.650s / 3.632s, zero retries/auth waits and 0–4ms local preflight. The slowest sample is remote socket/setup latency; no honest always-under-3s guarantee.
Recorded root cause (certainty qualified above): Slow sample occurred after the app idled past access-token expiry, adding a 2.936s refresh to the normal backend/provider handshake.
Fix: Refresh signed-in access tokens shortly before expiry in the background; keep the existing on-demand authenticated fallback.
Evidence: `repair-5d6d101` audio-run logs and sessions `b24c49...`, `0a3973...`, `e11170...`, `0d018d...`; normal proactive auth refresh completed before the post-overnight start. Keep final mixed-session startup check under P26; no further startup code change justified.
Touches: Paid account refresh; Live startup transport.
Protect: 2.37–3s happy path, microphone readiness, reliable connection.

### P03 — Jarvis popup topmost

**User-observed evidence:** Human Chrome covered Jarvis overlay previously.

**Current handoff assessment:** Protected native topmost/no-activate evidence; old text about pending multi-app acceptance is not a new failure.

**Causal certainty / suspected cause:** No new root cause established.

**What still needs verification:** Final visible Chrome/Explorer/Edge transition without focus theft where affected.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: A visible Jarvis overlay remains above ordinary apps without stealing focus.
Latest evidence: Exact installed overlay retained `WS_EX_TOPMOST | WS_EX_NOACTIVATE` while Chrome kept foreground focus; prior TEST proof covered Chrome, Edge and Explorer transitions.
Recorded root cause (certainty qualified above): Topmost was only a managed property; recreated native handles did not carry the topmost extended style.
Fix: Every overlay HWND now owns `WS_EX_TOPMOST | WS_EX_NOACTIVATE` directly.
Evidence: Focused native-style test plus TEST multi-app proof and installed HWND `2296258` style verification.
Touches: `VoiceOverlayForm` window lifecycle.
Protect: No-focus-steal interaction and active-app typing.

### P06 — Deterministic Explorer close

**User-observed evidence:** Human latest closeboth immediately worked; earlier wrong-window operations severe.

**Current handoff assessment:** Protected fixed native target set and unrelated Chrome preservation.

**Causal certainty / suspected cause:** No new close defect proven.

**What still needs verification:** Retain target identity/cancel proof; rerun if dependent native code changes.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Close a fixed set of actual Explorer windows only, verify disappearance, never kill the shell or affect browsers.
Latest evidence: Exact installed `8492daa` natural close-both closed only A/B paths; same-title third and two Chrome windows remained. Latest human confirms immediate correct Explorer closure.
Recorded root cause (certainty qualified above): Retired fallback used browser/foreground shortcuts instead of Explorer HWND ownership.
Fix: Fixed native Explorer handle snapshot, identity validation, native close and verification.
Evidence: Human session; `native-window-acceptance` including mid-close/pre-cancel cases; `production-folder-trace/native-path-close-20260914` (no visible browser during this sample, not installed P31 proof).
Touches: Native window closer.
Protect: No blind Ctrl+W/Alt+F4 and no `explorer.exe` termination.

### P07 — Modifier and foreground safety

**User-observed evidence:** Human Win+Shift+S overlapped earlier unintended shortcuts; contamination was suspected.

**Current handoff assessment:** Protected balanced input/foreground/race evidence.

**Causal certainty / suspected cause:** Exact causal attribution of every historical shortcut not established; do not claim Win+Shift+S caused it.

**What still needs verification:** Affected final input boundary test with held modifiers, cancellation and changed foreground.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: User-held modifiers, focus changes and cancellation cannot redirect or contaminate injected input.
Latest evidence: Exact installed build passed balanced-input, HWND validation and 1,500 cancellation/race repetitions.
Recorded root cause (certainty qualified above): Earlier key injection lacked one authoritative key/target owner.
Fix: Balanced batches, held-modifier refusal, target validation and final cancellation checks.
Evidence: Full gate and adversarial race harness at `7824b192...`.
Touches: Trusted keyboard/input boundary.
Protect: Fast explicit direct actions.

### P31 — Referential direct-action context

**User-observed evidence:** Human 'close it' failed earlier; current exact Explorer close works, ambiguous duplicates uncertain.

**Current handoff assessment:** Protected installed canonical A/B result and one speech; small duplicate-result UX not a blocker without evidence.

**Causal certainty / suspected cause:** Canonical identity loss proven; current uncertain same-folder explanation not yet verified.

**What still needs verification:** Only investigate actual distinct target failure; preserve safe close and general semantics.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: A contextual close resolves the recent successfully opened target, asks once if ambiguous, and never broadens to unrelated windows.
Latest evidence: Exact installed `8492daa` ambiguity→open both→unrelated conversation→close both resolves A/B full paths once; same-title unrelated window and two Chrome windows survive. Original speaker recording confirms one final open/close completion.
Recorded root cause (certainty qualified above): Native folder receipts/prompts reduce canonical paths to shared titles and window_action permits a missing target; distinct folders lose identity. Earlier workflow/tab receipt ownership fixes remain protected.
Fix: Preserve canonical folder receipts; require explicit tool target; resolve full paths through Shell windows at the existing native owner, revalidate before posting close, and refuse mixed/unknown tabs. No basename fallback, new owner or routing grammar. Existing workflow/Chrome receipt fixes retained.
Evidence: `production-folder-trace/current-source-20260914` uses real production model/billing and current dispatcher with simulated effects; native-path-read-three/native-path-close show actual paths and two verified closes, unrelated preserved, cancellation/cleanup pass. Shell also exposes the real Home+folder tabs, including unknown Home path. 112 focused and 1,146 desktop/262 backend/12 Firestore pass.
Next: Protected installed evidence: `repair-8492daa/p31-ambiguous-pair` and `p31-observed-mid`; reservations zero, input mute restored. Final mixed/microphone acceptance remains P26/P38; latest uncertain duplicate-folder report is not yet a proven new defect.
Touches: Live tool instruction/context; recent action ownership.
Protect: Deterministic native Explorer close and general Gemini reasoning.

### P39 — Intermittent Helper availability

**User-observed evidence:** Human Helper intermittent; later explicitly said extension updated/fixed.

**Current handoff assessment:** Current Store connected/protocol2.1.21 NOW; historical actualStore2.1.27 activation and NASA effects prove beyond source.

**Causal certainty / suspected cause:** Heartbeat blocked/page lease/readiness mismatch proved and corrected.

**What still needs verification:** Only affected installed matrix; stop treating old Store update as blocker unless new testfails.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Direct tabs/Browser tasks share the real Store Helper reliably; failures are truthful and cancellable.
Latest evidence: After Chrome restart, actual Secure Preferences selects 2.1.27 and installed Store Helper reports fresh, command-ready form state without errors. Earlier 2.1.26 activation failure is resolved; successful direct-action/BA matrix remains next.
Recorded root cause (certainty qualified above): Page work blocked heartbeat/cancellation; capability checks also covered new sends but not advertised readiness or in-flight dispatch/result publication.
Fix: Preserve heartbeat/waiter safety; the existing bridge requires the same capability for readiness, queued dispatch and result acceptance. Store 2.1.27 delivery/acceptance remains required.
Evidence: Earlier failure retained in `repair-5d6d101/direct-browser-20260914`; September 14 15:05 actual active path `2.1.27_0`, Store=true, currentTabAutomationAvailable=true on the authorized Google Form. Windows/browser control recovered; no registration/source change.
Touches: Helper communication and action ownership only if proven.
Protect: Protocol 2.1.21, native trust/registration, original 1.4.17 compatibility; do not publish unaccepted Helper bytes.

### P23 — Website Pricing layout and auth state

**User-observed evidence:** Human repeated live layout/auth false-pass claims; now open real authenticated Account available.

**Current handoff assessment:** OPEN only remaining states/flow: historical canonical responsive+active-plan proof retained.

**Causal certainty / suspected cause:** Deployed shell mismatch proven; any remaining same-page sign-in issue needs exact auth event trace.

**What still needs verification:** Real signed-in/no-plan and sign-in-on-Pricing CTA hydration; desktop/tablet/mobile real deployment.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Compact hero, card/notice aligned, FAQ then reviews, no real mobile overflow, and signed-out/no-plan/active-plan hydration without re-sign-in.
Latest evidence: September 12 real canonical Account showed the designated account and active plan; header Pricing, reload and Account return all hydrated automatically to Manage current plan without sign-in. Card/notice align at 390/768/1440px without horizontal overflow.
Recorded root cause (certainty qualified above): Old deployed Pricing shell differed from corrected source; real authenticated-state failure still needs tracing.
Fix: Deployed existing shell alignment/session-hydration correction; app.js, account.js and desktop-login.js are byte-equivalent to prior deployment. Payment/auth logic unchanged.
Evidence: Real canonical signed-out and active-plan responsive DOM/visual checks pass; real signed-in/no-plan remains unverified. Public binary stays original 1.4.17. Browser-control screenshot/wait timeouts were checked against the actual completed page, not counted as product failures.
Touches: Production site Pricing CSS/JS/auth hydration.
Protect: Razorpay identifiers and billing semantics.

### P43 — One public support address

**User-observed evidence:** User only support@keyboardwtf.com public; do notchangeinternaladminemails.

**Current handoff assessment:** FIXED deployed/app text; liveStorelistingstillunverified.

**Causal certainty / suspected cause:** Stalepubliccontacttext known.

**What still needs verification:** ReadrealStorelisting/customercontactsurfaces; no cosmeticexpansion.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: FIXED
Expected: Customer-facing app, site and Helper support references use only support@keyboardwtf.com; internal/admin identities remain unchanged.
Latest evidence: Canonical/fallback support/privacy/refund/terms and exact installed `3636587` Settings visibly use only the support address. Live Store listing remains unverified; source listing text is corrected.
Fix: Replace public contact links only; verify actual deployed pages and the next installed resource.
Evidence: Twelve contact checks fail→pass; native installed Settings screenshot/read-back and real deployment DMSjeF3uQwj1A9BH5RMwheYLFgC2 pass. Four contact pages changed; download config/signed stable manifest unchanged.
Touches: Public support text/links only.
Protect: Auth/admin identities, payment settings, stable 1.4.17 public artifact and frozen candidate provenance.

### P41 — Bounded model and long-term cost audit (after core repair)

**User-observed evidence:** User later requests model/cost audit including claimedGemini3.8Flash andJan2027standardrates.

**Current handoff assessment:** OPEN deferred; actualroutes inventoriedNOW, lineup/pricesnotverified.

**Causal certainty / suspected cause:** No claim newer model fixes ownership; thinkingBudget config/request discrepancy proven.

**What still needs verification:** Official current models/prices + representative benchmarks; onlycompatible nonLive change ifclearlybetter, preserveeconomics.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Inventory every actual AI route; benchmark compatible alternatives only after architecture stabilizes; use verified January 2027 standard rates including thinking/output usage.
Latest evidence: User requested later evaluation including Gemini 3.8 Flash; availability, compatibility and pricing not yet verified.
Fix: Deferred audit; change model only on representative task/latency/reliability/cost evidence, preserve Live native audio and customer billing unless proven internal metering adjustment is necessary.
Evidence: Actual route/config inventory, official pricing and comparative task receipts required.
Touches: Model configuration only if justified.
Protect: Regression priority, billing math/reservation/settlement/history/exactly-once charge.

### P42 — Real payment identity and canonical Razorpay domain

**User-observed evidence:** User ₹999payment under unknowngod2024@gmail.com; two Razorpayemailsshowfallbackdomain.

**Current handoff assessment:** OPEN real identity/domain audit, no paymentmadehere.

**Causal certainty / suspected cause:** No specificwrongUIDcauseproven; customer-facingbrandingcauseunknown.

**What still needs verification:** ReadUID→customer/subscription/payment/webhook/entitlement/credits/relogin; actualmerchantoutput, notsourceonly.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Latest ₹999 purchase belongs exclusively to the authenticated Firebase UID for the user-designated account; customer-facing payment output uses keyboardwtf.com.
Latest evidence: User reports purchase under unknowngod2024@gmail.com and two Razorpay emails displaying the fallback Vercel domain; ownership/domain cause not yet verified.
Fix: At account/payment acceptance, trace UID→customer/subscription/payment→webhook→entitlement/credits→restart; inspect real merchant/checkout/callback configuration and output before changing it.
Evidence: Read-only production chain and actual customer-facing output required; do not generate another charge without authorization.
Touches: Existing payment identity/config boundary only if proven.
Protect: Firebase UID authority, account isolation, internal infrastructure URLs and existing billing semantics.

### P17 — Production Browser credit capacity

**User-observed evidence:** User previously insufficient25-credit planner reservation, later restored usable credits.

**Current handoff assessment:** Protected capacity recovery; NOW reserved0. Historical402 evidence not current Helper diagnosis.

**Causal certainty / suspected cause:** Account capacity explained402; later409 needs own rate/concurrency trace.

**What still needs verification:** Recheck read-only capacity before bounded production work, no billing bypass.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Final bounded production Browser acceptance can reserve and settle normally.
Latest evidence: User restored capacity; latest installed human session established paid Live and received successful HTTP 200 Browser plans. Later HTTP 409 rate limit is not insufficient-credit failure.
Recorded root cause (certainty qualified above): Acceptance account capacity, not Helper/native messaging.
Fix: Use isolated non-shipping test capacity now; run bounded production acceptance when capacity exists.
Evidence: Task `eadf43...`; Live session `35295324...` ended after four sequential sockets, then reconnects and fresh sessions `13e5cc...`/`a4bde09...` received 402 after successful auth refresh.
Touches: Test execution only.
Protect: Production billing rules and balances.

### P18 — Isolated Gemini/Jarvis/Browser test harness

**User-observed evidence:** User requested separate TEST and reported it hijacked Settings shortcuts.

**Current handoff assessment:** Historical isolated test proof; no test session running in handoff; old profile hashes not current.

**Causal certainty / suspected cause:** Compile-time isolated harness worked, but developer model differs from production.

**What still needs verification:** If reused, new source/process/profile/config/native identity isolation proof; no production reservations.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Separate Ctrl+Alt+T TEST process/profile exercises real Jarvis UI/mic plus coordinator/executor/Helper without production billing/provider/data mutation.
Latest evidence: The disposable TEST process exercised real Jarvis, Browser Automation, Helper and long Smart Writing while production executable/profile/native-host hashes and billing stayed unchanged; all TEST processes are now stopped.
Recorded root cause (certainty qualified above): Production credit reservation blocks repeated acceptance.
Fix: Compile current source into an ignored TEST-only executable/profile; load the key through masked process memory, compile registration bypass out of production, and register only explicit TEST hotkeys.
Evidence: Ctrl+Alt+T launched the isolated process; production settings SHA remained `8506D843...`, installed EXE SHA `1B217B33...`; Store Helper 2.1.26 connected without registry rewrite and paid/auth stayed off.
Touches: Compile-time TEST seam plus ignored test project only.
Protect: Production auth, provider, Firestore and billing.

### P19 — No test provider in shipping artifacts

**User-observed evidence:** User prohibits test keys/hotkey/provider/BYOK in release.

**Current handoff assessment:** Protected older scans, rerun every final artifact after changes.

**Causal certainty / suspected cause:** Preventive requirement, not a claimed secret incident.

**What still needs verification:** Scan tracked source/package/installer/installed/native host/site/config/logs; no secret output.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Final source/package/install/site/logs contain no test keys, hotkey, provider selector, URL or billing bypass.
Latest evidence: Tracked source, package, installer and exact installed executable contain no TEST symbol/provider/hotkey or supplied test-key material.
Recorded root cause (certainty qualified above): Preventive release requirement.
Fix: Remove the disposable seam after acceptance and scan tracked source plus final package/install independently.
Evidence: Full secret gate plus package, installer and installed-tree scans at `e01f33a...`; only expected public Firebase browser configuration remains on the sites.
Touches: Packaging/release verification.
Protect: No BYOK or hidden production bypass.

### P33 — Production/TEST hotkey ownership

**User-observed evidence:** Human TEST reset stole Ctrl+Alt+, and Settings lost AccountBilling.

**Current handoff assessment:** Protected isolated allow-list fix and physical production/TEST shortcut proof.

**Causal certainty / suspected cause:** Settings replacement bypassed startup filtering.

**What still needs verification:** If TEST relaunched inspect actual saved bindings and registry/profile; onlyTEST shortcuts, no production mutation.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Production owns its configured shortcuts; TEST owns only Ctrl+Alt+T and TEST cancel, and settings changes cannot cross that boundary.
Latest evidence: run16 writes only `%LOCALAPPDATA%\keyboard.wtf-test`, registers only TEST cancel, and physical Ctrl+Alt+, opened production Settings on port 8091; Ctrl+Alt+T launched TEST Live through the isolated launcher.
Recorded root cause (certainty qualified above): TEST filtering exists only in initial registration; the settings replacement path bypasses it and registers production bindings.
Fix: Apply the same compile-time TEST allow-list at the single hotkey replacement owner, preserving both profile files.
Evidence: TEST log `02:15:48` shows only Ctrl+Alt+Shift+X; production settings SHA stayed `8506D843...`; the replayed reset could not take any production binding or mutate production/candidate profiles.
Touches: `HotkeyService` TEST seam and focused tests.
Protect: All production hotkey values and unrelated user settings.

### P35 — Bounded personal memory

**User-observed evidence:** User asked useful bounded memories last, no raw selection archive.

**Current handoff assessment:** Protected existing memory owner; no new implementation needed.

**Causal certainty / suspected cause:** Acceptance gap only.

**What still needs verification:** Small relevant retrieval/privacy/store view-delete-clear; preserve Links/workflows separation.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Store only explicit/useful concise non-sensitive facts and retrieve a small relevant subset; expose view/delete/clear, adding a disable toggle only if it remains a single clean owner.
Latest evidence: Exact installed Settings exposes the existing bounded memory owner separately from four mappings and two workflows; one durable entry is loaded within a 20-entry/280-character cap.
Recorded root cause (certainty qualified above): This was an acceptance gap, not a missing architecture; the existing owner already enforces explicit save intent, bounded retrieval and same-key update.
Fix: Keep the existing owner and avoid a late competing memory system; no separate disable state was added because memory is explicit-only and clearable.
Evidence: 29 focused persistence/dedupe/relevance/privacy tests pass; prompt digest is bounded to 10 entries/900 characters and installed Settings supports view/delete/clear.
Touches: Existing intent memory and Settings only.
Protect: Startup latency, selected-text privacy, mappings/workflows and prompt bounds.

### P36 — Routine OS notification spam

**User-observed evidence:** User routine opened/closed/tab notifications spam Windows.

**Current handoff assessment:** Protected existing notification boundary, no new polish.

**Causal certainty / suspected cause:** Info/InfoTransient unconditionally ballooned previously.

**What still needs verification:** Routine oneJarvis response/noOSballoon; actionable warning/backgroundattention still works.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Routine success stays in Jarvis UI/speech; only attention-worthy completion, warning or failure creates an OS notification.
Latest evidence: Exact installed `e01f33a...` contains the single notification boundary; routine success has no system-publication path while explicit attention/failure retains one.
Recorded root cause (certainty qualified above): `NotificationService.Info` and `InfoTransient` unconditionally publish a Windows balloon regardless of purpose.
Fix: Make routine information silent at the system boundary and require explicit attention severity for useful non-error notifications.
Evidence: 3 focused boundary tests, 1,027 desktop, 262 backend and 12 Firestore tests pass; private CI `34424810051`, clean-profile runtime, byte-identical install and no-bypass scan are green.
Touches: Notification publication boundary; timer and long Browser-task completion classification.
Protect: Jarvis status/speech, actionable NeedsUserAction/failure notices and persistent logging policy.

### P22 — Live lease/resumption/accounting

**User-observed evidence:** Human needs long continuity without old~230sec reclaim; newer long sessions improved.

**Current handoff assessment:** Protected policy and settlement evidence; current route/service read does not itself prove all ledger paths.

**Causal certainty / suspected cause:** Short legacy lease known; preserve fixes.

**What still needs verification:** Affected final long sequence with sequential sockets/exact usage; no gratuitous backend changes.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: 720s authoritative lifecycle, 120s cleanup grace, Cloud Run 900s, sequential resumption, no duplicate socket/409 and exactly-once settlement.
Latest evidence: Current config/revision and long-session tests preserve post-1.4.17 lease fix; latest sessions settled cleanly.
Recorded root cause (certainty qualified above): 1.4.17-era reservation ownership used a retired short lifetime.
Fix: Server-owned long lease with bounded grace and sequential physical sockets.
Evidence: Backend 262, Firestore 12, prior >8m continuity and production settlement checks.
Touches: Live backend/session accounting.
Protect: Do not alter Live/audio/VAD architecture.

### P44 — Inactive resource and credit cleanup

**User-observed evidence:** User wants unused Jarvisreleaseusage but longquietactiveconversationstayavailable.

**Current handoff assessment:** OPEN final exitpathmatrix; currentreadreserved0/noactivetask.

**Causal certainty / suspected cause:** No newidlebugproven; preserve explicitlogicalexit vsquietpause distinction.

**What still needs verification:** Dismiss/cancel/exit/restart clean sockets/audio/tasks/reservations; retain trayhotkeys/Helper.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Stopped/dismissed Jarvis releases Live, microphone, work and reservations; quiet pauses in an active logical conversation preserve context.
Latest evidence: Exact installed `5d6d101` long quiet conversation resumed correctly (P32), then final-text go-away/stop released both sequential sockets, speaker capture and input guard; zero reserved credits, empty Browser task and unchanged profile. Earlier stopped overnight idle proof remains valid.
Fix: Verify existing cleanup/restart ownership; do not add a silence-driven logical exit or alter billing/lifecycle policy.
Evidence: September 12 socket/shutdown/account read-back; remaining exit paths and bounded restart acceptance pending.
Touches: Existing session/resource lifecycle only if a failure is reproduced; test-resource cleanup.
Protect: Tray hotkeys/Helper readiness, long-silence continuity, 720s policy, 120s grace, exactly-once settlement.

### P25 — Core installed feature smoke

**User-observed evidence:** User requires all actual installed modes/tools, not fake integration proof.

**Current handoff assessment:** OPEN composite smoke.

**Causal certainty / suspected cause:** Acceptance gap, no invented cause.

**What still needs verification:** Raw/Smart/screen/selection/files/apps/search/Settings/Account/Helper/updater together.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Raw Dictation, Screen Reasoning, selected text, app/URL/file/folder/search, Settings, Account/Auth, Helper and updater all work together.
Latest evidence: Exact installed `e01f33a...` is signed in, owns all five production hotkeys, opens the microphone diagnostic, connects only Store Helper, preserves mappings/workflows, and reports the updater current; final human voice/writing smoke remains.
Recorded root cause (certainty qualified above): Final release acceptance gap.
Fix: Bounded installed checks, reusing unaffected evidence.
Evidence: Final matrix pending.
Touches: Test execution; fix only proven defects.
Protect: Stable subsystem architecture.

### P26 — Long-duration mixed session

**User-observed evidence:** Human degradation appeared after several tasks.

**Current handoff assessment:** OPEN full final mixed run.

**Causal certainty / suspected cause:** No single cause established for every late-session issue.

**What still needs verification:** Run specified long sequence, capture speech/task/socket/resource/account timeline once after coherent repairs.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Extended mixed use has no stale state, duplicate speech/tasks/sockets, recognition degradation, leaks, reconnect loops or orphan reservations.
Latest evidence: Individual long Live evidence exists; final mixed-session sequence is incomplete.
Recorded root cause (certainty qualified above): Final release acceptance gap.
Fix: One realistic installed sequence after source freeze.
Evidence: Final timeline/resources/accounting pending.
Touches: Whole installed product.
Protect: Avoid repeated paid sessions.

### P30 — Accounting and test-state cleanup

**User-observed evidence:** User requires no orphan reservations/resources or test secrets.

**Current handoff assessment:** OPEN reconciliation history; NOW this account reserved0. Snapshot10.7325balance is not current.

**Causal certainty / suspected cause:** Historical cancelled Firestore accounting RPCs documented; present zero doesn't independently prove seven quarantines reconciled.

**What still needs verification:** Read authoritative historical ledger/provider evidence at final audit; no ad hoc credit adjustments.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: No active/orphan test task/session; reservations settle once or are validly reclaimable; no test secrets/config remain.
Latest evidence: Runtime/test state is clean; seven expired Live reservations are explicitly quarantined and exactly equal the 10.7325-credit reserved balance.
Recorded root cause (certainty qualified above): Pre-`bd1d248` Live shutdown cancelled in-flight Firestore budget/checkpoint transactions after provider dispatch, leaving no final completion evidence.
Fix: Preserve the deployed shutdown-safe accounting fix; resolve historical quarantines only through audited operator reconciliation with provider evidence.
Evidence: Firestore/account totals match exactly; all seven traces show cancelled accounting RPCs, and the healthy six-hour maintenance job reports seven quarantines rather than silently releasing them.
Touches: Test state and accounting inspection.
Protect: Billing math, plan allowance and existing balances.

### P27 — Final security/reliability audit

**User-observed evidence:** User asks one final material whole-system audit, not endless speculative reviews.

**Current handoff assessment:** OPEN after known failures closed.

**Causal certainty / suspected cause:** Preventive final gate.

**What still needs verification:** Security/isolation/ownership/dependencies/config drift/input/billing/release integrity, no cosmetic refactors.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: No release-impacting auth/isolation/secret/input/Helper/updater/dependency/config/accounting defect.
Latest evidence: One bounded final review remains. A prior hotkey-test teardown timeout has not recurred in four full desktop runs; its cause is still unproven and diagnostic phase reporting remains enabled.
Recorded root cause (certainty qualified above): Final release gate.
Fix: Review only material attack/failure paths and repair proven defects.
Evidence: Final report pending.
Touches: Whole project.
Protect: No speculative rewrite.

### P28 — Exact current installer/install provenance

**User-observed evidence:** User requires exact installed candidate, agent should run installer if possible.

**Current handoff assessment:** NOW installed5fddef4 byte identical; snapshot8492 hashes are STALE. DirtyP15 not packaged.

**Causal certainty / suspected cause:** No provenance defect shown; stale documentation corrected in handoff.

**What still needs verification:** Freeze new SHA only after gates, package/install/hashes; final manual readiness not established.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Installed bytes derive from the frozen source and exact hashed installer.
Latest evidence: Installed frozen `8492daa5271d569f6d78f1187e5d69930a68a8d8` EXE `49D83807...CF7CD` matches package (314,817,998 bytes); installer `160DB01F...5C478` is 84,294,110 bytes. Production profile hash remains `8506D843...5850`.
Recorded root cause (certainty qualified above): Release-integrity requirement.
Fix: Freeze source, build once, hash, install exact bytes and independently verify provenance.
Evidence: `dist/verification/repair-8492daa/provenance.json`; CI `34855703571`, 1,146 desktop/262 backend/12 real Firestore, clean-profile runtime and independent installed hash agree. Physical P31 acceptance in progress; not final human acceptance.
Touches: Build/install/provenance.
Protect: Do not rebuild without a source defect.

### P24 — Public website/release still old

**User-observed evidence:** User explicitly rejected repaired public download and requested original fallback.

**Current handoff assessment:** NOW canonical/fallback manifests and public GitHub latest1.4.17; local original artifact rehashed.

**Causal certainty / suspected cause:** Intentional public rollback, not code rollback.

**What still needs verification:** Do not publish repaired release; recheck actual download bytes at final release stage.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: PHYSICALLY VERIFIED
Expected: Public canonical/fallback/download/updater/GitHub latest use the original verified 1.4.17 installer until a repaired candidate is manually accepted.
Latest evidence: Canonical and fallback live config/signed manifests plus GitHub latest now advertise 1.4.17. Actual canonical Download CTA downloaded 84,251,836 bytes matching original SHA 812aaa43...ded6cd; local 1.4.19 install preserved.
Recorded root cause (certainty qualified above): User rejected newer candidate and explicitly requested temporary public rollback only.
Fix: Restore original signed download metadata, preserve local repair branch/install, verify actual public downloaded bytes; never rebuild/relabel 1.4.17.
Evidence: Deployment `site-1kxoy9cdc`; original signature validated by production verifier, hashes independently matched, replay/downgrade rejected for installed 1.4.17/18/19; `dist/verification/baseline-1.4.17`.
Touches: Release metadata/sites/updater.
Protect: No premature public release.

### P29 — Final immutable release synchronization

**User-observed evidence:** User forbids publishing before manual acceptance.

**Current handoff assessment:** OPEN intentionally blocked.

**Causal certainty / suspected cause:** Release gate, not product defect.

**What still needs verification:** After explicit approval synchronize exact public installer/manifests/sites/backend/Helper/CI/judge.

**Preserved ledger detail (historical statuses qualified above):**

Ledger status: OPEN
Expected: Accepted SHA, installer, GitHub, updater, canonical/fallback sites, backend, Helper, CI and judge snapshot agree.
Latest evidence: Repair source is unreleased; original 1.4.17 is now the temporary public stable. New release requires explicit manual approval.
Recorded root cause (certainty qualified above): Release intentionally blocked by open acceptance items.
Fix: Publish once after all critical items reach required verification.
Evidence: Final independent hash/size and live endpoint checks pending.
Touches: Release/deployment metadata.
Protect: Ship one candidate only.


## 8. Evidence map: what is actually fixed and what is not

All relative paths below are under the private repair checkout. Evidence in ignored dist/output directories is LOCAL and may not follow a Git clone. The fresh thread should use this machine/workspace, or explicitly transfer sanitized evidence. Do not commit raw profile/auth/account captures or screenshot secrets.

| Evidence directory / source | What it establishes | What it does NOT establish |
|---|---|---|
| dist/verification/human-20260910-rejected-785a48a | Preserved rejected human runtime/history before repairs | Current candidate acceptance |
| dist/verification/human-20260915-8492daa | Latest human screen/backend/model-route correlation | That every subjective audio failure had a single cause |
| dist/verification/repair-5fddef4/provenance.json | Exact installed/package/hash/private CI chain | Dirty P15 is installed |
| repair-5fddef4/p15-full-gates/p15-full.trx | 1,173 desktop tests passed on dirty correction | Real Browser success, backend/Firestore rerun, final release |
| repair-5fddef4/tests/p15-source-focused.trx | 547 Browser-focused source tests passed | Live model or Store renderer end-to-end |
| repair-5fddef4/tests/p15-attribution-before.trx | Five failing extraction tests before repair | Every earlier human loop has that cause |
| repair-5fddef4/nasa-oldest-20260916 | Real Store/Chrome task repeatedly navigated; local cancellation stopped it | Fixed NASA flow (it FAILED) |
| repair-5fddef4/planning-final-sentence | Installed final-text cancellation observed in23ms, stale ReadingPage rejected | Human ASR/all-state cancellation |
| repair-5fddef4/attached-page-final-sentence | Installed executing/attached-page cancellation observed in11ms, conversation survived | All renderer races and all task states physically retested |
| repair-5fddef4/settings-direct-20260916 | Local how-to-use owner, one speaker response, BA OFF, zero reservation | Settings section actually foreground/visible; last3 tab close |
| repair-5fddef4/conversation-after-repair | Startup2.565s, actual speaker answer, zero actions/BA, final-text farewell clean | Microphone recognition/barge-in |
| repair-5fddef4/screen-recovery-20260916 | Real screen result10.12s, speaker output and next conversation, goodbye/cleanup | Fast1–2s response or forced installed timeout |
| repair-5fddef4/screen-second-image-thinking | Real alternative thinking trials were inconsistent/slower/503 | Justification for a production model/config change |
| repair-8492daa/p31-ambiguous-pair | Actual A/B folder open, interposed conversation, exact close, one coherent spoken completion | Every arbitrary reference or “last3tabs” |
| repair-8492daa/p31-observed-mid | Native HWND/path witness including unrelated same-title folder/Chrome survival | Stale HWNDs reusable on next run |
| repair-5d6d101/quiet-rollover-20260914 |642s quiet, same logical context, sequential1.6s recovery, spoken remembered details, clean stop | Human ASR/visible overlay throughout |
| production-folder-trace/current-source-20260914 | Real production model/current dispatcher with simulated effects | Real native actions |
| production-folder-trace earlier source-scheduling traces | Duplicate protocol/scheduling causality, retained failures | One experimental collector safe to ship |
| output/playwright/browser-real-guidance; browser-model-guidance | Real isolated browser/model/Helper form/skip/handoff and unrelated-action routing | Current paid installed production model+mic matrix |
| dist/verification/hardware-audio/long-control/desktop*.json |823s/70sections and412s/35sections interrupted replay, actual single paste/clipboard/UI | Long microphone callback behavior |
| dist/verification/baseline-1.4.17 | Original artifact, actual earlier CTA download, signed-manifest verification | New repaired source is stable |
| docs/regression-differential-1.4.17.md | Detailed causal investigations/protocol references and invalidated harness samples | Every paragraph has been updated to latest HEAD |
| COMPARISON.md | Compact protected decisions | Header/statuses currently entirely fresh |

Native screenshots already inspected:
- `C:\Users\Admin\AppData\Local\Temp\codex-shot-2026-09-16_10-02-51.png`: real Jarvis Speaking above public form after screen result.
- `C:\Users\Admin\AppData\Local\Temp\codex-shot-2026-09-16_17-54-37.png`: real NASA channel/Oldest selected and overlay; supports actual page, not terminal success.
- `C:\Users\Admin\AppData\Local\Temp\codex-shot-2026-09-16_17-49-38.png`: Codex foreground/overlay; **does not prove Settings page navigation**.

These images can include user tab titles. Do not upload arbitrary screenshots to a model or public repo. The normal screen test must first verify only a safe public test page is visible.

### Meaningful fixes with stronger evidence

- Safe native Explorer target closure, full-path referents, balanced input: source/race tests plus installed actual windows and latest human correct-close report. Preserve.
- One coherent A/B open/close speech: actual speaker transcription plus exact effect receipts at8492daa; scheduling change is real protocol correction. Final mixed-human scope remains.
- Proactive auth refresh: installed timed starts/local preflight and latest human under3s; no absolute network guarantee.
- Native topmost/no-activate: HWND style and physical evidence; do not force keyboard focus.
- Durable bare-hostname Link: saved store/visible URL/restart evidence; current conflict concerns execution/knowledge, not destroying persistence.
- Long Live policy/sequential rollover: server tests + actual quiet/context/settlement; retain720/120/900.
- Notification boundary: routine informational path no longer creates OS balloons, useful attention path retained.
- Public stable rollback and actual deployed layout: real artifact/download and responsive bounds; no-plan auth gap remains.
- Recorder arithmetic/drain/Raw recovery, BA renderer cancellation/guidance/default-value/field targeting: strong source and isolated physical evidence; do not promote to final installed microphone/Store proof.
- Screen recovery: one installed successful next-turn sample; speed remains open.
- P15 dirty extraction: source only. Do not mark CLOSED.

## 9. Test tooling, isolation, and known misleading diagnostics

### Existing nonshipping installed harnesses

Read scripts before reuse; hardcoded source hashes are intentional safety checks, not annoyances to bypass:
- `dist/verification/run-installed-conversation.ps1`: Label, Turns, AllowedActions, TurnObservationSeconds, SilenceAfterFirstSeconds, Farewell, ExpectedSource, ExpectedExeHash. Uses existing final-text API, real production provider/speaker loopback, temporarily muted physical input with restoration, finally stop/paid refresh. A fixed observation wait is not proof speech completed.
- `dist/verification/run-installed-browser-cancel.ps1`: label/attached-page/request/ObserveCompletion. Currently expects5fddef4 hash. Up to75s task observation; captures state transitions, finally stops/cleans. Repeated semantic navigation can vary its progress text, so exact-string loop detection alone misses it.
- `dist/verification/hardware-audio/bin/Release/net8.0-windows/HardwareAudio.exe`: bounded quiet-input guard, loopback capture, offline Whisper. Local prerecorded/speaker evidence is not human microphone evidence.
- `dist/verification/screen-provider-probe`: ignored developer-key process, fixed developer endpoint, safe public image, no production auth/billing/Firestore. Retain every rejection/503 in evaluation.

Local API (read current source/origin requirements, do not add a new shipping diagnostic endpoint):
- GET http://localhost:8090/api/settings, /api/browser-automation/status.
- Existing POST /api/jarvis/text-turn, /api/jarvis/stop, /api/paid/refresh, /api/browser-helper/test require the existing local CSRF action token and Origin/Referer.
- Keep tokens in memory. Query only non-secret fields; no full settings dump.
- text-turn exercises downstream interpretation/tool/result/provider/audio, **not ASR**.
- Task state with empty taskId is not an active task.
- A normal account refresh can update local access tokens; compare intended invariants, not a frozen encrypted session-file hash.

### TEST provider boundaries

Previous keys were supplied privately in conversation. **Do not reproduce them in this file or any repo/log/artifact.** The old test profile `%LOCALAPPDATA%\keyboard.wtf-test-p34\settings.json` contains an encrypted local test credential; decrypt only in an explicitly isolated process if necessary. Keys may be exhausted; failures are not permission to switch production routing.
A valid test setup has separate executable/profile/config/hotkeys, visible TEST label, no production auth/session/billing/Firestore, and no native-host identity overwrite. Production must never reach a test provider and TEST must not fall back to paid production. Do not add a permanent hidden BYOK/selector/bypass.
Historical test-key acceptance had a different Live model from production. Reproduce critical behavior on production at bounded cost. Current production capacity exists; reserve/settle normally.
The user authorized safe test-only key failover, not production key rotation. If safe fast isolation is not available, do not redesign production just to save test credits.

### Tool capability reality

CUA/node computer-control runtime previously failed “failed to write kernel assets…system cannot find path…os error3” even after one reset. This is a tool initialization problem, not Windows UAC denial. Do not keep retrying it or bypass platform safety.
Read-only screenshot skill worked. Playwright CLI could bring its own separate public-page browser to front; this profile did NOT have the Store Helper or user auth. It cannot stand in for installed Store acceptance.
Where supported use appropriate native computer-use skill for real input. Do not replace forbidden/unsupported native input with blind SendKeys/PowerShell UIA. Existing app APIs and bounded harnesses are valid only for what they measure.
Agent has previously successfully run the exact installer with normal silent flags and exit0; do not repeatedly ask the user to run .exe unless an actual OS/tool blocker is established. No OS policy bypass, no unnecessary trust/UAC investigation.

### Resource discipline

No paid Live session while reading/writing code. Each test uses bounded start/stop/finally cleanup, stops temporary audio/browser/test processes, restores microphone mute if changed, preserves useful user pages, and verifies current reservation/task state.
Do not globally set input0 indefinitely. User consented to temporary quiet testing; restoration is mandatory. Do not kill normal tray/native-host processes as “duplicates” without inspecting invocation.
Keep normal production hotkeys/Helper/background readiness intact; idle logical conversation policy and user-dismissed session policy differ.
Do not solve historical accounting cleanup with Firestore edits. Use existing reconciliation/verified provider evidence, or leave a precise human/operator blocker.

## 10. What should NOT be touched without new causal evidence

- Public original1.4.17 artifact/signature metadata; private repair checkout and frozen artifact provenance.
- Production Firebase UID/session/PKCE/state/nonce; Razorpay business semantics and ₹999/500-credit math; ledger/idempotency/authorization.
-720s authoritative Live lease,120s cleanup grace,900s Cloud Run, sequential old-socket settlement before replacement.
- Native Explorer path/HWND identity, no explorer.exe termination, safe fixed-set target close.
- Balanced modifier ownership, foreground/cancel checks, renderer command lease and stale-generation rejection.
- Topmost/no-activate overlay; do not repeatedly force foreground.
- Real Store Helper2.1.27/protocol2.1.21 trust/identity. Older user prompts saying preserve2.1.26 are superseded by actual user-confirmed update; don't “fix” resolved Store update.
- Durable Link/workflow/tombstone architecture and bounded personal memory.
- Clipboard selected-text safety, explicit replace/append, protected form submission/reset semantics.
- Nonblocking Live reader and one existing effect/result owner; no second TTS completion narrator.
- Natural persona/audio/VAD/barge-in unless measured fault requires it.
- Normal installed Settings/auth/hotkeys; isolated TEST may never overwrite them.
- Successful deferred context/known-field/group/page-generation work unless changed dependency requires retest.

“Preserve” does not mean blindly accept old PASS. If a new fix touches a protected dependency, explicitly demote/reverify its affected acceptance, not reopen every unrelated subsystem after compaction.

## 11. Recommended work order and exit criteria

The following is a priority queue, not authorization to fix during the handoff run. Resolve one coherent issue before moving on; if expensive/unresolved, record exact progress and safely move to the next conclusive issue, then return.

1. **Resume current P15 dirty verifier review** rather than lose it: tiny proven defect, source tests already pass. Validate publisher boundaries without weakening verification. If coherent, complete relevant gates/private candidate install and exact NASA replay. Never claim source pass closes the real loop. Do not let this short in-progress completion displace critical P40 indefinitely.
2. **P40 screen critical**: actual provider latency/output/request contract plus tool wait/UI recovery. Current20s deadline is recovery, not fast analysis. Use representative public images and follow-up speech/forced failure. No risky model/VAD/billing change from one benchmark.
3. **P14/P34 cancellation, guidance, no resurrection**: exact installed task-state matrix, corrections/skip/unrelated action and terminal follow-up. Existing source owner is correct place, no grammar expansion.
4. **P15 broader BA/P10–13 real web**: NASA stop-at-success; Devpost prize sort/explicit destination; Google controlled form plus Selenium/TestPages/different DOM; same-task handoff and visible terminal truth. No submits. Retain failed traces and avoid expensive loops.
5. **P16 safe multi-tab selection and visible local Settings**: exact intended tab set; no blind repeatedCtrlW. Preserve workflow-tab success. Simple open/search/navigation/local Settings BA OFF.
6. **P21/P38 workflow/mapping coherence**: latest blank episode and saved trigger correction, one Link confirmation, general capabilities; do not rewrite working store. P02 one spoken completion final proof.
7. **P09/P08/P20/P37 actual long capture**: real microphone10min+ where feasible, failure/tail/cancel/clipboard/onepaste/UI. Prerecorded replay is supporting evidence only. P04/P32 natural farewell and quiet-session continuity.
8. **P23 actual deployment remaining no-plan/auth flow, P43 live Store contact**, reuse alreadyvalid responsive/active-plan checks. Do not cosmetic-redesign Pricing.
9. **P41 secondary model/cost, P42 specific payment identity/domain** at clean stage: official current data and representative benchmarks; read-only payment trace, no unauthorizedcharge. Do not stall core repair on speculative upgrades.
10. **P25/P26/P38/P44 final core+longmixed** after coherent freeze, then **P27/P30 one bounded full regression/security/resource audit**, exact final P28 candidate and manual readiness. Public P24 remains1.4.17; P29 publication waits explicitapproval.

Useful final mixed sequence:
startup → normal conversation → interruption → ambiguous folder/openboth → one completion → closeboth → one completion/Chrome untouched/overlaytopmost → learned mapping/workflow → deterministic websearch BA OFF → selected/deferred context → real BA → NeedsUserAction → unrelated direct turn → relevant correction/resume/skip → page handoff/cancel → long Smart/Raw (bounded separate long runs if needed) → Screen Analysis → more conversation/actions → long silence/resumption → clear farewell → restart.
Track audio/transcript/tooldecision/effect/result/speech/target/generation/session and actual page; not only final green state.

### Required acceptance evidence naming

For each closed issue store sourceSHA, installedSHA, inputmethod (humanmic/finaltext/prerecorded), providerroute/model, real vs stubbed effects, relevantbefore/after visible state, result/speech count, resource cleanup and billing state. Preserve failing samples and harness errors distinctly. Do not replace a failure by rerunning until green.
No result may claim FIRST-UTTERANCE microphone success when only API text was supplied. No screenshot of a blank placeholder UI proves actual Settings or browser effect.

## 12. Release mechanics for the next implementation thread

Read repo instructions/skills first. Use explicit workdir for every command:
`C:\Users\Admin\OneDrive\Desktop\keyboard.wtf-paid-staging`.

Expected gates (read current CI rather than copy stale test counts):
- git diff --check and smallest relevant focused/adversarial tests.
- dotnet restore KeyboardWtf.sln --locked-mode; clean Release build.
- dotnet test KeyboardWtf.sln -c Release --no-restore --no-build --filter "Category!=FirestoreIntegration".
- Backend suite separately where not included in solution; actual staging Firestore via scripts/staging/run-firestore-integration.ps1, never reinterpret as in-memory unit tests.
- Dependency vulnerability, scripts/security/scan-secrets.ps1, frontend/Helper node syntax plus test-native-reconnect.js, test-form-context.js, test-bridge-lifecycle.js.
- Private paid-staging CI exact commit; no public push.
- scripts/build-windows-release.ps1 -PaidProductionRelease -SkipSiteHashUpdate.
- scripts/release/test-paid-production-runtime.ps1 -Executable dist/release/keyboard-wtf-win-x64/keyboard.wtf.exe (isolated clean profile; verify registry restoration).
- scripts/release/generate-sbom.ps1 -BuildDirectory dist/release/keyboard-wtf-win-x64 -Version1.4.19 (supply normal PowerShell argument spacing).
- Hash artifact/package/installed bytes and record truthful provenance in new repair-SHA directory; don't trust stale root provenance.
- Agent runs installer if tools/OS permit, then affected exact installed acceptance. If blocked, report exact denial, do not bypass.
- Never delete/move across computed Windows paths without proving resolved target lies inside intended workspace; preserve unrelated dirty files and user profiles.
- No final public release until manualapproval. This run itself must not perform these actions.

Manual readiness response, only when actually warranted:
FINAL HUMAN ACCEPTANCE READY
Version:
Source SHA:
Installer SHA-256:
Installed EXE SHA-256:
Then a short checklist covering remaining human-only speech/barge-in/farewell and visible long-mode behavior. No PUBLIC READY verdict early.

## 13. Limits of this handoff / claims independently refreshed

NOW: privateHEAD/dirtydiff, projectfiles/controlboundaries above,44Pledger,comparison/differential, chronologicalGitlog and99-filediffstat, exact installed/package/installer/originalbaselinehashes, privateCIconclusion, publicGitHublatest, canonical/fallbackstablemanifests, backendlatestreadyrevision/900stimeout, actualmodelrouting, localreserved0/emptytask/Storeconnected and nativehostinvocation were checked.
HISTORICAL retained: actualCTA download, responsive production visuals, authenticatedactiveplan, nativewindows/audio/longreplays and fullFirestore gates. We did not spend credits or repeat physical acceptance for documentation.
NOT established: finalhumanacceptance, completeP40speedfix, newdirtyP15installedsuccess, allBAstates/multi-siteinstalledmatrix, last3tabs, longrealmic, realno-planPricing, specificpaymentUID/domainchain, officialGemini3.8availability/Jan2027prices, finalsecurityaudit. Do not invent those.
No new provider/network/model research was used to assert current public Google lineup; modeltable is actual account routing, not a recommendation or vendoravailabilityclaim.

The user's full attached request was read at:
`C:\Users\Admin\.codex\attachments\27641294-9c19-4e67-acaf-04ca29a7f1cf\pasted-text.txt`.
Its fix instructions are future recovery scope, not permission to override this run's analysis-only request. This packet contains the operative requirements so a new thread need not reread the entire chat.

## 14. NEXT-CODEX EXECUTION PROMPT — copy from here

Continue keyboard.wtf recovery. Read **C:\Users\Admin\OneDrive\Desktop\keyboard.wtf-paid-staging\docs\CODEX_HANDOFF_FULL.md FIRST**, then actual PROBLEMS.md, COMPARISON.md, repository instructions and relevant evidence. Do not restart the old investigation, blindly trust PASS claims, or use the separate default public checkout as the repair worktree.

Independently verify the current repair root, branch, HEAD, dirty diff, installed/package hashes, relevant logs and task/resource state. At handoff:
- HEAD: `5fddef4068b45677d7aa209203d9d22790d94c14`.
- Installed version: 1.4.19.
- Installed EXE SHA-256: `CDADCC46FB51939F74DF4FD503A2AFCF61B25FCBBB2A3C2BFBAE3C3A5BDCEB44`.
- A dirty P15 verifier/test correction exists and is NOT installed.
- That correction passed 1,173 desktop and 547 Browser-focused tests.
Preserve unrelated user work. Do not assume these facts remain current without checking.

Finish remaining problems **ONE BY ONE**:

reproduce the actual failure → correlate transcript/tool/provider/effect/UI logs → prove the root cause → make the smallest generic correction at the existing ownership boundary → focused/adversarial tests → self-review for simplicity and regressions → real installed/physical verification where required → truthful status/evidence → next issue.

Do not call source/tests fixed when physical acceptance is missing. A new human failure automatically reopens older passes. Separate human microphone input, supplied text, prerecorded capture, real provider with stubbed effects, and actual installed Store/Chrome evidence.

Resume the small existing P15 review/installed replay without discarding progress. Then prioritize critical P40 screen latency/recovery, P14 cancellation, P34 guidance/terminal authority, broader real-web BA, safe last-three-tab/local Settings behavior, mapping/workflow coherence, and long actual Smart Writing/Raw/audio acceptance. Use the handoff's priority queue. Park a difficult issue with precise evidence and return after safe conclusive items; do not spend indefinitely on optional cosmetic file-search status.

Do NOT add phrase/site-specific hacks, giant keyword grammars, speculative refactors, prompt bloat, competing owners/queues/state machines, arbitrary retries, or tests merely to inflate counts. Never blindly restore 1.4.17 or blindly retain newer complexity. Preserve proven native-window/input/modifier/foreground/cancellation safety, topmost/no-activate behavior, durable Links/workflows/memory, auth/billing/payments, 720-second Live policy, 120-second grace, 900-second Cloud Run timeout, sequential resumption, Helper trust/protocol and updater integrity unless an OPEN proven cause requires a change.

Use bounded real Jarvis + Store Helper + Chrome acceptance on multiple safe public/demo/controlled sites, not fixtures alone. Do not submit arbitrary forms or cause unwanted external effects. Clear/Reset-and-repeat is allowed only on safe authorized test forms. Cancellation must preempt every active/paused state and remove routing/publication authority. Old task diagnostics may answer a later question but must never act or speak by themselves. Confirm completion against the visible page, publisher/source/freshness evidence and exact writes. Simple open/search/navigation/apps/Settings must remain direct.

Keep production frozen during diagnosis/testing where possible. Ignored developer-key infrastructure may save usage only with separate process/profile/hotkeys/configuration and proven absence of production auth, billing, Firestore, provider and native-identity mutation. No secrets in logs/commits/artifacts; no BYOK feature. Existing developer Live probes use a different model from production, so isolated passes do not prove production integration. Production capacity currently exists; reserve and settle normally. Stop Live/microphone/test-browser resources outside bounded checks, restore microphone mute, and verify tasks/reservations are clean. Do not disable useful tray/Helper/hotkey background readiness.

Update PROBLEMS.md concisely after meaningful verification. Correct stale P15/P28/P30 fields using current evidence. Update COMPARISON.md only for meaningful decisions. Keep actual candidate provenance authoritative. Do not re-audit unrelated verified systems after context compaction.

After known issues are genuinely closed:
1. Perform one complete material regression/security/maintainability audit.
2. Run full desktop/backend/real isolated-staging Firestore/Helper/security gates and private CI.
3. Freeze the exact source SHA and build the exact production installer.
4. Run the installer yourself if tools/OS permit; independently verify installer and installed hashes.
5. Prove no TEST provider/hotkey/key/bypass enters shipping output.
6. Physically verify affected behavior and the final realistic long mixed session.
7. Ask for final manual acceptance only when the exact candidate is genuinely ready.

Do not rerun a flaky failure until green and call it accepted. Ask the user only for genuinely unavoidable human/OS/sign-in/payment blockers. Final physical microphone acceptance cannot be replaced by a text API.

**Keep the original verified 1.4.17 public stable/download/updater/GitHub latest until the user explicitly approves repaired manual acceptance.**
- Original installer SHA-256: `812aaa434a4d6b618764b044a9a5f96b5dc9b85830ce1f205c5b6ad582ded6cd`.
- Original installer size: 84,251,836 bytes.
Do not rebuild or relabel that artifact; do not downgrade private development.

Stop at a truthful **FINAL HUMAN ACCEPTANCE READY**, with version, source SHA, installer SHA-256, installed EXE SHA-256, and a short manual checklist. **DO NOT PUBLISH the repaired version before explicit manual approval.** If the user finds a release-critical defect, reject the candidate, trace that exact failure, fix the generic cause, and repeat the coherent candidate process.
