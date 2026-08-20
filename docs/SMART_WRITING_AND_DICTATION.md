# Smart Writing and Raw Dictation

This document describes the Windows 1.4.3 production implementation. It is based on the code paths that ship in the desktop build, not on planned behavior.

## The three interaction modes

| Mode | Default shortcut | Speech recognition | Paid AI | Delivery |
|---|---|---|---|---|
| Smart Writing | `Ctrl+Alt+K` | Local Vosk fast path | Yes: authenticated keyboard.wtf Cloud AI | Active field, clipboard, or ask each time |
| Raw Dictation | `Ctrl+Alt+D` | Local Vosk fast path | No | Active field, clipboard, or ask each time |
| Jarvis | `Ctrl+Alt+Q` | Direct Gemini Live audio after hotkey activation | Yes, immediately after the user invokes Jarvis | Conversational response and supported actions |

The displayed shortcut is the shortcut Windows actually registered. If a shortcut is unavailable, Settings reports it as unavailable and asks the user to choose another; the public build does not silently fall back to F13-F24.

## Shared recording pipeline

`HotkeyService` dispatches Smart Writing and Dictation to `CommandRegistry`, which delegates recording to `VoiceCaptureService`. `AudioRecorderService` captures 16 kHz mono PCM through NAudio/WinMM. A configured microphone index is honored; `-1` uses WinMM `WAVE_MAPPER`, so the current Windows default input is followed rather than assuming device zero.

The shortcut toggles recording. Silence detection or the maximum recording duration can also stop capture. The visible states are Listening, Transcribing, Polishing (Smart Writing only), Done, Cancelled, and Error. A microphone or Windows privacy failure is surfaced as an error and is never reported as success.

Jarvis does not run a Vosk transcription or local speech gate before Live starts. Pressing the dedicated Jarvis shortcut immediately creates one logical conversation, opens the microphone, and begins the authenticated Gemini Live connection. A short PCM pre-roll prevents first-word clipping while that connection is established. Recoverable provider or Cloud Run socket rollovers replace only the physical transport: the microphone, bounded in-memory context, and single user-visible Jarvis surface remain part of the same logical session until the user stops it.

Only one recording/transcription operation is allowed at a time. A second press of the same shortcut stops the current recording. A competing voice mode cannot open a second microphone capture or overlap an active Jarvis session. `Cancel Anything` stops recording, cancels local recognition and the cancellable cloud request, prevents late paste, and returns the voice state to idle/cancelled.

## Local speech engines and first-run provisioning

The desktop packages the managed Vosk and Whisper runtimes plus the native Whisper libraries. Speech model data is stored per user at:

```text
%LOCALAPPDATA%\keyboard.wtf\models
```

Models are not read from a repository or developer-specific path.

On first run the app downloads the small English Vosk model (about 40 MB) from the official Vosk model host, validates its minimum archive size and required directory structure, then loads it immediately. Smart Writing and Dictation can work as soon as Vosk is ready while the larger Whisper model downloads in the background. The Whisper Base/Small download is obtained from the upstream whisper.cpp model repository and validated against the upstream published SHA-1 before installation. Progress is shown in the existing model status UI. Partial downloads are removed and a later launch can retry; completed models are cached and are not downloaded every launch.

Vosk is the fast path used by Smart Writing and Raw Dictation. Whisper remains available for accuracy-oriented speech features/settings. No Python, .NET SDK, Visual Studio, FFmpeg, environment variable, or manual model path is required on a normal customer installation. Internet access is needed only for first-time model provisioning and paid/cloud features.

## Raw Dictation

Raw Dictation records audio, transcribes it locally with Vosk, applies only configured local transforms (the conservative filler-word filter and case transform), and delivers the transcript. It deliberately ignores the AI translation setting so it cannot accidentally create a Gemini/Vertex request.

Raw Dictation:

- does not require Google sign-in or a paid entitlement;
- does not call the keyboard.wtf backend, Gemini, or Vertex AI;
- does not reserve or deduct paid usage;
- remains available when a cloud session is signed out or revoked, provided a local speech model and microphone are available.

## Smart Writing

Smart Writing uses the same local speech capture/transcription first. It then sends the transcript and the fixed polishing instruction through `PaidCloudAiProvider` to `PaidAccountService.GenerateAsync` with task kind `desktop-reformat`.

The paid request path is:

```text
verified desktop session
  -> production /v1/ai/generate
  -> server entitlement and allowance check
  -> bounded reservation
  -> server-selected Vertex model
  -> provider usage metadata
  -> idempotent settlement
  -> polished text
```

The desktop does not receive a provider credential and does not decide its own UID, model price, token count, balance, or ledger entry. Smart Writing cancellation is passed to the authenticated HTTP request. If the account is signed out, revoked, has no usable entitlement/allowance, or the paid request fails, the production build reports the failure and inserts nothing; it does not misleadingly type the raw transcript as though Smart Writing succeeded.

While the user records, Smart Writing sends a no-cost `/v1/ready` warm-up to the authenticated backend. That warm-up cannot reserve allowance or invoke Vertex, and a warm-up failure never prevents local recording. Safe stage timings (recording, transcription, authorized cloud request, polishing, and insertion) are logged without recording the transcript or generated text. The paid polishing stage has a bounded timeout and every path ends in exactly one terminal success, clipboard fallback, failure, cancellation, or timeout state; a late response cannot paste after cancellation.

The transformation instruction removes conversational filler and false starts where appropriate, honors explicit self-corrections, improves grammar/punctuation, and preserves meaning and tone. The returned text is the only text delivered.

## Text delivery, clipboard, and focus safety

The default delivery mode is `TypeIntoActiveApp`; users may choose clipboard-only or ask-each-time instead.

At recording start, keyboard.wtf captures the intended foreground window, process, and title. Before pasting it verifies that target again, waits briefly, then verifies immediately before `Ctrl+V`. If focus changed to another app/window/tab, it does not type there: the result is copied to the clipboard and the user is told why.

For verified paste delivery, keyboard.wtf snapshots the existing clipboard (including an empty clipboard), temporarily places the result on it, pastes, and restores the previous clipboard after the target had time to consume the paste. Cancellation before paste restores the clipboard and produces no late text.

This protection is intentionally conservative. It reliably detects application/window changes and ordinary Chrome tab-title changes. Windows does not provide a universal stable identity for every custom editor caret (notably canvas-based web editors), so a cursor move to another field inside the exact same unchanged window may not always be distinguishable. Clipboard-only mode is the safest fallback for such editors.

Google Docs and some custom editors may intercept synthetic paste or expose the editor through a nonstandard accessibility surface. The app never claims insertion succeeded unless the paste shortcut was sent to the verified target; if delivery cannot be confirmed at the desktop layer, it keeps/copies the result for manual paste.

## Selected text

The Smart Writing hotkey is a speak-to-write flow; it does not replace a highlighted selection as a separate feature. Selected-text explanation, rewrite, summary, translation, and reply behavior belongs to the Jarvis/selected-context pipeline. This separation prevents a Smart Writing recording from silently capturing or replacing unrelated selected content.

## Relevant implementation paths

- `src/Services/HotkeyService.cs`
- `src/Services/CommandRegistry.cs`
- `src/Services/VoiceCaptureService.cs`
- `src/Services/AudioRecorderService.cs`
- `src/Services/SpeechRecognitionService.cs`
- `src/Services/ModelManager.cs`
- `src/Services/Ai/PaidCloudAiProvider.cs`
- `src/Services/PaidStaging/PaidAccountService.cs`
- `src/Destinations/TypeOutDestination.cs`
- `src/Helpers/ClipboardHelper.cs`
- `src/Services/TextInsertionTarget.cs`

## Test boundary

Automated tests cover routing, model/microphone validation, local-dictation no-AI policy, cancellation propagation, focus-change blocking, clipboard restoration, revoked-session provider blocking, and hotkey registration. Real microphone quality, Bluetooth hardware, Windows privacy toggles, and editor-specific insertion still require the packaged-app hardware matrix; those results must be reported separately and must not be represented by unit tests.
