# Local Windows Agent Testing

This branch is local-only. Do not push, tag, deploy, or update the public installer before user approval.

## Restore, build, and test

```powershell
cd C:\Users\Admin\OneDrive\Desktop\keyboard.wtf
dotnet restore KeyboardWtf.sln
dotnet build KeyboardWtf.sln -c Release --no-restore
dotnet test KeyboardWtf.sln -c Release --no-restore
```

The test suite includes a harmless `https://example.com/` inspection and a localhost mock-form fill/preview flow. It does not purchase, submit, publish, send, delete, or modify an account.

## Run the local build

Close an already running installed `keyboard.wtf.exe` first so only one tray instance owns the global shortcuts.

```powershell
dotnet run --project .\src\KeyboardWtf.csproj -c Release
```

Expected shortcut:

- Jarvis: `Ctrl+Alt+J`
- Conflict-only Jarvis fallback: `Ctrl+Shift+J`
- Cancel: `Ctrl+Alt+X`
- `Ctrl+Alt+Space` is not used

Opening the local build once migrates the old default `Ctrl+Alt+Q` setting to `Ctrl+Alt+J` and writes schema version 2. A current-schema user-customized shortcut is preserved.

## Settings checks

Open Settings with `Ctrl+Alt+,` and verify:

1. Hotkeys shows configured and active Jarvis shortcut `Ctrl+Alt+J`.
2. Task Agent / Browser Agent appears in the sidebar.
3. Show task popup defaults on.
4. Ask for clarification at low confidence defaults on.
5. Clarification mode defaults to Smart Clarify.
6. Browser Agent is marked experimental and enabled.
7. Intent memory shows typed structured entries and Edit/Delete controls.

## Safe manual prompts

- `Open Microsoft Excel.` It should resolve Excel directly if installed.
- `Remember that whenever I say initialize my setup, open Chrome, VS Code, and my project folder.`
- `List my memories.`
- `Update the initialize my setup memory to also open Terminal.`
- `Use this selected text as context and open example.com as a browser task.`

For a controlled mock task, use a harmless local form or test site. Confirm that:

- a task tab appears only for the multi-step task;
- context capture is visible as a completed or failed step;
- ambiguous targets show `Waiting for clarification`;
- protected external-state actions show `Waiting for confirmation`;
- cancel closes the controlled task page;
- the receipt records only verified actions.

## Known limitations

- The controlled profile starts without the user's normal browser cookies. Login is manual and remains in the dedicated local profile.
- CAPTCHA, OTP, passwords, payment, checkout, banking, and protected final submissions are not automated.
- Generic performance still depends on Gemini selecting useful Playwright actions from the bounded page snapshot.
- A changed or poorly accessible website can require clarification or manual completion.
- The installer/version remains v1.2.0 until the local upgrade is approved for release.
