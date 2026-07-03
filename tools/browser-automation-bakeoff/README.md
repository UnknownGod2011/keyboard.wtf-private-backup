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

Gemini keys are bake-off-only. Put them in ignored `.env.local` as:

```env
BAKEOFF_GEMINI_API_KEY_01=...
BAKEOFF_GEMINI_API_KEY_02=...
```

The runners rotate only `BAKEOFF_GEMINI_API_KEY_*` and do not read or modify keyboard.wtf/Jarvis API-key settings.

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
.\.venv\Scripts\python.exe python\backends\browser_use_runner.py --all
```

Run orchestrated comparison:

```powershell
npm run run -- --backend stagehand --backend playwright-mcp
.\.venv\Scripts\python.exe python\backends\browser_use_runner.py --all
npm run report
```

Or run all three through the unified runner:

```powershell
npm run run -- --backend browser-use --backend stagehand --backend playwright-mcp
npm run report
```
