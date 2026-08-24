const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function event() {
  const listeners = [];
  return {
    listeners,
    addListener(listener) {
      listeners.push(listener);
    }
  };
}

function port({ failPost = false } = {}) {
  return {
    onMessage: event(),
    onDisconnect: event(),
    messages: [],
    disconnectCount: 0,
    postMessage(message) {
      if (failPost) throw new Error("Attempting to use a disconnected port object");
      this.messages.push(message);
    },
    disconnect() {
      this.disconnectCount += 1;
    }
  };
}

async function main() {
  const workerPath = path.join(__dirname, "..", "service-worker.js");
  const worker = fs.readFileSync(workerPath, "utf8");
  const deadPort = port({ failPost: true });
  const replacementPort = port();
  const scheduled = new Map();
  let nextTimer = 1;
  let connectAttempts = 0;

  const runtimeMessage = event();
  const chrome = {
    action: { setBadgeText() {} },
    alarms: { create() {}, onAlarm: event() },
    runtime: {
      getManifest: () => ({ version: "2.1.25" }),
      connectNative() {
        connectAttempts += 1;
        return connectAttempts === 1 ? deadPort : replacementPort;
      },
      onInstalled: event(),
      onStartup: event(),
      onMessage: runtimeMessage,
      onMessageExternal: event(),
      lastError: null,
      id: "gojdebdikepkpapmnggeljneamjdigbh"
    },
    tabs: {
      onRemoved: event(),
      onActivated: event()
    }
  };

  const context = {
    chrome,
    console,
    crypto: { randomUUID: () => "reconnect-test-session" },
    URL,
    Map,
    Set,
    Promise,
    Date,
    setTimeout(callback, delay) {
      const id = nextTimer++;
      scheduled.set(id, { callback, delay });
      return id;
    },
    clearTimeout(id) {
      scheduled.delete(id);
    }
  };

  vm.runInNewContext(worker, context, { filename: workerPath });
  await Promise.resolve();
  await Promise.resolve();

  assert.equal(connectAttempts, 1, "the initial native connection should be attempted once");
  assert.equal(deadPort.disconnectCount, 1, "the synchronously dead port must be discarded");

  const reconnect = [...scheduled.values()].find(timer => timer.delay === 1200);
  assert.ok(reconnect, "a bounded reconnect must be scheduled after the stale-port exception");
  reconnect.callback();
  assert.equal(connectAttempts, 2, "the worker must create a replacement native port");
  assert.equal(replacementPort.messages[0]?.type, "hello", "the replacement port must restart the handshake");

  replacementPort.onMessage.listeners[0]({ ok: true });
  await Promise.resolve();
  await Promise.resolve();

  let popupStatus = null;
  runtimeMessage.listeners[0]({ type: "helper_popup_status" }, {}, response => {
    popupStatus = response;
  });
  assert.equal(popupStatus?.connected, true, "the popup must report the recovered handshake");
  assert.equal(popupStatus?.lastError, "", "the stale disconnected-port error must be cleared");
}

main().then(
  () => console.log("Native reconnect regression: PASS"),
  error => {
    console.error(error);
    process.exitCode = 1;
  }
);
