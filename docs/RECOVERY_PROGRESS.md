# Recovery progress

- Checkpoint: installed private `ce707f4eb9f3bde9d45bc12f089066a76523f4c0`; source `fabc3d466fbe4e82ed2caebe3006a1c90212f598` plus dirty P10 correction. Public 1.4.17 untouched.
- Current problem: P10 high-priority direct selected-text transformation/clipboard regression.
- Proven root cause: installed log captured a fresh 1,836-character selection, then Gemini supplied an instruction paraphrase as `copy_text.text`; current and 1.4.17 both trusted that unvalidated model argument. Capture, clipboard restore, and Browser source identity were not the failing owners.
- Fix made: same-turn selected-context copy guard leaves clipboard unchanged on an instruction echo; Live/tool contracts label selection as source content and require only the derived final result. No production auth/billing/Helper/model change.
- Verification: failing manual trace preserved; local guard tests and 342 focused tests pass; full desktop suite 1,199/1,199; isolated developer-provider transform and analysis-only probes pass with production settings hash unchanged. Private build/install and physical selection matrix pending.
- Remaining: selected-context blocker; P34 installed matrix; broader P10-P16 web/direct behavior; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; bounded Gemini 3.5/3.8 Screen Analysis benchmark; P41/P42; final P25-P30/P44 audit/freeze/install/hash/human acceptance.
