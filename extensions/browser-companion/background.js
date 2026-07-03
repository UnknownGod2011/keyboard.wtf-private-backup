const HEADER_NAME = "X-Keyboard-Wtf-Companion";
const HEADER_VALUE = "keyboard-wtf-companion-v1";
const VERSION = chrome.runtime.getManifest().version;
const SESSION_ID_KEY = "keyboardWtfCompanionSessionId";
const PORTS = [8090, 8091, 8092, 8093, 8094, 8095];

let sessionId = "";
let bridgeBase = "";
let polling = false;
let lastRegisterAt = 0;
let state = {
  connected: false,
  lastHeartbeat: 0,
  lastGraphAt: 0,
  lastCommandAt: 0,
  lastCommandResult: "",
  lastError: "",
  activeTabUrl: "",
  lastGraphTabId: 0,
  lastGraphControlCount: 0,
  contentScriptVersion: "",
  browserName: "Unknown"
};

function randomId() {
  return crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

async function ensureSession() {
  if (sessionId) return sessionId;
  const stored = await chrome.storage.local.get(SESSION_ID_KEY);
  sessionId = stored[SESSION_ID_KEY] || randomId();
  await chrome.storage.local.set({ [SESSION_ID_KEY]: sessionId });
  return sessionId;
}

function browserName() {
  const ua = navigator.userAgent || "";
  if (/Edg\//i.test(ua)) return "Edge";
  if (/Chrome/i.test(ua)) return "Chrome";
  if (/Chromium/i.test(ua)) return "Chromium";
  return "Unknown";
}

function setConnected(connected, error = "") {
  state.connected = connected;
  if (connected) {
    state.lastHeartbeat = Date.now();
    state.lastError = "";
  } else if (error) {
    state.lastError = String(error).slice(0, 260);
  }
  updateBadge();
}

function updateBadge() {
  const text = state.connected ? "ON" : (state.lastError ? "OFF" : "...");
  const color = state.connected ? "#19c37d" : (state.lastError ? "#ef4444" : "#f59e0b");
  chrome.action.setBadgeText({ text }).catch(() => null);
  chrome.action.setBadgeBackgroundColor({ color }).catch(() => null);
}

async function activeTabUrl() {
  try {
    const tab = await activeTab();
    return tab?.url || "";
  } catch {
    return "";
  }
}

async function request(path, options = {}) {
  const bases = bridgeBase ? [bridgeBase, ...PORTS.map((port) => `http://localhost:${port}`)] : PORTS.map((port) => `http://localhost:${port}`);
  let lastError = null;
  for (const base of [...new Set(bases)]) {
    try {
      const response = await fetch(`${base}${path}`, {
        ...options,
        headers: {
          [HEADER_NAME]: HEADER_VALUE,
          "Content-Type": "application/json",
          ...(options.headers || {})
        }
      });
      if (!response.ok) {
        lastError = new Error(`HTTP ${response.status}`);
        continue;
      }
      bridgeBase = base;
      setConnected(true);
      return await response.json();
    } catch (error) {
      lastError = error;
    }
  }
  setConnected(false, lastError?.message || "keyboard.wtf bridge unavailable");
  throw lastError || new Error("keyboard.wtf bridge unavailable");
}

async function register() {
  await ensureSession();
  const now = Date.now();
  if (now - lastRegisterAt < 4000) return;
  state.browserName = browserName();
  state.activeTabUrl = await activeTabUrl();
  await request("/api/browser-companion/register", {
    method: "POST",
    body: JSON.stringify({
      sessionId,
      extensionVersion: VERSION,
      contentScriptVersion: state.contentScriptVersion || "",
      browserName: state.browserName,
      activeTabUrl: state.activeTabUrl
    })
  });
  lastRegisterAt = now;
}

async function activeTab() {
  const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
  return tabs[0] || null;
}

async function commandTab(taskId = "") {
  if (taskId && state.lastGraphTabId) {
    try {
      const tab = await chrome.tabs.get(state.lastGraphTabId);
      if (tab?.id && /^https?:/i.test(tab.url || "")) return tab;
    } catch {
      state.lastGraphTabId = 0;
    }
  }
  return await activeTab();
}

async function sendToTab(tabId, message) {
  if (!tabId) throw new Error("No browser tab is available.");
  try {
    return await chrome.tabs.sendMessage(tabId, message);
  } catch {
    await chrome.scripting.executeScript({ target: { tabId }, files: ["content.js"] });
    return await chrome.tabs.sendMessage(tabId, message);
  }
}

async function sendToActiveTab(message) {
  const tab = await activeTab();
  if (!tab?.id) throw new Error("No active browser tab is available.");
  return await sendToTab(tab.id, message);
}

async function focusBrowserTab(tabId = 0) {
  const tab = tabId ? await chrome.tabs.get(tabId) : await activeTab();
  if (!tab?.id) throw new Error("No active browser tab is available.");
  if (tab.windowId != null) await chrome.windows.update(tab.windowId, { focused: true }).catch(() => null);
  await chrome.tabs.update(tab.id, { active: true }).catch(() => null);
  return tab;
}

async function focusActiveBrowserTab() {
  return await focusBrowserTab(0);
}

function prop(object, camel, pascal) {
  return object?.[camel] ?? object?.[pascal];
}

async function trustedClick(tabId, bounds) {
  if (!tabId || !bounds || bounds.width <= 0 || bounds.height <= 0) {
    throw new Error("Trusted click needs a visible control bounding box.");
  }
  const target = { tabId };
  const x = Math.max(1, Math.round(bounds.x + bounds.width / 2));
  const y = Math.max(1, Math.round(bounds.y + bounds.height / 2));
  let attached = false;
  try {
    await chrome.debugger.attach(target, "1.3");
    attached = true;
  } catch (error) {
    const message = String(error?.message || error || "");
    if (!message.includes("Another debugger is already attached")) {
      throw error;
    }
  }

  try {
    await chrome.debugger.sendCommand(target, "Input.dispatchMouseEvent", {
      type: "mouseMoved",
      x,
      y,
      button: "none",
      clickCount: 0
    });
    await chrome.debugger.sendCommand(target, "Input.dispatchMouseEvent", {
      type: "mousePressed",
      x,
      y,
      button: "left",
      clickCount: 1
    });
    await chrome.debugger.sendCommand(target, "Input.dispatchMouseEvent", {
      type: "mouseReleased",
      x,
      y,
      button: "left",
      clickCount: 1
    });
  } finally {
    if (attached) {
      await chrome.debugger.detach(target).catch(() => null);
    }
  }
}

async function waitForTabReady(tabId, timeoutMs = 15000) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    const tab = await chrome.tabs.get(tabId);
    if (tab.status === "complete") return;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
}

async function waitForPostActionSettle(tabId, previousUrl, timeoutMs = 3000) {
  const started = Date.now();
  let sawNavigation = false;
  while (Date.now() - started < timeoutMs) {
    const tab = await chrome.tabs.get(tabId);
    if (tab.url && previousUrl && tab.url !== previousUrl) sawNavigation = true;
    if (sawNavigation && tab.status === "complete") {
      await new Promise((resolve) => setTimeout(resolve, 450));
      return;
    }
    if (!sawNavigation && Date.now() - started > 750) {
      await new Promise((resolve) => setTimeout(resolve, 250));
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 150));
  }
}

async function scanGraph(tabId = 0) {
  const response = tabId
    ? await sendToTab(tabId, { type: "keyboardWtfScan" })
    : await sendToActiveTab({ type: "keyboardWtfScan" });
  if (!response?.ok) throw new Error(response?.message || "Page scan failed.");
  return decorateGraph(response.graph);
}

async function scanActiveGraph() {
  return await scanGraph(0);
}

function decorateGraph(graph) {
  if (!graph) return graph;
  state.activeTabUrl = graph.url || graph.Url || state.activeTabUrl;
  const tabId = graph.tabId || graph.TabId || 0;
  if (tabId) state.lastGraphTabId = tabId;
  state.lastGraphAt = Date.now();
  state.lastGraphControlCount = graph.controls?.length || graph.Controls?.length || 0;
  state.contentScriptVersion = graph.contentScriptVersion || graph.ContentScriptVersion || state.contentScriptVersion || VERSION;
  state.browserName = graph.browserName || graph.BrowserName || state.browserName || browserName();
  return {
    ...graph,
    sessionId,
    extensionVersion: VERSION,
    contentScriptVersion: state.contentScriptVersion || VERSION,
    browserName: state.browserName || browserName()
  };
}

function graphMatchesTarget(graph, targetUrl) {
  if (!graph?.url || !targetUrl) return false;
  try {
    const actual = new URL(graph.url);
    const target = new URL(targetUrl);
    return actual.hostname === target.hostname
      && actual.pathname === target.pathname
      && (graph.controls?.length || 0) > 0;
  } catch {
    return false;
  }
}

async function waitForNavigationGraph(tabId, targetUrl, timeoutMs = 15000) {
  const started = Date.now();
  let lastGraph = null;
  while (Date.now() - started < timeoutMs) {
    try {
      const graph = await scanGraph(tabId);
      if (graphMatchesTarget(graph, targetUrl)) return graph;
      lastGraph = graph;
    } catch {
      // The new document may not have the content script yet.
    }

    const tab = await chrome.tabs.get(tabId);
    if (tab.status === "complete" && lastGraph && graphMatchesTarget(lastGraph, targetUrl)) return lastGraph;
    await new Promise((resolve) => setTimeout(resolve, 300));
  }
  return lastGraph;
}

async function waitForTabUrlMatch(tabId, targetUrl, timeoutMs = 8000) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    const tab = await chrome.tabs.get(tabId);
    try {
      const actual = new URL(tab.url || "");
      const target = new URL(targetUrl);
      if (actual.hostname === target.hostname && actual.pathname === target.pathname) return tab;
    } catch {
      // Keep polling until the tab reaches a normal URL.
    }
    await new Promise((resolve) => setTimeout(resolve, 150));
  }
  return await chrome.tabs.get(tabId);
}

async function runCommand(command) {
  const commandId = prop(command, "commandId", "CommandId");
  const commandSessionId = prop(command, "sessionId", "SessionId") || sessionId;
  const taskId = prop(command, "taskId", "TaskId") || "";
  const type = prop(command, "type", "Type");
  const url = prop(command, "url", "Url") || "";
  const controlId = prop(command, "controlId", "ControlId") || "";
  const text = prop(command, "text", "Text") || "";
  const started = Date.now();

  try {
    let graph = null;
    let focusedElement = null;
    let message = "Command completed.";
    state.lastCommandAt = Date.now();
    if (type === "navigate") {
      const tab = await activeTab();
      if (!tab?.id) throw new Error("No active browser tab is available.");
      state.lastGraphTabId = tab.id;
      await chrome.tabs.update(tab.id, { url });
      graph = await waitForNavigationGraph(tab.id, url, prop(command, "timeoutMs", "TimeoutMs") || 15000);
      if (!graph || !graphMatchesTarget(graph, url)) {
        await waitForTabReady(tab.id, prop(command, "timeoutMs", "TimeoutMs") || 15000);
        await new Promise((resolve) => setTimeout(resolve, 350));
        graph = await scanGraph(tab.id);
      }
      message = `Navigated to ${graph?.url || url}.`;
    } else if (type === "navigateFast") {
      const tab = await activeTab();
      if (!tab?.id) throw new Error("No active browser tab is available.");
      state.lastGraphTabId = tab.id;
      await chrome.tabs.update(tab.id, { url });
      const matched = await waitForTabUrlMatch(tab.id, url, prop(command, "timeoutMs", "TimeoutMs") || 8000);
      try {
        graph = await scanGraph(tab.id);
      } catch {
        graph = {
          sessionId,
          tabId: tab.id,
          url: matched.url || url,
          title: matched.title || "",
          pageType: (matched.url || url).includes("/watch") ? "youtube_watch" : "generic",
          routeKey: "",
          graphVersion: 0,
          capturedAt: new Date().toISOString(),
          controls: []
        };
      }
      if (!graphMatchesTarget(graph, url)) {
        graph = {
          ...(graph || {}),
          sessionId,
          tabId: tab.id,
          url: matched.url || url,
          title: matched.title || graph?.title || "",
          pageType: (matched.url || url).includes("/watch") ? "youtube_watch" : graph?.pageType || "generic",
          routeKey: graph?.routeKey || "",
          graphVersion: graph?.graphVersion || 0,
          capturedAt: new Date().toISOString(),
          controls: graph?.controls || []
        };
      }
      message = `Fast navigated to ${graph?.url || url}.`;
    } else if (type === "getGraph") {
      const tab = await commandTab(taskId);
      graph = await scanGraph(taskId ? tab?.id : 0);
      message = `Captured ${graph?.controls?.length || 0} controls.`;
    } else if (type === "click") {
      const tab = await commandTab(taskId);
      const previousUrl = tab?.url || "";
      const prepared = await sendToTab(tab?.id, { type: "keyboardWtfPrepareClick", controlId });
      if (!prepared?.ok) throw new Error(prepared?.message || "Click preparation failed.");
      let response = { ok: true, message: `Trusted-clicked ${controlId}.` };
      try {
        if (!tab?.id) throw new Error("No active browser tab is available.");
        await trustedClick(tab.id, prepared.bounds);
      } catch (trustedError) {
        response = await sendToTab(tab.id, { type: "keyboardWtfClick", controlId });
        if (!response?.ok) throw new Error(response?.message || trustedError?.message || "Click failed.");
        response.message = `${response.message || "Clicked control."} Trusted click fallback was unavailable: ${trustedError?.message || trustedError}`;
      }
      if (tab?.id) {
        await waitForPostActionSettle(tab.id, previousUrl, Math.min(prop(command, "timeoutMs", "TimeoutMs") || 3000, 5000));
      } else {
        await new Promise((resolve) => setTimeout(resolve, 750));
      }
      graph = await scanGraph(tab.id);
      message = response.message || "Clicked control.";
    } else if (type === "fill") {
      const tab = await commandTab(taskId);
      const response = await sendToTab(tab?.id, { type: "keyboardWtfFill", controlId, text });
      if (!response?.ok) throw new Error(response?.message || "Fill failed.");
      graph = await scanGraph(tab.id);
      message = response.message || "Filled control.";
    } else if (type === "scroll") {
      const tab = await commandTab(taskId);
      const response = await sendToTab(tab?.id, { type: "keyboardWtfScroll", direction: text || "down" });
      if (!response?.ok) throw new Error(response?.message || "Scroll failed.");
      await new Promise((resolve) => setTimeout(resolve, 450));
      graph = await scanGraph(tab.id);
      message = response.message || "Scrolled page.";
    } else if (type === "inspectFocusedElement") {
      const tab = await commandTab(taskId);
      await focusBrowserTab(tab?.id || 0);
      const response = await sendToTab(tab?.id, { type: "keyboardWtfInspectFocusedElement" });
      if (!response?.ok) throw new Error(response?.message || "Focused element inspection failed.");
      focusedElement = response.focusedElement || response.FocusedElement || null;
      graph = await scanGraph(tab.id).catch(() => null);
      message = focusedElement?.exists ? "Focused browser element inspected." : "No focused browser element.";
    } else if (type === "focusActiveTab") {
      const preferred = await commandTab(taskId);
      const tab = await focusBrowserTab(preferred?.id || 0);
      state.activeTabUrl = tab.url || "";
      graph = await scanActiveGraph().catch(() => ({
        sessionId,
        tabId: tab.id,
        url: tab.url || "",
        title: tab.title || "",
        extensionVersion: VERSION,
        contentScriptVersion: state.contentScriptVersion || "",
        browserName: state.browserName || browserName(),
        pageType: "generic",
        routeKey: "",
        graphVersion: 0,
        capturedAt: new Date().toISOString(),
        controls: []
      }));
      message = "Focused active browser tab.";
    } else {
      throw new Error(`Unsupported command type: ${type}`);
    }

    state.lastCommandResult = message;
    return {
      commandId,
      sessionId: commandSessionId,
      taskId,
      ok: true,
      status: "completed",
      message,
      runtimeMs: Date.now() - started,
      graph,
      focusedElement
    };
  } catch (error) {
    state.lastCommandResult = error?.message || "Browser Companion command failed.";
    state.lastError = state.lastCommandResult;
    updateBadge();
    return {
      commandId,
      sessionId: commandSessionId,
      taskId,
      ok: false,
      status: "failed",
      message: error?.message || "Browser Companion command failed.",
      runtimeMs: Date.now() - started
    };
  }
}

async function complete(result) {
  await request("/api/browser-companion/result", {
    method: "POST",
    body: JSON.stringify(result)
  });
  state.lastCommandResult = result?.message || "";
}

async function poll() {
  if (polling) return;
  polling = true;
  try {
    await register();
    await ensureSession();
    const response = await request(`/api/browser-companion/next-command?sessionId=${encodeURIComponent(sessionId)}`, { method: "GET" });
    const command = response?.command || response?.Command;
    if (command) {
      const result = await runCommand(command);
      await complete(result);
    }
  } catch {
    // Bridge may not be running yet. Polling is intentionally quiet.
  } finally {
    polling = false;
  }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "keyboardWtfGraph") {
    ensureSession()
      .then(register)
      .then(() => request("/api/browser-companion/graph", {
        method: "POST",
        body: JSON.stringify({
          sessionId,
          extensionVersion: VERSION,
          contentScriptVersion: message.graph?.contentScriptVersion || state.contentScriptVersion || VERSION,
          browserName: message.graph?.browserName || state.browserName || browserName(),
          graph: decorateGraph(message.graph)
        })
      }))
      .then(() => sendResponse({ ok: true }))
      .catch((error) => sendResponse({ ok: false, message: error?.message || "Graph post failed." }));
    return true;
  }
  if (message?.type === "keyboardWtfPopupStatus") {
    activeTabUrl()
      .then((url) => {
        state.activeTabUrl = url || state.activeTabUrl;
        sendResponse({
          ok: true,
          version: VERSION,
          contentScriptVersion: state.contentScriptVersion || "",
          bridgeBase,
          connected: state.connected,
          lastHeartbeat: state.lastHeartbeat,
          lastGraphAt: state.lastGraphAt,
          activeTabUrl: state.activeTabUrl,
          supported: /^https?:/i.test(state.activeTabUrl || ""),
          lastGraphControlCount: state.lastGraphControlCount,
          lastCommandAt: state.lastCommandAt,
          lastCommandResult: state.lastCommandResult,
          lastError: state.lastError,
          browserName: state.browserName || browserName()
        });
      })
      .catch((error) => sendResponse({ ok: false, message: error?.message || "Status failed." }));
    return true;
  }
  if (message?.type === "keyboardWtfPopupReconnect") {
    lastRegisterAt = 0;
    register()
      .then(() => sendResponse({ ok: true, connected: state.connected, bridgeBase }))
      .catch((error) => sendResponse({ ok: false, message: error?.message || "Reconnect failed." }));
    return true;
  }
  if (message?.type === "keyboardWtfPopupSendTestGraph") {
    scanActiveGraph()
      .then((graph) => request("/api/browser-companion/graph", {
        method: "POST",
        body: JSON.stringify({
          sessionId,
          extensionVersion: VERSION,
          contentScriptVersion: graph?.contentScriptVersion || VERSION,
          browserName: graph?.browserName || browserName(),
          graph
        })
      }).then(() => sendResponse({ ok: true, controls: graph?.controls?.length || 0, url: graph?.url || "" })))
      .catch((error) => sendResponse({ ok: false, message: error?.message || "Test graph failed." }));
    return true;
  }
  return false;
});

chrome.tabs.onActivated.addListener(() => {
  setTimeout(() => sendToActiveTab({ type: "keyboardWtfScan" }).catch(() => null), 350);
});

chrome.tabs.onUpdated.addListener((_tabId, changeInfo) => {
  if (changeInfo.status === "complete") {
    setTimeout(() => sendToActiveTab({ type: "keyboardWtfScan" }).catch(() => null), 700);
  }
});

setInterval(poll, 350);
setInterval(register, 5000);
ensureSession().then(register).catch(() => null);
