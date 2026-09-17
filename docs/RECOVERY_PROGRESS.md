# Recovery progress

- Checkpoint: installed/source private `d4cbd111ba0144abb87ea2d3483e40372f649f7d`; installer `D6900A1C...C556`, installed EXE `45B239B7...E4AD5`. Public 1.4.17 untouched.
- Current problem: P11 Browser page/task ownership installed acceptance.
- Proven root cause: P34 used active-task heuristics after semantic tool selection, allowing broad overlap to steal unrelated direct-browser turns.
- Fix made: only Browser start/control tools may enter task guidance; explicit cancellation and read-only terminal diagnostics remain authoritative.
- Verification: P34 exact installed `d4cbd11`/production Store Helper task `5ddfd01c...` stayed paused during one unrelated `open_url`, then accepted a skippable same-task instruction and completed with zero fixture writes/submission. Jarvis stopped; reservations zero. Existing focused gate 355/355; full desktop 1,207/1,207.
- Remaining: P11 then P13/P16 web/direct acceptance; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; bounded Gemini 3.5/3.8 Screen Analysis benchmark; P41/P42; final P25-P30/P44 audit/freeze/install/hash/human acceptance.
