# Recovery progress

- Checkpoint: installed/source private `c52b9df55830ec1d169d032b944250a228b8b50d` plus dirty P02/P10 protocol correction. Public 1.4.17 untouched.
- Current problem: P02/P10 Live result ownership and selected-text transformation/clipboard regression.
- Proven root cause: post-1.4.17 `5d6d101` attached async WHEN_IDLE/SILENT scheduling to default-blocking Live declarations. Installed `c52b9df` copied one correct fresh-selection result six times; the same ordering explains the earlier instruction payload escaping before capture owned the continuation. Unvalidated output ownership and the `7a2a21e` deferred Browser cache were secondary hazards.
- Fix made: dirty source restores plain blocking Live function responses; selected `copy_text`/`replace_selected_text` still require same-turn selection proof and reject instruction echoes. Dispatcher serialization/cancellation, deferred Browser ownership, auth/billing/Helper/model remain unchanged.
- Verification: selected/clipboard and adjacent blast 320/320; focused corrected protocol/routing 58/58; full desktop 1,206/1,206. Isolated plain-response provider emitted one capture/one correct copy. Rebuilt installed P02/P10 replay pending.
- Remaining: selected-context blocker; P34 installed matrix; broader P10-P16 web/direct behavior; P21/P38; long real microphone P04/P08/P09/P20/P32/P37; P23/P43; bounded Gemini 3.5/3.8 Screen Analysis benchmark; P41/P42; final P25-P30/P44 audit/freeze/install/hash/human acceptance.
