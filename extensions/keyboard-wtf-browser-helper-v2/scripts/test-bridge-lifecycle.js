const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const event = () => ({ addListener() {} });
const flush = async () => { for (let index = 0; index < 30; index++) await Promise.resolve(); };
async function until(predicate) {
  for (let index = 0; index < 1000 && !predicate(); index++) await Promise.resolve();
  assert.ok(predicate(), "expected protocol event did not arrive");
}

function harness() {
  const messages = [];
  const timers = new Map();
  let nextTimer = 1;
  let receive;
  let rendererMessage;
  let resolveStatus;
  let resolvePrepare;
  let command = null;
  const cancelled = new Set();
  let stallStatus = false;
  const pageMessages = [];
  const port = {
    onMessage: { addListener(fn) { receive = fn; } }, onDisconnect: event(), disconnect() {},
    postMessage(message) {
      messages.push(message);
      const reply = { ok: true, command: message.type === "poll" ? command : null,
        commandAuthorized: !cancelled.has(message.commandId) };
      if (message.type === "poll") command = null;
      queueMicrotask(() => receive(reply));
    },
  };
  const context = vm.createContext({
    console, URL, crypto: { randomUUID: () => "lifecycle-test" },
    setTimeout(fn, delay) { const id = nextTimer++; timers.set(id, { fn, delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    chrome: {
      action: { setBadgeText() {}, setBadgeBackgroundColor() {} },
      alarms: { create() {}, onAlarm: event() },
      runtime: { id: "gojdebdikepkpapmnggeljneamjdigbh", getManifest: () => ({ version: "2.1.27" }),
        connectNative: () => port, onInstalled: event(), onStartup: event(),
        onMessage: { addListener(fn) { rendererMessage = fn; } }, onMessageExternal: event() },
      tabs: { onRemoved: event(), onActivated: event(),
        query: async () => [{ id: 1, windowId: 1, url: "https://example.org/" }],
        get: async () => ({ id: 1, windowId: 1, url: "https://example.org/" }),
        update: async (id) => { pageMessages.push(`activate_tab:${id}`); return { id, windowId: 1 }; },
        sendMessage(tabId, message) {
          pageMessages.push(message.type);
          if (message.type === "helper_status" && stallStatus) {
            stallStatus = false;
            return new Promise(resolve => { resolveStatus = resolve; });
          }
          if (message.type === "prepare_action")
            return new Promise(resolve => { resolvePrepare = resolve; });
          return Promise.resolve({ ok: true, version: "2.1.21" });
        } },
      webNavigation: { getAllFrames: async () => [] },
    },
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "service-worker.js"), "utf8")
    + "\nglobalThis.probe = { pollOnce, wakeBridge, ownTab, endTask, resetNativePort, connectNative, taskCount: () => taskTabs.size, currentCommand: () => pollBusy?.command?.commandId };", context);
  return {
    messages, pageMessages, context,
    stallStatus: () => { stallStatus = true; },
    resumeStatus: () => resolveStatus({ ok: true, version: "2.1.21" }),
    queue: value => { command = value; },
    cancel: (id = "click") => { cancelled.add(id); },
    async execute(value) {
      command = value;
      await context.probe.pollOnce();
      return messages.findLast(message => message.type === "command_result" && message.commandId === value.commandId)?.result;
    },
    preparing: () => Boolean(resolvePrepare),
    preparedReply: () => resolvePrepare,
    resumePrepare: (supportsAuthority = true) => resolvePrepare({ ok: true, resolutionToken: "prepared-on-old-turn", commandAuthoritySupported: supportsAuthority }),
    rendererAuthority: (message = {}, sender = {}) => new Promise(resolve => rendererMessage({
      type: "helper_command_authority", taskId: "task", commandId: "click", ...message,
    }, { id: "gojdebdikepkpapmnggeljneamjdigbh", tab: { id: 1 }, frameId: 0, ...sender }, resolve)),
    async tickPoll() {
      const timer = [...timers.entries()].find(([, timer]) => timer.delay === 900 || timer.delay === 220);
      assert.ok(timer, "poll heartbeat must stay scheduled during page work");
      timers.delete(timer[0]);
      timer[1].fn();
      await flush();
    },
  };
}

async function main() {
  {
    const h = harness(); await flush();
    const tabs = [{ id: 1, windowId: 1 }, { id: 2, windowId: 1 }, { id: 3, windowId: 2 }];
    const removed = [];
    h.context.chrome.tabs.query = async () => tabs.slice();
    h.context.chrome.tabs.create = async ({ url }) => { const tab = { id: 4, windowId: 1, pendingUrl: url }; tabs.push(tab); return tab; };
    h.context.chrome.tabs.remove = async id => { removed.push(id); tabs.splice(tabs.findIndex(tab => tab.id === id), 1); };
    const opened = await h.execute({ taskId: "direct", commandId: "open", type: "open_direct_tab", requiresAuthorityCheck: true, url: "https://example.org/requested" });
    assert.equal(opened.ok, true); assert.equal(opened.tabId, 4); assert.ok(opened.sessionId);
    assert.equal(h.context.probe.taskCount(), 0, "direct tabs must not create a BA task/lease");
    const close = { taskId: "direct", type: "close_direct_tab", requiresAuthorityCheck: true, tabId: 4, windowId: 1, expectedSessionId: opened.sessionId };
    assert.equal((await h.execute({ ...close, commandId: "wrong-session", expectedSessionId: "expired" })).ok, false);
    assert.equal((await h.execute({ ...close, commandId: "wrong-window", windowId: 2 })).ok, false);
    assert.deepEqual(removed, []);
    assert.equal((await h.execute({ ...close, commandId: "close" })).ok, true);
    assert.equal((await h.execute({ ...close, commandId: "already" })).alreadyClosed, true);
    assert.deepEqual(removed, [4]);
    assert.deepEqual(tabs.map(tab => tab.id), [1, 2, 3], "unrelated tabs/windows must remain intact");
  }
  {
    const h = harness(); await flush();
    let listed = false; let finishQuery;
    h.context.chrome.tabs.query = options => Object.keys(options).length
      ? Promise.resolve([{ id: 1, windowId: 1, url: "https://example.org/" }])
      : new Promise(resolve => { listed = true; finishQuery = resolve; });
    const removed = [];
    h.context.chrome.tabs.remove = async id => removed.push(id);
    const pending = h.execute({ taskId: "direct", commandId: "cancel-close", type: "close_direct_tab", requiresAuthorityCheck: true, tabId: 1, windowId: 1, expectedSessionId: "lifecycle-test" });
    await until(() => listed);
    h.cancel("cancel-close"); finishQuery([{ id: 1, windowId: 1 }]);
    assert.notEqual((await pending)?.ok, true);
    assert.deepEqual(removed, [], "cancellation during validation must prevent tab closure");
  }
  {
    const h = harness(); await flush();
    let exists = true;
    h.context.chrome.tabs.query = async () => exists ? [{ id: 1, windowId: 1 }] : [];
    h.context.chrome.tabs.remove = async () => { exists = false; throw new Error("No tab with id: 1 (user closed it)"); };
    const result = await h.execute({ taskId: "direct", commandId: "manual-close", type: "close_direct_tab", requiresAuthorityCheck: true, tabId: 1, windowId: 1, expectedSessionId: "lifecycle-test" });
    assert.equal(result.ok, true, "verify the final tab set even when the user closes the target during validation");
    assert.equal(result.alreadyClosed, true);
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("same-task", 1);
    const cleanup = h.context.probe.endTask({ taskId: "same-task" });
    h.context.probe.ownTab("same-task", 1);
    await cleanup;
    assert.ok(!h.pageMessages.includes("end_task"), "a resumed lease cannot receive an old asynchronous cleanup");
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("same-task", 1);
    h.queue({ taskId: "same-task", commandId: "old", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const old = h.context.probe.pollOnce();
    await until(h.preparing);
    const finishOld = h.preparedReply();
    const heartbeat = h.context.probe.pollOnce();
    h.context.probe.resetNativePort("disconnect while preparing");
    assert.equal(h.context.probe.taskCount(), 0, "native reset immediately revokes the old leases");
    h.context.probe.connectNative();
    h.context.probe.ownTab("same-task", 1);
    await heartbeat;
    assert.equal(h.context.probe.taskCount(), 1, "an old heartbeat failure cannot clear a newly adopted lease");
    await flush();
    h.queue({ taskId: "same-task", commandId: "fresh", type: "click", tabId: 1,
      controlId: "g2c1", requiresAuthorityCheck: true });
    const fresh = h.context.probe.pollOnce();
    await until(() => h.preparedReply() !== finishOld);
    finishOld({ ok: true, resolutionToken: "old", commandAuthoritySupported: true });
    await old;
    assert.equal(h.context.probe.taskCount(), 1, "a late old-port failure cannot clear a replacement task lease");
    h.resumePrepare();
    await fresh;
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("task", 1);
    h.queue({ taskId: "task", commandId: "click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const pending = h.context.probe.pollOnce();
    await until(h.preparing);
    assert.equal((await h.rendererAuthority()).ok, true);
    for (const sender of [{ tab: { id: 2 } }, { frameId: 2 }, { id: "untrusted-extension" }])
      assert.equal((await h.rendererAuthority({}, sender)).ok, false, "only the owned renderer can request mutation authority");
    assert.equal((await h.rendererAuthority({ commandId: "old-command" })).ok, false);
    assert.equal((await h.rendererAuthority({ taskId: "old-task" })).ok, false);
    h.cancel();
    assert.equal((await h.rendererAuthority()).ok, false, "desktop cancellation revokes renderer authority immediately");
    assert.equal(h.context.probe.taskCount(), 0);
    h.resumePrepare();
    await pending;
    assert.ok(!h.pageMessages.includes("commit_action"));
  }
  {
    const h = harness();
    await flush();
    h.stallStatus();
    const pending = h.context.probe.pollOnce();
    await flush();
    await h.tickPoll();
    assert.ok(h.messages.some(message => message.type === "heartbeat"),
      "a blocked page must not block the native heartbeat");
    h.resumeStatus();
    await pending;
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("task", 1);
    h.queue({ taskId: "task", commandId: "click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const pending = h.context.probe.pollOnce();
    await until(h.preparing);
    h.cancel();
    await h.tickPoll();
    assert.equal(h.context.probe.taskCount(), 0, "cancellation must clear local task authority while preparation is blocked");
    assert.ok(h.pageMessages.includes("end_task"), "cancellation clears the task indicator without waiting for preparation");
    h.queue({ taskId: "fresh-task", commandId: "fresh", type: "activate_existing_tab",
      windowId: 1, text: "index:1", requiresAuthorityCheck: true });
    await h.context.probe.pollOnce();
    assert.equal(h.pageMessages.filter(type => type === "activate_tab:1").length, 1,
      "a new command must work while the cancelled page response is still pending");
    h.resumePrepare();
    await pending;
    assert.ok(!h.pageMessages.includes("commit_action"),
      "cancelled prepared work must never commit after the page responds");
    assert.ok(h.messages.some(message => message.type === "command_check"));
  }
  {
    const h = harness();
    await flush();
    h.stallStatus();
    const old = h.context.probe.pollOnce();
    await flush();
    h.context.probe.resetNativePort("test disconnect");
    h.context.probe.connectNative();
    await flush();
    h.context.probe.ownTab("new", 1);
    h.queue({ taskId: "new", commandId: "new-click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const current = h.context.probe.pollOnce();
    await until(h.preparing);
    h.resumeStatus();
    await old;
    assert.equal(h.context.probe.currentCommand(), "new-click",
      "old transport completion cannot clear the replacement command owner");
    h.resumePrepare();
    await current;
    assert.equal(h.pageMessages.filter(type => type === "commit_action").length, 1);
  }
  for (const requiresAuthorityCheck of [false, true]) {
    const h = harness();
    await flush();
    h.context.probe.ownTab("task", 1);
    h.queue({ taskId: "task", commandId: "click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck });
    const pending = h.context.probe.pollOnce();
    await until(h.preparing);
    h.resumePrepare();
    await pending;
    assert.equal(h.pageMessages.filter(type => type === "commit_action").length, 1,
      "both original and lease-aware desktop commands commit exactly once");
    assert.equal(h.messages.filter(message => message.type === "command_result").length, 1);
    assert.equal(h.messages.some(message => message.type === "command_check"), requiresAuthorityCheck);
  }
  {
    const h = harness();
    await flush();
    h.context.probe.ownTab("task", 1);
    h.queue({ taskId: "task", commandId: "click", type: "click", tabId: 1,
      controlId: "g1c1", requiresAuthorityCheck: true });
    const pending = h.context.probe.pollOnce();
    await until(h.preparing);
    h.resumePrepare(false);
    await pending;
    assert.ok(!h.pageMessages.includes("commit_action"), "a stale pre-update content script cannot bypass renderer cancellation");
    const result = h.messages.find(message => message.type === "command_result").result;
    assert.equal(result.ok, false);
    assert.match(result.error, /Refresh this tab/);
  }
  console.log("Helper heartbeat and cancelled preparation: PASS");
}
const watchdog = setTimeout(() => {
  console.error("Helper lifecycle test stalled before reaching its assertions.");
  process.exit(1);
}, 10000);
main().catch(error => { console.error(error); process.exitCode = 1; })
  .finally(() => clearTimeout(watchdog));
