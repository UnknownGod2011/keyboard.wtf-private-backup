const STORE_EXTENSION_ID = "gojdebdikepkpapmnggeljneamjdigbh";
const FALLBACK_EXTENSION_ID = "gfndpohemndahmmeealkdobepjacblnf";
const LATEST_PACKAGE_VERSION = "2.1.25";
const SUPPORTED_PACKAGE_VERSIONS = new Set(["2.1.21", "2.1.24", "2.1.25"]);
const state = document.getElementById("chromeHelperState");
const detail = document.getElementById("chromeHelperDetail");
const check = document.getElementById("checkChromeHelper");

function queryExtension(extensionId) {
  return new Promise((resolve) => {
    if (!globalThis.chrome?.runtime?.sendMessage) {
      resolve(null);
      return;
    }
    let finished = false;
    const timer = setTimeout(() => {
      if (!finished) resolve(null);
      finished = true;
    }, 2200);
    try {
      chrome.runtime.sendMessage(extensionId, { type: "keyboard_wtf_public_helper_status" }, (response) => {
        if (finished) return;
        finished = true;
        clearTimeout(timer);
        resolve(chrome.runtime.lastError ? null : response || null);
      });
    } catch {
      finished = true;
      clearTimeout(timer);
      resolve(null);
    }
  });
}

async function refreshChromeHelperStatus() {
  if (!state || !detail) return;
  state.textContent = "Connecting…";
  detail.textContent = "Checking the Store helper and its live Jarvis native connection.";
  check?.setAttribute("disabled", "");
  try {
    const [store, fallback] = await Promise.all([
      queryExtension(STORE_EXTENSION_ID),
      queryExtension(FALLBACK_EXTENSION_ID)
    ]);
    const connected = [store, fallback].filter(result => result?.connected === true && result?.ready === true);
    if (connected.length > 1) {
      state.textContent = "Connected — two helpers enabled";
      detail.textContent = "Disable the unpacked fallback and keep only the Chrome Web Store helper enabled.";
    } else if (connected.length === 1) {
      const result = connected[0];
      const source = result.extensionId === STORE_EXTENSION_ID ? "Chrome Web Store" : "unpacked fallback";
      state.textContent = "Connected";
      detail.textContent = `${source} helper ${result.version} completed a live handshake with Jarvis.`;
    } else {
      const installed = store || fallback;
      if (!installed) {
        state.textContent = "Not installed / Not connected";
        detail.textContent = "Install the helper, keep Jarvis running, then check again. Chrome may require one page refresh after installation.";
      } else if (!SUPPORTED_PACKAGE_VERSIONS.has(installed.version)) {
        state.textContent = "Update required";
        detail.textContent = `Helper ${installed.version || "unknown"} is installed; update to ${LATEST_PACKAGE_VERSION}.`;
      } else {
        state.textContent = "Not connected";
        detail.textContent = "The helper is installed but did not complete a live handshake. Start Jarvis, then use Retry/reconnect in Settings.";
      }
    }
  } finally {
    check?.removeAttribute("disabled");
  }
}

check?.addEventListener("click", refreshChromeHelperStatus);
window.addEventListener("focus", refreshChromeHelperStatus);
void refreshChromeHelperStatus();
