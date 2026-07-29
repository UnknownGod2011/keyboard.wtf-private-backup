const statusDot = document.getElementById("status-dot");
const statusTitle = document.getElementById("status-title");
const statusDetail = document.getElementById("status-detail");

chrome.runtime.sendMessage({ type: "helper_popup_status" })
  .then(renderStatus)
  .catch(error => renderStatus({
    ok: false,
    lastError: String(error?.message || error)
  }));

function renderStatus(status) {
  statusDot.className = "status-dot";
  if (!status?.ok || !status.connected) {
    statusDot.classList.add("disconnected");
    statusTitle.textContent = "Jarvis is not connected";
    statusDetail.textContent = status?.lastError
      || "Open the keyboard.wtf app, then this helper reconnects automatically.";
    return;
  }

  if (Number(status.activeTasks || 0) > 0) {
    statusDot.classList.add("working");
    statusTitle.textContent = "Jarvis is controlling Chrome";
    statusDetail.textContent = `${status.activeTasks} task tab${status.activeTasks === 1 ? "" : "s"} active · Helper ${status.version}`;
    return;
  }

  statusDot.classList.add("connected");
  statusTitle.textContent = "Connected and ready";
  statusDetail.textContent = `Waiting for a browser task · Helper ${status.version}`;
}
