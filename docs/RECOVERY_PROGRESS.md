# Recovery progress

- Checkpoint: exact installed/source private `78131adf3ebe0e1feff3f878cf631907c2765318`; installer `D9F2DDC4...B928B`, installed EXE `7FEB900B...B66F4`. Public 1.4.17 untouched.
- Current problem: P16 Direct Settings and Browser opt-out installed acceptance.
- Proven root cause: P13's post-1.4.17 verifier suppression treated any nonempty live field as the requested missing answer; punctuation-normalized optional identity was a second narrow matching bug.
- Fix made: only an explicit expected value proved on the live field suppresses an intervention; optional labels normalize trailing punctuation while preserving unique-field and protected-step checks.
- Verification: P13 exact installed production task `61d712aa...` asked despite pre-seeded `Existing placeholder`, accepted natural `P13-RESUME-78131AD` on the same task, replaced only that field, completed, and did not submit. P34 installed evidence covers skip/isolation. Jarvis stopped, reservations zero, mic muted/level 0; existing focused 342/342 plus full 1,210/1,210 gate.
- Remaining: P16 web/direct acceptance; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; bounded Gemini 3.5/3.8 Screen Analysis benchmark; P41/P42; final P25-P30/P44 audit/freeze/install/hash/human acceptance.
