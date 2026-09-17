# Recovery progress

- Checkpoint: installed private `ce707f4eb9f3bde9d45bc12f089066a76523f4c0`; installer `AFC20E475A7F43D9530566BD3A15E76C59C79F15E18D8847C4BA929F15771A5A`; installed/package EXE `796C30578F9DD32EE4E30C8D11693F88B23AF8DBD424607532B3178BBDD4A25E`. Public 1.4.17 untouched.
- Current problem: P10 high-priority direct selected-text transformation/clipboard regression.
- Proven root cause: pending. Manual installed result copied a paraphrase of the instruction instead of transforming the actual selection; capture/freshness/payload/tool/clipboard ownership and 1.4.17 differential must be traced.
- Fix made: none yet. Preserve the existing Browser selected-source identity guard.
- Verification: P15 is physically verified at `repair-ce707f4/p15-devpost-terminal-final`; correct live `$740,000` card, `…-2026` destination, Completed, stable follow-up, audible success, input restored, reserved zero; 1,194 desktop tests pass.
- Remaining: selected-context blocker; P34 installed matrix; broader P10-P16 web/direct behavior; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; bounded Gemini 3.5/3.8 Screen Analysis benchmark; P41/P42; final P25-P30/P44 audit/freeze/install/hash/human acceptance.
