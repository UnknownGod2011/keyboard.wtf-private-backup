import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import dotenv from "dotenv";

export const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const tasksPath = path.join(rootDir, "tasks.json");
export const resultsDir = path.join(rootDir, "results");
export const screenshotsDir = path.join(rootDir, "screenshots");
export const tracesDir = path.join(rootDir, "traces");
export const logsDir = path.join(rootDir, "logs");
export const profilesDir = path.join(rootDir, "profiles");

export function ensureDirs() {
  for (const dir of [resultsDir, screenshotsDir, tracesDir, logsDir, profilesDir]) {
    fs.mkdirSync(dir, { recursive: true });
  }
}

export function loadLocalEnv() {
  dotenv.config({ path: path.join(rootDir, ".env.local"), quiet: true });
  dotenv.config({ path: path.join(rootDir, ".env"), quiet: true });
  // For this bake-off, prefer GEMINI_API_KEY when present. The user's local
  // file may contain multiple aliases, and some aliases may point to disabled
  // keys. Do not print or persist values.
  if (process.env.GEMINI_API_KEY) {
    process.env.GOOGLE_API_KEY = process.env.GEMINI_API_KEY;
    process.env.GOOGLE_GENERATIVE_AI_API_KEY = process.env.GEMINI_API_KEY;
  } else if (process.env.GOOGLE_API_KEY) {
    process.env.GEMINI_API_KEY = process.env.GOOGLE_API_KEY;
    process.env.GOOGLE_GENERATIVE_AI_API_KEY = process.env.GOOGLE_API_KEY;
  }
}

export function loadTasks() {
  return JSON.parse(fs.readFileSync(tasksPath, "utf8"));
}

export function parseArgs(argv = process.argv.slice(2)) {
  const out = { backend: [], task: [] };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--backend") out.backend.push(argv[++i]);
    else if (arg === "--task") out.task.push(argv[++i]);
    else if (arg === "--all") out.all = true;
    else if (arg.startsWith("--")) out[arg.slice(2)] = true;
  }
  return out;
}

export function nowIso() {
  return new Date().toISOString();
}

export function safeName(input) {
  return String(input).replace(/[^a-z0-9._-]+/gi, "-").replace(/^-+|-+$/g, "").toLowerCase();
}

export function redactError(error) {
  const text = error instanceof Error ? `${error.name}: ${error.message}\n${error.stack ?? ""}` : String(error ?? "");
  return text
    .replace(/AQ\.Ab8[0-9A-Za-z._-]+/g, "[REDACTED_API_KEY]")
    .replace(/(GEMINI|GOOGLE|BROWSERBASE|OPENAI|ANTHROPIC)(_GENERATIVE_AI)?_API_KEY=\\S+/gi, "$1_API_KEY=[REDACTED]");
}

export function makeBaseResult(backendName, task) {
  return {
    backendName,
    taskId: task.id,
    taskText: task.taskText,
    targetSite: task.targetSite,
    startedAt: nowIso(),
    endedAt: null,
    success: false,
    finalUrl: null,
    screenshotPaths: [],
    runtimeMs: null,
    actionCount: 0,
    llmCallCount: null,
    confirmationCount: task.cartMutationPreconfirmed ? 1 : 0,
    browserProfileUsed: null,
    realChromeUsed: false,
    controlledBrowserUsed: false,
    wrongBrowserOpened: false,
    stoppedAfterSuccess: null,
    failureReason: null,
    logsPath: null,
    tracesPath: null,
    summary: null,
    rawBackendMetadata: {}
  };
}

export function finishResult(result, startTime, patch = {}) {
  Object.assign(result, patch);
  result.endedAt = nowIso();
  result.runtimeMs = Date.now() - startTime;
  return result;
}

export function writeJson(filePath, data) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify(data, null, 2)}\n`, "utf8");
}

export function appendJsonl(filePath, data) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.appendFileSync(filePath, `${JSON.stringify(data)}\n`, "utf8");
}

export function runProcess(command, args, options = {}) {
  return new Promise((resolve) => {
    const executable = process.platform === "win32" && ["npm", "npx"].includes(command)
      ? `${command}.cmd`
      : command;
    const env = {};
    for (const [key, value] of Object.entries(options.env ?? process.env)) {
      if (value !== undefined && value !== null) env[key] = String(value);
    }
    let child;
    try {
      child = spawn(executable, args, {
        cwd: options.cwd ?? rootDir,
        env,
        shell: process.platform === "win32" && executable.endsWith(".cmd"),
        stdio: ["ignore", "pipe", "pipe"]
      });
    } catch (error) {
      resolve({ code: -1, stdout: "", stderr: redactError(error) });
      return;
    }
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => {
      stdout += chunk.toString();
      if (options.echo) process.stdout.write(chunk);
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk.toString();
      if (options.echo) process.stderr.write(chunk);
    });
    child.on("close", (code) => resolve({ code, stdout, stderr }));
    child.on("error", (error) => resolve({ code: -1, stdout, stderr: redactError(error) }));
  });
}

export function selectTasks(args) {
  const allTasks = loadTasks();
  if (!args.task?.length) return allTasks;
  const wanted = new Set(args.task);
  return allTasks.filter((task) => wanted.has(task.id));
}
