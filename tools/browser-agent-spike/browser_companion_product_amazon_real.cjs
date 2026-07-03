const { chromium } = require("playwright");
const path = require("node:path");
const fs = require("node:fs");

const root = path.resolve(__dirname, "..", "..");
const extensionPath = path.join(root, "extensions", "browser-companion");
const profile = path.join(root, "output", "browser-companion-product-amazon-real-profile");
const screenshotDir = path.join(root, "output", "browser-agent-real-site-screenshots");
const expectedExtensionVersion = JSON.parse(fs.readFileSync(path.join(extensionPath, "manifest.json"), "utf8")).version;

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

function taskStatus(task) {
  return prop(task, "status", "Status") || "";
}

function taskFromResult(result) {
  return prop(result, "task", "Task") || {};
}

async function jsonFetch(url, options = {}) {
  const response = await fetch(url, options);
  const text = await response.text();
  let json = {};
  try { json = text ? JSON.parse(text) : {}; } catch { json = { raw: text }; }
  if (!response.ok && !options.allowNonOk) {
    const error = new Error(json.error || json.message || json.Message || `HTTP ${response.status}`);
    error.response = json;
    throw error;
  }
  return json;
}

async function waitFor(predicate, label, ms = 20000) {
  const started = Date.now();
  while (Date.now() - started < ms) {
    const value = await predicate();
    if (value) return value;
    await new Promise((resolve) => setTimeout(resolve, 350));
  }
  throw new Error(`Timed out waiting for ${label}`);
}

async function readTask(taskId) {
  const settings = await jsonFetch("http://localhost:8090/api/settings");
  const tasks = settings.jarvisTasks || settings.JarvisTasks || [];
  return tasks.find((task) => (task.taskId || task.TaskId) === taskId);
}

async function readGraph() {
  const response = await jsonFetch("http://localhost:8090/api/browser-companion/graph", { headers: settingsHeader });
  return response.graph || response.Graph || {};
}

function classifyBlock(task, graph) {
  const text = [
    task?.result || task?.Result || "",
    task?.pendingPrompt || task?.PendingPrompt || "",
    graph?.title || graph?.Title || "",
    ...(graph?.pageSignals || graph?.PageSignals || []),
  ].join(" ").toLowerCase();
  if (/captcha|verify you are human|cloudflare|bot/.test(text)) return "blocked by CAPTCHA/security/bot protection";
  if (/sign in|log in|password|otp|verification/.test(text)) return "blocked by login/authentication";
  if (/add to cart/.test(text) && /could not find|no longer exposes/.test(text)) return "blocked by add-to-cart control graph failure";
  if (/product result|multiple|variant|color|size|unavailable|out of stock/.test(text)) return "blocked by product ambiguity or availability";
  if (/browser companion extension is not connected/.test(text)) return "blocked by missing extension connection";
  return "";
}

async function runCartTask(label, page) {
  const started = Date.now();
  const start = await jsonFetch("http://localhost:8090/api/browser-agent/start", {
    method: "POST",
    headers: settingsHeader,
    allowNonOk: true,
    body: JSON.stringify({
      title: "Amazon real add to cart",
      task: "Open the MX Master 4 mouse and Stanley bottle on Amazon and add them to cart. Stop at the cart page.",
      startUrl: "https://www.amazon.in",
      expectedOutcome: "The Amazon cart page is open with the requested products added, with no checkout or payment.",
    }),
  });
  const taskId = prop(start, "taskId", "TaskId");
  let confirmations = 0;
  const startTask = taskFromResult(start);
  const needsConfirmation =
    prop(start, "confirmationRequired", "ConfirmationRequired") === true
    || taskStatus(startTask) === "WaitingForConfirmation";
  if (needsConfirmation) {
    confirmations += 1;
    await jsonFetch("http://localhost:8090/api/browser-agent/confirm", {
      method: "POST",
      headers: settingsHeader,
      body: JSON.stringify({ taskId, confirmed: true }),
    });
  }

  const finalTask = await waitFor(async () => {
    const task = await readTask(taskId);
    const status = taskStatus(task);
    return ["Completed", "Failed", "TimedOut", "Cancelled", "WaitingForClarification"].includes(status) ? task : null;
  }, `${label} terminal task`, 90000);

  const graph = await readGraph().catch(() => ({}));
  const screenshotPath = path.join(screenshotDir, `amazon-real-${label}-${Date.now()}.png`);
  await page.screenshot({ path: screenshotPath, fullPage: false }).catch(() => null);
  const controls = graph.controls || graph.Controls || [];
  const finalUrl = graph.url || graph.Url || page.url();
  return {
    label,
    taskId,
    status: taskStatus(finalTask),
    result: finalTask.result || finalTask.Result || "",
    runtimeMs: Date.now() - started,
    confirmations,
    finalUrl,
    visibleSuccess: String(finalUrl).toLowerCase().includes("/cart") || /cart/i.test(graph.title || graph.Title || ""),
    pageTitle: graph.title || graph.Title || "",
    pageType: graph.pageType || graph.PageType || "",
    pageSignals: graph.pageSignals || graph.PageSignals || [],
    graphControls: controls.length,
    blockReason: classifyBlock(finalTask, graph),
    screenshotPath,
    receipts: finalTask.receipt || finalTask.Receipt || [],
  };
}

async function main() {
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`],
  });
  try {
    const page = context.pages()[0] || await context.newPage();
    await page.goto("https://www.amazon.in", { waitUntil: "domcontentloaded", timeout: 30000 }).catch(() => null);
    const bridge = await waitFor(async () => {
      const settings = await jsonFetch("http://localhost:8090/api/settings");
      const companion = settings.browserCompanionStatus || settings.BrowserCompanionStatus || {};
      const available = companion.available ?? companion.Available;
      const version = companion.extensionVersion || companion.ExtensionVersion || "";
      const activeUrl = companion.activeTabUrl || companion.ActiveTabUrl || "";
      return available && version === expectedExtensionVersion && /amazon\.in/i.test(activeUrl) ? companion : null;
    }, "Browser Companion bridge connection", 25000);

    const first = await runCartTask("first", page);
    const second = await runCartTask("replay", page);
    const ok = first.status === "Completed" && second.status === "Completed" && first.visibleSuccess && second.visibleSuccess;
    console.log(JSON.stringify({
      ok,
      realSite: "https://www.amazon.in",
      browserMode: "visible Chromium profile with Browser Companion extension",
      extensionPath,
      bridge,
      first,
      second,
      replayFaster: second.runtimeMs < first.runtimeMs,
    }, null, 2));
    if (!ok) process.exitCode = 1;
  } finally {
    await context.close();
  }
}

main().catch((error) => {
  console.error(error?.response ? JSON.stringify(error.response, null, 2) : error);
  process.exitCode = 1;
});
