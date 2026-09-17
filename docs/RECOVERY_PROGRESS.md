# Recovery progress

- Checkpoint: installed/source private `d4cbd111ba0144abb87ea2d3483e40372f649f7d`; installer `D6900A1C...C556`, installed EXE `45B239B7...E4AD5`. Public 1.4.17 untouched.
- Current problem: P34 final installed active-task guidance/direct-action matrix.
- Proven root cause: P02/P10 closed: invalid async scheduling on blocking Live responses plus cross-turn reuse of the 90-second same-window selection fallback.
- Fix made: plain blocking responses; same-turn selected-result/echo guards; new-turn invalidation of the local selection fallback. Auth/billing/Helper/model unchanged.
- Verification: adjacent 234/234; full desktop 1,207/1,207. Exact install preserved user/Helper hashes. Two distinct selected-copy results executed once; fresh 111-character selection replaced stale 120; analysis-only used current content without mutation; clipboard restored; account reserved zero.
- Remaining: P34 installed matrix; broader P11-P16 web/direct behavior; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; bounded Gemini 3.5/3.8 Screen Analysis benchmark; P41/P42; final P25-P30/P44 audit/freeze/install/hash/human acceptance.
