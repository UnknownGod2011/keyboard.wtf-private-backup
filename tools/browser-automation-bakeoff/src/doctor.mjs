import fs from "node:fs";
import path from "node:path";
import { ensureDirs, loadLocalEnv, rootDir, runProcess } from "./common.mjs";

ensureDirs();
loadLocalEnv();

const checks = [];
function add(name, ok, detail = "") {
  checks.push({ name, ok, detail });
}

add("root exists", fs.existsSync(rootDir), rootDir);
add(".env.local ignored", (await runProcess("git", ["check-ignore", "-q", "--", "tools/browser-automation-bakeoff/.env.local"], { cwd: path.resolve(rootDir, "..", "..") })).code === 0);
add("GOOGLE_API_KEY or GEMINI_API_KEY present", Boolean(process.env.GOOGLE_API_KEY || process.env.GEMINI_API_KEY), "value not printed");
add("GOOGLE_GENERATIVE_AI_API_KEY present for Stagehand", Boolean(process.env.GOOGLE_GENERATIVE_AI_API_KEY), "value not printed");

for (const [cmd, args, name] of [
  ["node", ["--version"], "node"],
  ["npm", ["--version"], "npm"],
  ["npx", ["--version"], "npx"]
]) {
  const result = await runProcess(cmd, args);
  add(name, result.code === 0, result.stdout.trim() || result.stderr.trim());
}

const browserUsePython = path.join(rootDir, ".venv", "Scripts", "python.exe");
if (fs.existsSync(browserUsePython)) {
  const result = await runProcess(browserUsePython, ["-c", "import browser_use; print(getattr(browser_use, '__version__', 'unknown'))"]);
  add("browser-use import", result.code === 0, result.stdout.trim() || result.stderr.trim());
} else {
  add("browser-use venv", false, `${browserUsePython} missing`);
}

console.table(checks);
const failed = checks.filter((check) => !check.ok);
if (failed.length) {
  process.exitCode = 1;
}

