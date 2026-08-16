# keyboard.wtf

**Stop typing. Say it.**

keyboard.wtf is a standalone Windows voice desktop app. Global shortcuts separate raw dictation, AI-polished writing, and Jarvis mode for spoken conversation plus safe computer actions.

## Why keyboard.wtf?

keyboard.wtf started from the question: why are we still typing, copying, and formatting manually when voice and AI can do it faster? Press a shortcut, say what you want, and the app either types your words, cleans them into ready-to-send writing, or lets Jarvis chat and take supported safe actions.

The keyboard becomes the trigger, not the main input method.

## Features

- Distinct global Windows hotkeys for raw dictation, smart writing, Jarvis mode, cancel, and settings.
- Automatic pause detection in every recording mode, plus same-key manual submit and a universal cancel shortcut.
- A top-center voice orb shows real listening level, transcription, Gemini thinking, execution, speaking, completion, cancellation, and errors without stealing focus.
- Offline speech recognition with Vosk and Whisper model support.
- Fast Vosk path for interactive hotkeys, with Whisper available for accuracy-focused transcription.
- Live Vosk partial transcripts appear in the voice orb while the microphone is active; final delivery still uses the completed local transcript.
- Flow-like smart writing that removes fillers and false starts, honors corrections, adds punctuation, and types polished text into the active app.
- Jarvis mode combines Gemini Live conversation with allowlisted desktop, window, browser, file, clipboard, productivity, workflow, and system tools.
- Start with Windows is enabled for new installs. The tray app, hotkeys, local settings, memory, models, and background services reload after sign-in, with a single-instance guard.
- Choose Ask before routine actions or Auto-execute routine actions in Settings. Ask confirms actions; Auto-execute runs explicit app, browser, file, camera, screen, recording, and routine requests without a second internal prompt. Windows permissions are never bypassed.
- Gemini Live bidirectional voice conversation with server-side voice activity detection, interruption support, and auto-end phrases.
- Selectable Gemini Live voices in Settings. Jarvis speech comes directly from Gemini; offline Read Back remains on Piper with Windows SAPI fallback.
- Local intent memory with a 20-entry cap, saved only when the user explicitly asks, and manageable from settings.
- Custom assistant name, voice, and four tone options in settings.
- Resolve approximate or misspelled app names from Windows Start Apps, Start Menu shortcuts, registered App Paths, and trusted application locations. Low-confidence matches are presented for selection instead of guessed.
- Fuzzy-search safe files and folders with breadth-first coverage across Desktop, Downloads, Documents, Pictures, Videos, Music, OneDrive, screenshot/camera/recording folders, and user-approved learned folders. Search remains bounded by depth, result count, scan count, timeout, and cancellation.
- Save user-approved local aliases for apps, files, folders, links, workflows, and browser preference. Learned mappings are bounded to 80 entries and can be removed in Settings.
- Open links and web searches in Chrome, Edge, Brave, Firefox, Opera, Vivaldi, or Arc when that installed browser is explicitly requested. Jarvis does not silently substitute a different browser.
- Inspect one user-approved screen capture in memory with Gemini vision and return text guidance. The capture is not written to disk and Jarvis does not click or type automatically.
- Create, switch, and close Windows virtual desktops through allowlisted Windows shortcuts. Closing a desktop always requires confirmation.
- Start, stop, or toggle Xbox Game Bar recording with `Win+Alt+R`, open Game Bar, or open the Windows screen-region recorder. keyboard.wtf reports that the shortcut was sent because Windows owns the actual recording state.
- Open common apps, switch/minimize/maximize windows, and open known folders or safe local documents.
- Control active Chrome, Edge, Firefox, Brave, Opera, Vivaldi, or Arc tabs: new, close, switch, reopen, refresh, back, forward, downloads, history, and find.
- Use active-window, selected-text, and clipboard context for requests such as "summarize this," "explain this," or "make this professional."
- Search file and folder names, save notes, manage a local to-do list, and set in-app timers.
- Create reusable workflows from app names, safe URLs, and folders. A single "whenever I say..." command now saves the trigger and routine atomically, and fuzzy variants such as "initialize my set" can resolve "initialize my setup."
- Change volume or mute directly, and open Wi-Fi, Bluetooth, display, and sound settings.
- Open Spotify or YouTube searches, Spotify Liked Songs, YouTube Liked Videos/Subscriptions/History, Amazon product searches, and Discord home.
- Open Windows Camera, capture one permission-gated webcam photo under `Pictures\keyboard.wtf\Camera`, or save a desktop screenshot under `Pictures\keyboard.wtf\Screenshots`.
- Read battery, charging, Windows, machine, user, and uptime status.
- Sensitive actions use a two-turn local confirmation gate. Shutdown, restart, sleep, lock screen, and disable Wi-Fi are always gated; normal app closing is also gated in Ask mode.
- Local action history is capped at 50 entries, and Settings includes an emergency Stop Jarvis button.
- Gemini, Claude, and OpenAI providers implemented; DeepSeek and Perplexity are present as provider placeholders.
- Destinations: clipboard, type out, email, Slack, Discord, Teams, calendar, WhatsApp, Notion, Telegram.
- Gmail drafts are opened prefilled for review; keyboard.wtf never clicks Send.
- Voice notes saved to disk.
- Read back through Piper TTS or Windows SAPI fallback.
- Local browser settings UI.
- DPAPI-encrypted API keys and webhook URLs.
- Tray app menu for common actions.

## Default Hotkeys

| Hotkey | Action |
| --- | --- |
| `Ctrl+Alt+K` | Smart writing: clean spoken thoughts and type polished text |
| `Ctrl+Alt+D` | Dictation: type the recognized words without AI rewriting |
| `Ctrl+Alt+Q` | Jarvis mode: chat, open allowed apps, prepare drafts, and run supported safe actions |
| `Ctrl+Alt+X` | Cancel listening, transcription, Gemini work, or conversation |
| `Ctrl+Alt+,` | Open settings |

Recording modes stop automatically after speech followed by a short pause. Press the same mode shortcut again to stop and submit manually. Press `Ctrl+Alt+X` to discard the current operation.

If Windows reports that a shortcut is already in use, keyboard.wtf keeps running and shows a warning so the shortcut can be changed in settings.
If another app owns the configured Jarvis or Cancel shortcut, keyboard.wtf uses `Ctrl+Alt+J` for Jarvis and `Ctrl+Alt+Escape` for Cancel as conflict-only fallbacks. Settings shows which shortcuts are active. `Ctrl+Alt+Space` is not used.

## How It Works

1. A mode-specific global hotkey starts listening and shows the voice orb.
2. NAudio records microphone audio and reports live level to the orb.
3. Pause detection or a second hotkey press ends the turn.
4. Vosk transcribes interactive modes locally for low latency.
5. Dictation types the transcript directly; Smart Writing asks Gemini to clean it first.
6. Jarvis mode streams 16 kHz microphone audio, plays 24 kHz spoken replies, and calls allowlisted tools when the user asks for action.

## Voice

Jarvis conversation audio is native Gemini Live speech-to-speech. The default voice is `Kore`, and Settings offers 12 Gemini voices. A voice change applies to the next Jarvis conversation.

Read Back is separate: it uses local Piper voices and falls back to Windows SAPI. ElevenLabs is not required for the live assistant. Adding it in the middle of Gemini Live would add another network round trip and weaken interruption/tool-call continuity, so it is better reserved for a future optional narration or premium Read Back voice.

## Jarvis Safety Boundaries

- Jarvis never sends an email or WhatsApp message automatically. It prepares content for review.
- Auto-execute removes repetitive prompts for explicit routine and privacy actions while keeping visible status. Closing a virtual desktop, lock, sleep, restart, shutdown, and disabling Wi-Fi still require a fresh confirmation.
- Screen guidance is one-shot and user-triggered. The approved image is analyzed in memory and is not saved; there is no continuous screen watching.
- Camera capture is one-shot and user-triggered. Windows desktop camera access remains authoritative; keyboard.wtf does not alter privacy settings.
- Generic path opening blocks executables, scripts, installers, shortcuts, and registry files.
- Browser tab commands run only while a supported browser is the foreground app.
- Selected-text tools restore the previous clipboard after use.
- Basic text entry and navigation do not expose a generic Enter/submit action.
- Full webpage DOM reading, reliable contact lookup, and account-specific form automation require a future browser extension or accessibility integration. Jarvis states this limitation instead of claiming an action succeeded.
- Spotify search/deep-linking works without extra credentials. Starting a specific track programmatically requires Spotify OAuth and an eligible Spotify playback device, so keyboard.wtf does not claim a song started.
- Amazon cart changes, bulk YouTube unlike actions, and Discord personal-account messaging are intentionally not automated. They require authenticated site integrations, high-confidence target selection, and explicit confirmation.

## Setup

For normal users:

1. Download the Windows installer from `https://keyboardwtf.com`.
2. Run `keyboard-wtf-setup.exe`.
3. Launch `keyboard.wtf` if the installer did not start it automatically.
4. Open settings from the tray icon or press `Ctrl+Alt+,`.
5. Allow Windows microphone/camera access when needed, confirm active shortcuts, and choose the Jarvis permission mode.

macOS is coming soon.

Developer prerequisites:

- Windows
- .NET 8 SDK
- Microphone
- Optional API key for AI formatting. Gemini is the default provider for the hackathon demo.

Build and run:

```powershell
dotnet restore
dotnet build .\KeyboardWtf.sln
dotnet run --project .\src\KeyboardWtf.csproj
```

The app starts in the Windows tray as `keyboard.wtf`. Double-click the tray icon or press `Ctrl+Alt+,` to open settings.

Optional Gemini demo setup without committing secrets:

```powershell
[Environment]::SetEnvironmentVariable("KEYBOARD_WTF_GEMINI_API_KEY", "your-key-here", "User")
```

On next launch, keyboard.wtf imports that key into encrypted local settings and selects Gemini.

## Models

Speech recognition is local-first:

- Vosk is lightweight and fast.
- Whisper is more accurate but larger.
- Models are downloaded into `%LOCALAPPDATA%\keyboard.wtf\models`.
- Missing models do not crash the app; the app reports model status and falls back when possible.

## Privacy

- Offline dictation works without cloud AI.
- API keys and webhook URLs are encrypted at rest with Windows DPAPI for the current user.
- Secrets are masked in the settings UI.
- AI formatting is optional and can be disabled.

## Destinations

Raw destinations receive the transcript directly:

- Clipboard
- Type Out

AI destinations can use destination-specific prompts:

- Email
- Slack
- Discord
- Teams
- Calendar
- WhatsApp
- Notion
- Telegram

If typing into the active app fails, use Clipboard mode as the reliable fallback.

## Windows Download

Current Windows release:

- Canonical landing page: `https://keyboardwtf.com`
- Legacy/fallback landing page: `https://keyboard-wtf.vercel.app`
- Installer: `site/downloads/keyboard-wtf-setup.exe`
- Installed location: `%LOCALAPPDATA%\Programs\keyboard.wtf`

Build the self-contained Windows installer:

```powershell
.\scripts\build-windows-release.ps1
```

The installer is per-user, registers keyboard.wtf to start after sign-in, and launches
the local setup page when a Gemini API key has not been configured.

## License

keyboard.wtf is released under the MIT License. Copyright (c) 2026 Tanush Shah.
See [LICENSE](LICENSE).

## Demo Script

1. Launch keyboard.wtf.
2. Open Notepad, click inside it, press `Ctrl+Alt+D`, and say: "Keyboard dot w t f turns speech into action." Pause and show the literal dictation appear.
3. Press `Ctrl+Alt+K` and say: "Um, I finished the prototype, actually the full demo, and I will send it soon." Show the filler-free corrected sentence appear.
4. Press `Ctrl+Alt+Q` and say: "Open Notepad." Show the app opening, then ask Jarvis to minimize and restore it.
5. With Chrome or Edge active, say: "Open a new tab," then "close this tab."
6. Say: "Send a professional message saying hi, I am sorry for being late for the meeting, I was stuck in traffic, to unknowngod2024@gmail.com."
7. Show Gmail opened with the recipient, `Apology for Being Late` subject, and polished body already filled. Explain that the user reviews and clicks Send.
8. Say: "Remember that I prefer concise replies," then show the bounded local memory in Settings.
9. Say: "Whenever I say initialize my setup, open Chrome, VS Code, and my project folder." Then say: "Initialize my set."
10. In Ask mode, ask Jarvis to close Notepad. Show the confirmation preview, say "confirm," and show the action history.
11. Say: "Open my liked videos on YouTube," then: "Play Whistle by Flo Rida on Spotify." Show the exact Spotify search and select the result manually.
12. Say: "Take a photo," and show the saved JPEG in `Pictures\keyboard.wtf\Camera`.
13. Say: "Start recording," and explain that Windows Game Bar controls the actual recording state.
14. Say: "Take a screenshot," and show the saved PNG in Pictures.
15. Say "bye" to auto-end, or press `Ctrl+Alt+X` at any point for an emergency stop.

## Test It On This PC

1. Run `%LOCALAPPDATA%\Programs\keyboard.wtf\keyboard.wtf.exe`.
2. Press `Ctrl+Alt+,` and confirm Whisper and Vosk show as loaded, Gemini shows configured, and the Realtek microphone is selected.
3. Open Notepad and click in the document.
4. Press `Ctrl+Alt+D`, speak one sentence, and pause. The orb should move through Listening, Transcribing, and Done, then the literal text should appear.
5. Press `Ctrl+Alt+K`, speak with a filler or self-correction, and confirm the polished result is typed.
6. In Settings -> Assistant, change the Gemini Live voice. Start a new Jarvis session and confirm the new voice is used.
7. Press `Ctrl+Alt+Q`, say "Open Notepad," and confirm a new Notepad window opens.
8. With a supported browser active, say "open a new tab," "next tab," and "close this tab."
9. Ask "what app am I using?" or select text and say "summarize this."
10. Use the Gmail draft sentence from the demo and confirm the compose fields are filled but not sent.
11. Ask Jarvis to remember one short preference, then confirm it appears under Settings -> Assistant -> Intent memory.
12. Create a workflow in Settings, run it by name, and confirm it appears in local action history.
13. In Ask mode, ask Jarvis to close Notepad. It must refuse to act until you answer with a fresh "confirm." In Auto-execute mode, normal window closing is immediate.
14. Enable Start with Windows and confirm Settings reports the startup registration as active.
15. Switch Jarvis action permissions between Ask and Auto-execute. Ask must confirm actions; Auto-execute must run explicit camera, screen, screenshot, recording, app, file, and routine actions directly. Virtual-desktop closing and destructive system actions remain gated.
16. Use the Settings camera and screen-guidance diagnostics and verify the saved photo exists while screen guidance creates no screenshot file.
17. Save "initialize my setup," run the fuzzy phrase "initialize my set," and confirm the routine appears in Settings.
18. Ask for YouTube Liked Videos, Spotify Liked Songs, and an Amazon product search.
19. Start any mode and press `Ctrl+Alt+X`; the orb should say Cancelled and disappear after about three seconds.

If typing into an elevated app is blocked by Windows, keyboard.wtf copies the result to the clipboard and tells you it used the fallback.
