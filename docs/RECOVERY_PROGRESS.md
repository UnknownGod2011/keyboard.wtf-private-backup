# Recovery progress

- Checkpoint: exact installed/source private `5465ed8ba18dbe552ca13145164a6a3623332531`; installer `B5D007D2...7A896`, installed EXE `D7DE73E2...3E559`. Public 1.4.17 untouched.
- Current problem: P21 learned mappings/workflows installed coherence.
- Proven root cause: P16 Settings had a post-1.4.17 local-result ownership regression; positional `last N tabs` separately lacked a stable target set and fell to one foreground close shortcut.
- Fix made: retain Settings under its authoritative local result owner; resolve the whole rightmost tab set to distinct Store-Helper IDs before mutation, pin window/session, then close only those exact IDs.
- Verification: P16 exact installed production replay visibly foregrounded `#how-to-use` with BA idle, then closed exactly three disposable rightmost localhost tabs while preserving the original ChatGPT tab. Jarvis stopped, reservations zero, mic muted/level 0; current speaker setting was not modified. Focused 318/318; full desktop 1,220/1,220; package and installed EXE match exactly.
- Remaining: P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; bounded Gemini 3.5/3.8 Screen Analysis benchmark; P41/P42; final P25-P30/P44 audit/freeze/install/hash/human acceptance.
