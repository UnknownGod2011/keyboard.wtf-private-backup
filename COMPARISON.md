# 1.4.17 → current stability decisions

Reference: stable 1.4.17 commit `6a2c0fcbc5ade96bc6feb4717ab284e83b874d04` (source-equivalent parent `63beae98...`). Current candidate is the production-only working tree based on installed checkpoint `d278766...`; release metadata records the exact frozen SHA. 1.4.17 is a behavior reference, not a restore target.

| Area | 1.4.17 | Current | Decision | Evidence |
|---|---|---|---|---|
| Startup path/timing | Usually simple/consistent ~3–4s | Same basic path; observed 2.505s and 6.979s depending mainly on auth/provider latency | FIX CURRENT | P01 logs; remove only proven local serialization |
| Live prompt/personality | Natural Jarvis persona | Material persona config appears preserved; response variance remains possible | VERIFY ONLY | Source diff/config comparison and final conversation smoke |
| Farewell path | Reliable local-feeling termination | Local transcript interception plus guarded no-transcript end-tool fallback; session-owned cleanup is silent | KEEP CURRENT | P04 ordering and rebuilt-TEST shutdown prove one terminal owner |
| Live/tool-result dispatch | Socket handling was simple and sequential | Tool work is off the socket reader, preserving cancellation/control responsiveness | KEEP CURRENT | `655107f`, `7824b19`; focused race tests |
| Exactly-once completion | One coherent response was the perceived norm | Same-frame calls are batched; canonical duplicate retries are acknowledged silently | KEEP CURRENT | P02 human timeline and focused dispatcher tests |
| Cancellation ownership | Simple conversation stop | One token now reaches Browser/native/input/result boundaries | KEEP CURRENT | P08/P14 tests; final physical Browser matrix pending |
| Direct-action routing | Direct actions generally stayed direct | Adds learned mappings, explicit opt-out, Settings target preservation and recent-target receipts | KEEP CURRENT | P02/P16/P21/P31 |
| Keyboard injection | Less defensive but behavior felt stable | Balanced batches, held-modifier refusal, target checks and cancellation are safer | KEEP CURRENT | P07 adversarial tests |
| Foreground/window ownership | Fewer automation paths | Deterministic target HWND/generation ownership prevents wrong-window mutation | KEEP CURRENT | P06/P07 physical and race evidence |
| Explorer close | No current native multi-window guarantee | Fixed Explorer HWND set, identity validation, native close and verification | KEEP CURRENT | P06 human/physical acceptance |
| Jarvis popup/topmost | Expected to remain above apps | Every overlay HWND carries topmost and no-activate styles | KEEP CURRENT | P03 native-style test; installed multi-app acceptance pending |
| Microphone processing | Stable perceived capture | Same capture path plus overflow-safe full-scale metering | KEEP CURRENT | P09 regression tests |
| Selected-context ownership | Simple conversational selection use | Fresh context, deferral and stale replacement are explicit and human-proven | KEEP CURRENT | P10 human form acceptance |
| Browser Automation lifecycle | Useful-page/stale-plan protections introduced | Adds single task/context/intervention ownership, accepted-guidance safety context, bounded terminal diagnostics, first-navigation readiness, and visible state checks | KEEP CURRENT | P10–P15/P34 real Helper/Chrome evidence; final installed smoke pending |
| Browser cancellation | Less capable paused-state control | Local cancel reaches every task layer and stale work is rejected | KEEP CURRENT | P14 source/race evidence |
| Browser page/tab adoption | Useful page preserved; manual handoff limited | Explicit same-task current-tab adoption advances generation | KEEP CURRENT | P11 human acceptance |
| Smart Writing transport | Normal workloads reliable | No Smart Writing app cap; long audio is segmented and captured speech is recovered on provider failure | KEEP CURRENT | P20 real 605s TEST pass and forced-failure recovery; installed microphone check pending |
| Learned mappings/workflows | Existing durable workflow store | Trigger-first links and selected hostnames canonicalize into the same durable Link architecture | KEEP CURRENT | P21 source lifecycle tests; final installed acceptance pending |
| Live reservation/session policy | Client behavior good; short server lease could reclaim healthy work | 720s lease + 120s grace, sequential resumption, revision-owned reconnect UI, exactly-once settlement | KEEP CURRENT | P22/P32 backend and real rollover evidence |
| Billing/accounting | Existing prices/formulas | Same semantics; clearer available/reserved display and long-Live ownership | KEEP CURRENT | Backend/Firestore tests; P30 final reconcile |
| Helper/native messaging | Helper 2.1.25/protocol 2.1.21 | Helper 2.1.26 preserves protocol and adds form group/required metadata | KEEP CURRENT | Store install/handshake and form acceptance |
| Auth | Existing production account flow | PKCE/state/nonce and account isolation preserved | VERIFY ONLY | Auth gates plus final Pricing hydration |
| Updater | Signed metadata/hash verification | Architecture unchanged; release withheld on 1.4.18 metadata | KEEP CURRENT | P24/P28/P29 |
| Website behavior | Stable public 1.4.17 layout | Compact Pricing/FAQ/account-aware preview has exact responsive alignment and no DOM overflow; public site still 1.4.18 | KEEP CURRENT | P23 Playwright DOM/auth matrix; P24 deployment pending |
