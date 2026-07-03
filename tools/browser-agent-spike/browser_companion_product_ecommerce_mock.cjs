const { chromium } = require("playwright");
const http = require("node:http");
const path = require("node:path");
const fs = require("node:fs");

const root = path.resolve(__dirname, "..", "..");
const extensionPath = path.join(root, "extensions", "browser-companion");
const profile = path.join(root, "output", "browser-companion-product-ecommerce-profile");
fs.rmSync(profile, { recursive: true, force: true });
fs.mkdirSync(profile, { recursive: true });

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
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`Timed out waiting for ${label}`);
}

function productPage(name, id) {
  return `<!doctype html><html><head><title>${name}</title></head><body>
    <main>
      <h1>${name}</h1>
      <p>Prime product. 4.6 rating. Price ₹9,999.</p>
      <button id="add-to-cart-button" onclick="localStorage.setItem('${id}','1'); location.href='/cart/added?product=${id}'">Add to Cart</button>
      <button>Buy Now</button>
    </main>
  </body></html>`;
}

function searchPage(query) {
  return `<!doctype html><html><head><title>Amazon mock search</title></head><body>
    <form role="search"><input type="search" value="${query}" placeholder="Search Amazon"><button>Search</button></form>
    <section aria-label="Results">
      <article data-component-type="s-search-result">
        <a href="/dp/mx-master-4"><h2>Logitech MX Master 4 Wireless Mouse</h2></a>
        <p>4.7 rating · ₹10,999</p>
      </article>
      <article data-component-type="s-search-result">
        <a href="/dp/stanley-bottle"><h2>Stanley Quencher Bottle 30 oz</h2></a>
        <p>4.5 rating · ₹3,499</p>
      </article>
    </section>
  </body></html>`;
}

function cartPage() {
  return `<!doctype html><html><head><title>Shopping Cart</title></head><body>
    <h1>Shopping Cart</h1>
    <div>Logitech MX Master 4 Wireless Mouse</div>
    <div>Stanley Quencher Bottle 30 oz</div>
    <button>Proceed to checkout</button>
  </body></html>`;
}

async function startMockServer() {
  const server = http.createServer((req, res) => {
    res.setHeader("content-type", "text/html; charset=utf-8");
    const url = new URL(req.url || "/", "http://127.0.0.1");
    if (url.pathname === "/s") return res.end(searchPage(url.searchParams.get("k") || ""));
    if (url.pathname === "/dp/mx-master-4") return res.end(productPage("Logitech MX Master 4 Wireless Mouse", "mx"));
    if (url.pathname === "/dp/stanley-bottle") return res.end(productPage("Stanley Quencher Bottle 30 oz", "stanley"));
    if (url.pathname === "/cart/added") return res.end(`<!doctype html><html><body><h1>Added to Cart</h1><a href="/gp/cart/view.html">Go to Cart</a></body></html>`);
    if (url.pathname === "/gp/cart/view.html") return res.end(cartPage());
    return res.end(`<!doctype html><html><body><h1>Amazon mock</h1><a href="/s?k=mx+master+4">Search</a></body></html>`);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return { server, base: `http://127.0.0.1:${server.address().port}` };
}

async function readTask(taskId) {
  const settings = await jsonFetch("http://localhost:8090/api/settings");
  const tasks = settings.jarvisTasks || settings.JarvisTasks || [];
  return tasks.find((task) => (task.taskId || task.TaskId) === taskId);
}

async function runCartTask(label, base) {
  const started = Date.now();
  const start = await jsonFetch("http://localhost:8090/api/browser-agent/start", {
    method: "POST",
    headers: settingsHeader,
    allowNonOk: true,
    body: JSON.stringify({
      title: "Amazon mock add to cart",
      task: "Open the MX Master 4 mouse and Stanley bottle on Amazon and add them to cart. Stop at the cart page.",
      startUrl: base,
      expectedOutcome: "The cart page is open with both requested products, with no checkout or payment.",
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
    await waitFor(async () => {
      const task = await readTask(taskId);
      const status = taskStatus(task);
      return status && status !== "WaitingForConfirmation" ? task : null;
    }, `${label} confirmation transition`, 10000);
  }
  const finalTask = await waitFor(async () => {
    const task = await readTask(taskId);
    const status = taskStatus(task);
    return ["Completed", "Failed", "TimedOut", "Cancelled"].includes(status) ? task : null;
  }, `${label} terminal task`, 45000);
  const graphResponse = await jsonFetch("http://localhost:8090/api/browser-companion/graph", { headers: settingsHeader });
  const graph = graphResponse.graph || graphResponse.Graph || {};
  const controls = graph.controls || graph.Controls || [];
  return {
    label,
    taskId,
    status: taskStatus(finalTask),
    result: finalTask.result || finalTask.Result || "",
    runtimeMs: Date.now() - started,
    confirmations,
    finalUrl: graph.url || graph.Url || "",
    finalControls: controls.slice(0, 20).map((control) => ({
      role: control.role || control.Role || "",
      tag: control.tagName || control.TagName || "",
      text: control.text || control.Text || "",
      aria: control.ariaLabel || control.AriaLabel || "",
      href: control.href || control.Href || "",
      selectors: control.selectorCandidates || control.SelectorCandidates || [],
      sensitive: control.sensitive ?? control.Sensitive,
    })),
    receipts: finalTask.receipt || finalTask.Receipt || [],
  };
}

async function main() {
  const { server, base } = await startMockServer();
  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`],
  });
  try {
    const page = context.pages()[0] || await context.newPage();
    await page.goto(base, { waitUntil: "domcontentloaded" });
    const bridge = await waitFor(async () => {
      const settings = await jsonFetch("http://localhost:8090/api/settings");
      const companion = settings.browserCompanionStatus || settings.BrowserCompanionStatus || {};
      return (companion.available ?? companion.Available) ? companion : null;
    }, "Browser Companion bridge connection");
    const first = await runCartTask("first", base);
    const second = await runCartTask("replay", base);
    console.log(JSON.stringify({
      ok: first.status === "Completed" && second.status === "Completed",
      base,
      bridge,
      first,
      second,
      replayFaster: second.runtimeMs < first.runtimeMs,
    }, null, 2));
    if (first.status !== "Completed" || second.status !== "Completed") process.exitCode = 1;
  } finally {
    await context.close();
    server.close();
  }
}

main().catch((error) => {
  console.error(error?.response ? JSON.stringify(error.response, null, 2) : error);
  process.exitCode = 1;
});
