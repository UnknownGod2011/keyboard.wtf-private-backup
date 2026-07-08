# Browser automation bake-off

This folder is an isolated benchmark harness. It is not wired into `keyboard.wtf`.

Goals:

- test real browser automation backends exactly as their packages/docs intend;
- disable old workflow memory, old Browser Companion routing, and stale replay;
- run the same real tasks through each feasible backend;
- capture structured results, screenshots, traces, and failure reasons;
- pick a backend based on evidence before integrating anything into the app.

Backends:

1. `browser-use` through the real Python package and Gemini (`ChatGoogle`).
2. `Stagehand` through `@browserbasehq/stagehand`.
3. `Playwright MCP` through the real `@playwright/mcp` server and an MCP client loop.
4. Optional `browser-use-native` through `ChatBrowserUse` when `BROWSER_USE_API_KEY` is configured. This is skipped cleanly when the key is missing.

Gemini keys are bake-off-only. Put them in ignored `.env.local` as:

```env
BAKEOFF_GEMINI_API_KEY_01=...
BAKEOFF_GEMINI_API_KEY_02=...
```

The runners rotate only `BAKEOFF_GEMINI_API_KEY_*` and do not read or modify keyboard.wtf/Jarvis API-key settings.

Optional Browser Use optimized model path:

```env
BROWSER_USE_API_KEY=
BROWSER_USE_API_KEY_01=
BROWSER_USE_API_KEY_02=
BROWSER_USE_AGENT_MODEL=bu-2-0
```

If `BROWSER_USE_API_KEY` / `BROWSER_USE_API_KEY_*` are absent, the optimized Browser Use model path is reported as skipped and normal Gemini-backed browser-use benchmarks still run. When multiple `BROWSER_USE_API_KEY_*` values are present, the runner rotates across them on quota/rate/auth failures and records only masked key ids.

Generated output is ignored by Git:

- `.env.local`
- `.venv/`
- `node_modules/`
- `profiles/`
- `results/`
- `screenshots/`
- `traces/`
- `logs/`

Run setup:

```powershell
cd tools/browser-automation-bakeoff
npm install
C:\Users\Admin\AppData\Local\Programs\Python\Python312\python.exe -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
npm run doctor
```

Run one backend:

```powershell
npm run run:stagehand
npm run run:playwright-mcp
.\.venv\Scripts\python.exe python\backends\browser_use_runner.py --provider gemini --mode cold --all
.\.venv\Scripts\python.exe python\backends\browser_use_runner.py --provider browser-use --mode cold --all
```

Run orchestrated comparison:

```powershell
npm run run -- --backend stagehand --backend playwright-mcp
.\.venv\Scripts\python.exe python\backends\browser_use_runner.py --provider gemini --mode cold --all
npm run report
```

Or run all three through the unified runner:

```powershell
npm run run -- --backend browser-use --backend stagehand --backend playwright-mcp
npm run report
```

Run a warm-profile browser-use pass:

```powershell
npm run run -- --backend browser-use --warm
npm run report
```
