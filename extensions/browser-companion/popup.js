const $ = (id) => document.getElementById(id);

function formatTime(ms) {
  if (!ms) return "—";
  const date = new Date(ms);
  return Number.isNaN(date.getTime()) ? "—" : date.toLocaleTimeString();
}

function setStatus(data) {
  const connected = data?.connected === true;
  const waiting = !connected && !data?.lastError;
  $("statusDot").className = connected ? "dot ok" : waiting ? "dot" : "dot err";
  $("statusText").textContent = connected ? "Connected" : waiting ? "Waiting for keyboard.wtf" : "Disconnected";
  $("subtitle").textContent = connected
    ? "Ready to scan the active tab"
    : "Start keyboard.wtf or press Reconnect";
  $("version").textContent = data?.version || "—";
  $("contentVersion").textContent = data?.contentScriptVersion || "not injected yet";
  $("browserName").textContent = data?.browserName || "Unknown";
  $("bridge").textContent = data?.bridgeBase || "not found";
  $("heartbeat").textContent = formatTime(data?.lastHeartbeat);
  $("activeTab").textContent = data?.activeTabUrl || "—";
  $("controls").textContent = data?.lastGraphControlCount == null ? "—" : String(data.lastGraphControlCount);
  $("lastResult").textContent = data?.lastCommandResult || "—";
  $("error").textContent = data?.lastError || "";
}

async function request(type) {
  return await chrome.runtime.sendMessage({ type });
}

async function refresh() {
  try {
    const data = await request("keyboardWtfPopupStatus");
    setStatus(data);
  } catch (error) {
    setStatus({ connected: false, lastError: error?.message || "Popup status failed." });
  }
}

$("retry").addEventListener("click", async () => {
  $("error").textContent = "";
  const result = await request("keyboardWtfPopupReconnect").catch((error) => ({ ok: false, message: error?.message }));
  if (!result?.ok) $("error").textContent = result?.message || "Reconnect failed.";
  await refresh();
});

$("sendGraph").addEventListener("click", async () => {
  $("error").textContent = "";
  const result = await request("keyboardWtfPopupSendTestGraph").catch((error) => ({ ok: false, message: error?.message }));
  if (!result?.ok) {
    $("error").textContent = result?.message || "Test graph failed.";
  } else {
    $("lastResult").textContent = `Sent ${result.controls || 0} controls from ${result.url || "active tab"}`;
  }
  await refresh();
});

refresh();
