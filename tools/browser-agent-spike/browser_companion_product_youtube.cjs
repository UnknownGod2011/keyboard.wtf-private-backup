const { chromium } = require("playwright");
const path = require("node:path");
const fs = require("node:fs");

const root = path.resolve(__dirname, "..", "..");
const extensionPath = path.join(root, "extensions", "browser-companion");
const profile = path.join(root, "output", "browser-companion-product-youtube-profile");
const workflowPath = path.join(process.env.LOCALAPPDATA || "", "keyboard.wtf", "browser-workflows.json");
const screenshotDir = path.join(root, "output", "browser-agent-real-site-screenshots");
const expectedExtensionVersion = JSON.parse(fs.readFileSync(path.join(extensionPath, "manifest.json"), "utf8")).version;
const channelName = process.env.YOUTUBE_CHANNEL || process.argv.slice(2).join(" ") || "MrBeast";
fs.rmSync(profile, { recursive: true, force: true });
fs.mkdirSync(profile, { recursive: true });
fs.mkdirSync(screenshotDir, { recursive: true });

const settingsHeader = {
  "X-Keyboard-Wtf-Settings-Action": "explicit-user-action",
  "Content-Type": "application/json",
};

function prop(object, camel, pascal) {
  return object?.[camel] ?? object?.[pascal];
}

function taskStatus(result) {
  const task = prop(result, "task", "Task") || {};
  return prop(task, "status", "Status") || "";
}

function taskReceipts(result) {
  const task = prop(result, "task", "Task") || {};
  return prop(task, "receipt", "Receipt") || [];
}

function readWorkflowSummary() {
  if (!workflowPath || !fs.existsSync(workflowPath)) {
    return { path: workflowPath, total: 0, youtube: [] };
  }
  const workflows = JSON.parse(fs.readFileSync(workflowPath, "utf8"));
  const youtube = workflows
    .filter((workflow) => String(workflow.Domain || workflow.domain || "").includes("youtube.com"))
    .map((workflow) => ({
      workflowId: workflow.WorkflowId || workflow.workflowId,
      taskType: workflow.TaskType || workflow.taskType,
      successCount: workflow.SuccessCount || workflow.successCount || 0,
      failureCount: workflow.FailureCount || workflow.failureCount || 0,
      confidence: workflow.Confidence ?? workflow.confidence,
      averageRuntimeMs: workflow.AverageRuntimeMs || workflow.averageRuntimeMs || 0,
      lastRuntimeMs: workflow.LastRuntimeMs || workflow.lastRuntimeMs || 0,
      backend: workflow.BackendThatDiscoveredIt || workflow.backendThatDiscoveredIt || "",
    }));
  return { path: workflowPath, total: workflows.length, youtube };
}

async function jsonFetch(url, options = {}) {
  const response = await fetch(url, options);
  const text = await response.text();
  let json = {};
  try {
    json = text ? JSON.parse(text) : {};
  } catch {
    json = { raw: text };
  }
  if (!response.ok) {
    const error = new Error(json.error || json.message || `HTTP ${response.status}`);
    error.response = json;
    throw error;
  }
  return json;
}

async function waitFor(predicate, label, ms = 15000) {
  const started = Date.now();
  while (Date.now() - started < ms) {
    const value = await predicate();
    if (value) return value;
    await new Promise((resolve) => setTimeout(resolve, 300));
  }
  throw new Error(`Timed out waiting for ${label}`);
}

async function runBrowserTask(label, page) {
  const started = Date.now();
  const result = await jsonFetch("http://localhost:8090/api/browser-agent/start", {
    method: "POST",
    headers: settingsHeader,
    body: JSON.stringify({
      title: `Play oldest ${channelName} video`,
      task: `Play the oldest ${channelName} video on YouTube.`,
      expectedOutcome: `A YouTube watch page/player is open for the oldest visible ${channelName} channel video.`,
    }),
  });
  const apiRuntimeMs = Date.now() - started;
  const graph = await waitFor(async () => {
    const current = await readCompanionGraph();
    const url = String(current?.url || current?.Url || "");
    return url.includes("youtube.com/watch") || url.includes("/watch") ? current : null;
  }, `${label} YouTube watch graph`, 15000).catch(async () => await readCompanionGraph());
  const screenshotPath = path.join(screenshotDir, `youtube-${label}-${Date.now()}.png`);
  await page.screenshot({ path: screenshotPath, fullPage: false }).catch(() => null);
  const playerExists = await page.locator("video, #movie_player, ytd-player").count().catch(() => 0);
  const finalUrl = graph?.url || graph?.Url || page.url();
  const signals = graph?.pageSignals || graph?.PageSignals || [];
  return {
    label,
    ok: prop(result, "ok", "Ok") === true,
    message: prop(result, "message", "Message") || "",
    taskId: prop(result, "taskId", "TaskId") || "",
    taskStatus: taskStatus(result),
    apiRuntimeMs,
    evidenceRuntimeMs: Date.now() - started,
    finalUrl,
    visibleSuccess: String(finalUrl).includes("/watch") && playerExists > 0,
    playerExists: playerExists > 0,
    graphControls: (graph?.controls || graph?.Controls || []).length,
    pageSignals: signals,
    screenshotPath,
    receipts: taskReceipts(result),
  };
}

async function readCompanionGraph() {
  const response = await jsonFetch("http://localhost:8090/api/browser-companion/graph", {
    headers: settingsHeader,
  });
  return response.graph || response.Graph;
}

async function main() {
  const before = readWorkflowSummary();
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`],
  });
  try {
    const page = context.pages()[0] || await context.newPage();
    await page.bringToFront();
    await page.goto("https://example.com", { waitUntil: "domcontentloaded", timeout: 20000 });

    const bridge = await waitFor(async () => {
      const settings = await jsonFetch("http://localhost:8090/api/settings");
      const companion = settings.browserCompanionStatus || settings.BrowserCompanionStatus || {};
      const available = companion.available ?? companion.Available;
      const version = companion.extensionVersion || companion.ExtensionVersion || "";
      const activeUrl = companion.activeTabUrl || companion.ActiveTabUrl || "";
      return available && version === expectedExtensionVersion && /example\.com/i.test(activeUrl) ? companion : null;
    }, "Browser Companion bridge connection", 20000);
    await waitFor(async () => {
      const graph = await readCompanionGraph();
      return String(graph?.url || graph?.Url || "").includes("example.com") ? graph : null;
    }, "fresh Browser Companion graph", 20000);

    const first = await runBrowserTask("first", page);
    if (!first.ok) {
      console.log(JSON.stringify({ ok: false, extensionPath, bridge, before, first, after: readWorkflowSummary() }, null, 2));
      process.exitCode = 1;
      return;
    }

    const second = await runBrowserTask("replay", page);
    const after = readWorkflowSummary();
    console.log(JSON.stringify({
      ok: first.ok && second.ok,
      channelName,
      extensionPath,
      bridge,
      before,
      first,
      second,
      after,
      replayFaster: second.apiRuntimeMs < first.apiRuntimeMs,
    }, null, 2));
    if (!second.ok) process.exitCode = 1;
  } finally {
    await context.close();
  }
}

main().catch((error) => {
  console.error(error?.response ? JSON.stringify(error.response, null, 2) : error);
  process.exitCode = 1;
});
