const VERSION = "2.1.21";
const PACKAGE_VERSION = chrome.runtime.getManifest().version;
const HOST = "wtf.keyboard.browser";
const BRIDGE_ALARM = "keyboard-wtf-native-bridge";
const NATIVE_RESPONSE_TIMEOUT_MS = 5000;
const taskTabs = new Map();
const taskActiveTab = new Map();
const pendingNative = [];
let nativePort = null;
let reconnectTimer = null;
let pollTimer = null;
let sessionId = crypto.randomUUID();
let lastError = "";
let pollBusy = false;
let lastNativeResponseAt = 0;
let nativeHandshakeReady = false;
const PUBLIC_SITE_ORIGINS = new Set([
  "https://keyboardwtf.com",
  "https://www.keyboardwtf.com",
  "https://keyboard-wtf.vercel.app"
]);

chrome.runtime.onInstalled.addListener(() => {
  chrome.action.setBadgeText({ text: "" });
  ensureBridgeAlarm();
  connectNative();
});

chrome.runtime.onStartup.addListener(() => {
  ensureBridgeAlarm();
  connectNative();
});

chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name !== BRIDGE_ALARM) return;
  wakeBridge();
});

chrome.tabs.onRemoved.addListener((tabId) => {
  for (const tabs of taskTabs.values()) tabs.delete(tabId);
  for (const [taskId, activeTab] of taskActiveTab.entries()) {
    if (activeTab === tabId) taskActiveTab.delete(taskId);
  }
});

chrome.tabs.onActivated.addListener(() => wakeBridge());

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "helper_content_heartbeat") {
    wakeBridge();
    sendResponse({ ok: true, version: VERSION });
    return false;
  }
  if (message?.type === "helper_popup_status") {
    sendResponse({
      ok: true,
      version: PACKAGE_VERSION,
      connected: Boolean(nativePort && nativeHandshakeReady),
      activeTasks: taskTabs.size,
      lastError
    });
    wakeBridge();
    return false;
  }
  return false;
});

chrome.runtime.onMessageExternal.addListener((message, sender, sendResponse) => {
  if (message?.type !== "keyboard_wtf_public_helper_status" || !isAllowedPublicSender(sender))
    return false;

  wakeBridge();
  Promise.resolve(collectHelperState())
    .then(helper => sendResponse({
      ok: true,
      installed: true,
      version: PACKAGE_VERSION,
      connected: Boolean(nativePort && nativeHandshakeReady),
      ready: Boolean(nativePort && nativeHandshakeReady && helper.commandChannelReady),
      extensionId: chrome.runtime.id
    }))
    .catch(() => sendResponse({
      ok: true,
      installed: true,
      version: PACKAGE_VERSION,
      connected: false,
      ready: false,
      extensionId: chrome.runtime.id
    }));
  return true;
});

function connectNative() {
  if (nativePort) return;
  clearTimeout(reconnectTimer);
  try {
    const port = chrome.runtime.connectNative(HOST);
    nativePort = port;
    nativeHandshakeReady = false;
    lastNativeResponseAt = Date.now();
    lastError = "";
    port.onMessage.addListener(handleNativeResponse);
    port.onDisconnect.addListener(() => {
      if (nativePort !== port) return;
      nativePort = null;
      nativeHandshakeReady = false;
      const message = chrome.runtime.lastError?.message;
      lastError = message || "Keyboard.wtf native bridge disconnected.";
      while (pendingNative.length) {
        const pending = pendingNative.shift();
        clearTimeout(pending.timer);
        pending.reject(new Error(lastError));
      }
      pollBusy = false;
      clearTimeout(pollTimer);
      clearAllIndicators();
      scheduleReconnect();
    });
    sendNative({ type: "hello", helper: baseHelperState() })
      .catch(() => {})
      .finally(schedulePoll);
  } catch (error) {
    lastError = String(error?.message || error);
    scheduleReconnect();
  }
}

function wakeBridge() {
  ensureBridgeAlarm();
  if (nativePort
      && pendingNative.length > 0
      && Date.now() - lastNativeResponseAt > NATIVE_RESPONSE_TIMEOUT_MS) {
    resetNativePort("Keyboard.wtf native bridge heartbeat was stale.");
  }
  connectNative();
  if (nativePort && !pollBusy) schedulePoll(0);
}

function resetNativePort(reason) {
  const stalePort = nativePort;
  nativePort = null;
  nativeHandshakeReady = false;
  lastError = reason || "Keyboard.wtf native bridge disconnected.";
  while (pendingNative.length) {
    const pending = pendingNative.shift();
    clearTimeout(pending.timer);
    pending.reject(new Error(lastError));
  }
  pollBusy = false;
  clearTimeout(pollTimer);
  try { stalePort?.disconnect(); } catch (_) {}
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connectNative, 1200);
}

function ensureBridgeAlarm() {
  // Alarms wake a terminated Manifest V3 worker so native-bridge recovery is
  // not dependent on a page, an active task, or an in-memory timeout surviving.
  chrome.alarms.create(BRIDGE_ALARM, { periodInMinutes: 0.5 });
}

function schedulePoll(delay = hasActiveTask() ? 220 : 900) {
  clearTimeout(pollTimer);
  pollTimer = setTimeout(pollOnce, delay);
}

async function pollOnce() {
  if (!nativePort || pollBusy) {
    schedulePoll();
    return;
  }

  pollBusy = true;
  try {
    const helper = await collectHelperState();
    const response = await sendNative({ type: "poll", helper });
    if (response?.command) {
      const result = await executeCommand(response.command);
      await sendNative({
        type: "command_result",
        taskId: response.command.taskId || "",
        commandId: response.command.commandId || "",
        helper: await collectHelperState(),
        result
      });
    }
  } catch (error) {
    lastError = String(error?.message || error);
  } finally {
    pollBusy = false;
    schedulePoll();
  }
}

function sendNative(message) {
  if (!nativePort) return Promise.reject(new Error(lastError || "Native bridge is not connected."));
  return new Promise((resolve, reject) => {
    const pending = {
      resolve,
      reject,
      timer: setTimeout(() => {
        const index = pendingNative.indexOf(pending);
        if (index >= 0) pendingNative.splice(index, 1);
        reject(new Error("Keyboard.wtf native bridge response timed out."));
        // Force the regular disconnect/reconnect path rather than allowing a
        // permanently pending poll to make the worker look healthy.
        try { nativePort?.disconnect(); } catch (_) {}
      }, NATIVE_RESPONSE_TIMEOUT_MS)
    };
    pendingNative.push(pending);
    try {
      nativePort.postMessage(message);
    } catch (error) {
      clearTimeout(pending.timer);
      const index = pendingNative.indexOf(pending);
      if (index >= 0) pendingNative.splice(index, 1);
      reject(error);
    }
  });
}

function handleNativeResponse(message) {
  const pending = pendingNative.shift();
  if (pending) {
    clearTimeout(pending.timer);
    lastNativeResponseAt = Date.now();
    nativeHandshakeReady = true;
    pending.resolve(message);
  }
}

function isAllowedPublicSender(sender) {
  try {
    return PUBLIC_SITE_ORIGINS.has(new URL(sender?.url || sender?.tab?.url || "").origin);
  } catch {
    return false;
  }
}

function baseHelperState() {
  return {
    extensionId: chrome.runtime.id,
    serviceWorkerVersion: VERSION,
    contentScriptVersion: "",
    sessionId,
    commandChannelReady: false,
    activeTabFresh: false,
    lastError
  };
}

async function collectHelperState() {
  const state = baseHelperState();
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab?.id) {
    state.lastError = lastError || "Chrome has no active tab.";
    return state;
  }

  // Service-worker-only commands such as exact tab activation remain safe and
  // useful even when the active tab is a protected chrome:// page. Page
  // automation remains unavailable until a normal http/https tab is active.
  state.windowId = tab.windowId;
  state.tabId = tab.id;
  state.activeUrl = tab.url || "";
  state.activeTitle = tab.title || "";
  if (!isNormalPage(tab.url)) {
    state.lastError = lastError || "Open a normal http/https page to use current-tab automation.";
    return state;
  }

  let status = await sendToTab(tab.id, { type: "helper_status" }, false);
  if (!status?.ok) {
    await tryInjectContentScript(tab.id);
    status = await sendToTab(tab.id, { type: "helper_status" }, false);
  }

  if (status?.ok) {
    state.contentScriptVersion = status.version || "";
    state.graphVersion = status.graphVersion || 0;
    state.domEpoch = status.domEpoch || "";
    state.controlCount = status.controlCount || 0;
    state.activeTabFresh = status.version === VERSION;
    state.commandChannelReady = state.activeTabFresh;
    state.lastError = lastError;
  } else {
    state.lastError = lastError || "Chrome Helper content script is not ready on the active tab.";
  }
  return state;
}

async function executeCommand(command) {
  try {
    switch (command.type) {
      case "begin_task":
        return await beginTask(command);
      case "observe":
        return await observe(command);
      case "open_tab":
        return await openTab(command);
      case "activate_tab":
        return await activateTab(command);
      case "activate_existing_tab":
        return await activateExistingTab(command);
      case "navigate":
        return await navigate(command);
      case "capture_screenshot":
        return await captureScreenshot(command);
      case "end_task":
      case "cancel_task":
        return await endTask(command);
      default:
        return await executePageAction(command);
    }
  } catch (error) {
    return {
      commandId: command.commandId || "",
      taskId: command.taskId || "",
      ok: false,
      error: String(error?.message || error),
      action: command.type || ""
    };
  }
}

async function beginTask(command) {
  const [current] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!current?.id || !isNormalPage(current.url)) {
    throw new Error("The active Chrome tab is not a normal http/https page.");
  }

  ownTab(command.taskId, current.id);
  taskActiveTab.set(command.taskId, current.id);
  await beginTaskFrames(command.taskId, current.id, "Planning task");
  return await scanTab(command.taskId, current.id);
}

async function observe(command) {
  const tabId = resolveTaskTab(command);
  // A normal navigation creates a fresh content-script world. Reassert the
  // existing task lease before returning the refreshed graph so the next
  // grounded action cannot be rejected as belonging to a stale page.
  await beginTaskFrames(command.taskId, tabId, "Reading updated page");
  return await scanTab(command.taskId, tabId);
}

async function openTab(command) {
  const current = await resolveCurrentTaskTab(command);
  const existing = await findReusableDomainTab(
    command.taskId,
    current?.windowId,
    command.url);
  const beforeUrl = current?.url || "";
  let tab = existing;
  if (!tab) {
    tab = await chrome.tabs.create({
      windowId: command.windowId || current?.windowId,
      url: command.url,
      active: true
    });
  } else {
    await chrome.tabs.update(tab.id, { active: true, url: command.url });
  }
  ownTab(command.taskId, tab.id);
  taskActiveTab.set(command.taskId, tab.id);
  await waitForTab(tab.id);
  await beginTaskFrames(command.taskId, tab.id, "Opening page");
  const snapshot = await scanTab(command.taskId, tab.id);
  return {
    commandId: command.commandId,
    taskId: command.taskId,
    ok: true,
    action: "open_tab",
    beforeUrl,
    afterUrl: snapshot.url,
    navigation: true,
    windowId: snapshot.windowId,
    tabId: snapshot.tabId,
    graphVersion: snapshot.graphVersion,
    domEpoch: snapshot.domEpoch
  };
}

async function activateTab(command) {
  const tabId = resolveTaskTab(command);
  const tab = await chrome.tabs.get(tabId);
  await chrome.tabs.update(tabId, { active: true });
  taskActiveTab.set(command.taskId, tabId);
  return {
    commandId: command.commandId,
    taskId: command.taskId,
    ok: true,
    action: "activate_tab",
    windowId: tab.windowId,
    tabId
  };
}

async function activateExistingTab(command) {
  const requestedWindowId = Number(command.windowId || 0);
  if (!requestedWindowId) {
    throw new Error("The Chrome window ID is unavailable.");
  }

  const tabs = (await chrome.tabs.query({ windowId: requestedWindowId }))
    .filter(tab => Number.isInteger(tab.id))
    .sort((left, right) => (left.index || 0) - (right.index || 0));
  if (tabs.length === 0) {
    throw new Error("The selected Chrome window has no tabs.");
  }

  const activeIndex = Math.max(0, tabs.findIndex(tab => tab.active));
  const selection = String(command.text || "").trim().toLowerCase();
  let targetIndex = -1;
  if (selection === "next") {
    targetIndex = (activeIndex + 1) % tabs.length;
  } else if (selection === "previous") {
    targetIndex = (activeIndex - 1 + tabs.length) % tabs.length;
  } else if (selection.startsWith("index:")) {
    targetIndex = Number(selection.slice("index:".length)) - 1;
  } else if (selection.startsWith("from_end:")) {
    targetIndex = tabs.length - Number(selection.slice("from_end:".length));
  }

  if (!Number.isInteger(targetIndex) || targetIndex < 0 || targetIndex >= tabs.length) {
    throw new Error(`That Chrome window has ${tabs.length} tabs, so the requested tab does not exist.`);
  }

  const before = tabs[activeIndex] || null;
  const target = tabs[targetIndex];
  await chrome.tabs.update(target.id, { active: true });
  return {
    commandId: command.commandId || "",
    taskId: command.taskId || "",
    ok: true,
    action: "activate_existing_tab",
    beforeUrl: before?.url || "",
    afterUrl: target.url || "",
    navigation: false,
    noVisibleChange: before?.id === target.id,
    windowId: target.windowId,
    tabId: target.id,
    tabCount: tabs.length
  };
}

async function navigate(command) {
  const tabId = resolveTaskTab(command);
  const before = await chrome.tabs.get(tabId);
  await chrome.tabs.update(tabId, { url: command.url });
  await waitForTab(tabId);
  await beginTaskFrames(command.taskId, tabId, "Reading page");
  const snapshot = await scanTab(command.taskId, tabId);
  return {
    commandId: command.commandId,
    taskId: command.taskId,
    ok: true,
    action: "navigate",
    beforeUrl: before.url || "",
    afterUrl: snapshot.url,
    navigation: before.url !== snapshot.url,
    windowId: snapshot.windowId,
    tabId,
    graphVersion: snapshot.graphVersion,
    domEpoch: snapshot.domEpoch
  };
}

async function captureScreenshot(command) {
  const tabId = resolveTaskTab(command);
  const tab = await chrome.tabs.get(tabId);
  const screenshotDataUrl = await captureTaskTab(tabId, tab.windowId, 72);
  return {
    commandId: command.commandId,
    taskId: command.taskId,
    ok: true,
    action: "capture_screenshot",
    windowId: tab.windowId,
    tabId,
    screenshotDataUrl
  };
}

async function executePageAction(command) {
  const tabId = resolveTaskTab(command);
  const frame = frameForCommand(command);
  const frameId = frame.frameId;
  const frameCommand = frame.command;
  const beforeTab = await chrome.tabs.get(tabId);
  // DOM/accessibility actions are addressed to the leased tab ID and do not
  // need Chrome to own the Windows foreground. Only native keyboard fallbacks
  // explicitly focus the tab first via a focus/focus_active_cursor command.
  if (requiresForeground(command)) {
    await chrome.windows.update(beforeTab.windowId, { focused: true });
    await chrome.tabs.update(tabId, { active: true });
  }
  await ensureContentScript(tabId, frameId);
  // Navigation may finish after the preceding observation. Reassert the lease
  // at the mutation boundary so a newly created content-script world can only
  // execute the still-active task that owns this exact tab.
  await sendToTab(tabId, {
    type: "begin_task",
    taskId: command.taskId,
    action: "Preparing action"
  }, true, frameId);
  const prepared = await sendToTab(tabId, {
    type: "prepare_action",
    command: frameCommand
  }, true, frameId);
  if (!prepared?.ok) return failureReceipt(command, prepared);

  let screenshotDataUrl = "";
  if (command.captureBeforeAction) {
    try {
      screenshotDataUrl = await captureTaskTab(tabId, beforeTab.windowId, 70);
    } catch {
      // This receipt image is optional. A transient permission or capture
      // failure must never prevent the already-grounded live action.
      screenshotDataUrl = "";
    }
  }

  let receipt;
  try {
    receipt = await sendToTab(tabId, {
      type: "commit_action",
      command: frameCommand,
      resolutionToken: prepared.resolutionToken
    }, true, frameId);
  } catch (error) {
    const afterTab = await chrome.tabs.get(tabId).catch(() => null);
    if (afterTab?.url && afterTab.url !== beforeTab.url) {
      await waitForTab(tabId).catch(() => {});
      receipt = {
        commandId: command.commandId || "",
        taskId: command.taskId || "",
        ok: true,
        action: command.type || "",
        targetId: command.controlId || "",
        targetRole: command.targetRole || prepared.targetRole || "",
        targetName: command.targetName || prepared.targetName || "",
        beforeUrl: beforeTab.url || "",
        afterUrl: afterTab.url,
        navigation: true,
        noVisibleChange: false,
        windowId: afterTab.windowId,
        tabId
      };
    } else {
      throw error;
    }
  }
  if (receipt) {
    receipt.targetId = command.controlId || receipt.targetId || "";
    receipt.frameId = frameId;
  }
  if (receipt && screenshotDataUrl) receipt.screenshotDataUrl = screenshotDataUrl;
  return receipt || failureReceipt(command, { error: "The content script returned no action receipt." });
}

function requiresForeground(command) {
  return command?.type === "focus"
    || command?.type === "focus_active_cursor";
}

async function captureTaskTab(tabId, windowId, quality) {
  const [previous] = await chrome.tabs.query({ active: true, windowId });
  const restoreTabId = previous?.id && previous.id !== tabId
    ? previous.id
    : 0;
  if (restoreTabId) await chrome.tabs.update(tabId, { active: true });
  try {
    return await chrome.tabs.captureVisibleTab(windowId, {
      format: "jpeg",
      quality
    });
  } finally {
    if (restoreTabId) {
      try { await chrome.tabs.update(restoreTabId, { active: true }); } catch {}
    }
  }
}

async function endTask(command) {
  const tabs = [...(taskTabs.get(command.taskId) || [])];
  await Promise.all(tabs.map(tabId => endTaskFrames(command.taskId, tabId)));
  taskTabs.delete(command.taskId);
  taskActiveTab.delete(command.taskId);
  await updateBadge();
  return {
    commandId: command.commandId,
    taskId: command.taskId,
    ok: true,
    cancelled: command.type === "cancel_task",
    action: command.type
  };
}

function ownTab(taskId, tabId) {
  if (!taskTabs.has(taskId)) taskTabs.set(taskId, new Set());
  taskTabs.get(taskId).add(tabId);
  updateBadge();
}

function resolveTaskTab(command) {
  const requested = Number(command.tabId || 0);
  const owned = taskTabs.get(command.taskId);
  if (requested && owned?.has(requested)) return requested;
  const current = taskActiveTab.get(command.taskId);
  if (current && owned?.has(current)) return current;
  throw new Error("The requested tab is not owned by this browser task.");
}

async function resolveCurrentTaskTab(command) {
  const tabId = resolveTaskTab(command);
  return await chrome.tabs.get(tabId);
}

async function findReusableDomainTab(taskId, windowId, url) {
  const target = safeUrl(url);
  if (!target) return null;
  for (const tabId of taskTabs.get(taskId) || []) {
    try {
      const tab = await chrome.tabs.get(tabId);
      const current = safeUrl(tab.url);
      if (current?.hostname === target.hostname) return tab;
    } catch {}
  }
  const tabs = await chrome.tabs.query({ windowId });
  const matches = tabs
    .filter(tab => isNormalPage(tab.url))
    .filter(tab => sameReusablePage(safeUrl(tab.url), target))
    .sort((a, b) => Number(b.active) - Number(a.active));
  if (matches.length) return matches[0];
  return null;
}

function sameReusablePage(current, target) {
  if (!current || !target) return false;
  const normalizePath = value => {
    const path = value || "/";
    return path.length > 1 ? path.replace(/\/+$/, "") : path;
  };
  return current.protocol === target.protocol
    && current.hostname === target.hostname
    && normalizePath(current.pathname) === normalizePath(target.pathname)
    && current.search === target.search;
}

async function scanTab(taskId, tabId) {
  const top = await scanFrame(taskId, tabId, 0);
  const frames = await normalChildFrames(tabId);
  const children = (await Promise.all(frames.map(async frame => {
    try {
      await beginTaskFrame(taskId, tabId, frame.frameId, "Reading embedded editor");
      return await scanFrame(taskId, tabId, frame.frameId);
    } catch {
      return null;
    }
  }))).filter(Boolean);
  const snapshot = mergeFrameSnapshots(top, children);
  if (!snapshot?.tabId) {
    const tab = await chrome.tabs.get(tabId);
    snapshot.tabId = tabId;
    snapshot.windowId = tab.windowId;
    snapshot.url = tab.url || "";
    snapshot.title = tab.title || "";
  }
  return snapshot;
}

async function scanFrame(taskId, tabId, frameId) {
  return await sendToTab(tabId, { type: "scan", taskId, frameId }, true, frameId);
}

async function beginTaskFrames(taskId, tabId, action) {
  await beginTaskFrame(taskId, tabId, 0, action);
  const frames = await normalChildFrames(tabId);
  await Promise.all(frames.map(frame =>
    beginTaskFrame(taskId, tabId, frame.frameId, action).catch(() => {})));
}

async function beginTaskFrame(taskId, tabId, frameId, action) {
  await ensureContentScript(tabId, frameId);
  await sendToTab(tabId, { type: "begin_task", taskId, action, frameId }, true, frameId);
}

async function endTaskFrames(taskId, tabId) {
  const frames = await normalChildFrames(tabId);
  await Promise.all([0, ...frames.map(frame => frame.frameId)].map(frameId =>
    sendToTab(tabId, { type: "end_task", taskId, frameId }, false, frameId)));
}

async function normalChildFrames(tabId) {
  try {
    const frames = await chrome.webNavigation.getAllFrames({ tabId });
    const byId = new Map((frames || []).map(frame => [frame.frameId, frame]));
    return (frames || []).filter(frame =>
      frame.frameId !== 0 && isEligibleChildFrame(frame, byId));
  } catch {
    return [];
  }
}

function isEligibleChildFrame(frame, framesById) {
  let current = frame;
  for (let depth = 0; current && depth < 16; depth += 1) {
    if (isNormalPage(current.url)) return true;
    if (!isInheritedOriginFrame(current.url)) return false;
    current = framesById.get(current.parentFrameId);
  }
  return false;
}

function isInheritedOriginFrame(url) {
  return /^(?:about:(?:blank|srcdoc)|data:|blob:|filesystem:)/i.test(url || "");
}

function scopedControlId(frameId, controlId) {
  return `f${Number(frameId || 0)}:${controlId || ""}`;
}

function frameForCommand(command) {
  const match = /^f(\d+):(.*)$/.exec(String(command?.controlId || ""));
  const frameId = match ? Number(match[1]) : 0;
  return {
    frameId,
    command: {
      ...command,
      controlId: match ? match[2] : (command?.controlId || "")
    }
  };
}

function mergeFrameSnapshots(top, children) {
  if (!top) return top;
  const all = [top, ...children];
  const withFrame = (snapshot, frameId) => (snapshot.controls || []).map(control => ({
    ...control,
    id: scopedControlId(frameId, control.id),
    frameId
  }));
  const controls = all.flatMap(snapshot => withFrame(snapshot, snapshot.frameId || 0));
  // Canvas-backed editors commonly keep their focused keyboard proxy in an
  // inherited-origin child frame and intentionally hide it from the visible
  // control graph. Preserve the frame lease even when that proxy has no
  // ordinary indexed control ID; the content script will re-resolve only the
  // live focused editable at mutation time.
  const active = all.find(snapshot => snapshot.activeEditable);
  const pageText = all
    .map(snapshot => String(snapshot.pageText || ""))
    .filter(Boolean)
    .join("\n")
    .slice(0, 14000);
  return {
    ...top,
    pageText,
    fingerprint: all.map(snapshot => snapshot.fingerprint || "").join("|"),
    domEpoch: all.map(snapshot => snapshot.domEpoch || "").join("|"),
    activeEditable: Boolean(active),
    activeEditorId: active
      ? scopedControlId(active.frameId || 0, active.activeEditorId || "__active_cursor__")
      : "",
    activeEditorRole: active?.activeEditorRole || "",
    activeEditorName: active?.activeEditorName || "",
    controls
  };
}

async function ensureContentScript(tabId, frameId = 0) {
  const status = await sendToTab(tabId, { type: "helper_status" }, false, frameId);
  if (status?.ok && status.version === VERSION) return;
  await tryInjectContentScript(tabId, frameId);
  const retry = await sendToTab(tabId, { type: "helper_status" }, false, frameId);
  if (!retry?.ok || retry.version !== VERSION) {
    throw new Error(`Chrome Helper content script is unavailable or stale. Expected ${VERSION}.`);
  }
}

async function tryInjectContentScript(tabId, frameId = 0) {
  try {
    await chrome.scripting.executeScript({
      target: { tabId, frameIds: [frameId] },
      files: ["content-script.js"]
    });
  } catch {}
}

async function sendToTab(tabId, message, throwOnError = true, frameId = 0) {
  try {
    return await chrome.tabs.sendMessage(tabId, message, { frameId });
  } catch (error) {
    if (throwOnError) throw error;
    return null;
  }
}

async function waitForTab(tabId, timeoutMs = 15000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const tab = await chrome.tabs.get(tabId);
    if (tab.status === "complete" && isNormalPage(tab.url)) return;
    await delay(180);
  }
  throw new Error("The page did not finish loading in time.");
}

async function clearAllIndicators() {
  const work = [];
  for (const [taskId, tabs] of taskTabs.entries()) {
    for (const tabId of tabs) {
      work.push(endTaskFrames(taskId, tabId));
    }
  }
  taskTabs.clear();
  taskActiveTab.clear();
  await Promise.all(work);
  await updateBadge();
}

async function updateBadge() {
  const count = [...taskTabs.values()].reduce((sum, tabs) => sum + tabs.size, 0);
  await chrome.action.setBadgeBackgroundColor({ color: "#f59e0b" });
  await chrome.action.setBadgeText({ text: count ? String(count) : "" });
}

function hasActiveTask() {
  return taskTabs.size > 0;
}

function failureReceipt(command, result) {
  return {
    commandId: command.commandId || "",
    taskId: command.taskId || "",
    ok: false,
    stale: Boolean(result?.stale),
    error: result?.error || "The requested page action could not be prepared.",
    action: command.type || ""
  };
}

function isNormalPage(url) {
  return /^https?:\/\//i.test(url || "");
}

function safeUrl(url) {
  try {
    const parsed = new URL(url);
    return /^https?:$/.test(parsed.protocol) ? parsed : null;
  } catch {
    return null;
  }
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

ensureBridgeAlarm();
wakeBridge();
