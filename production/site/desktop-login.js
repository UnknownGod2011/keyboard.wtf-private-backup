import {
  auth,
  completeGoogleRedirect,
  googleSignIn,
  switchGoogleAccount,
  api,
  waitForAuthInitialization,
} from "./app.js";

const params = new URLSearchParams(location.search);
const attempt = params.get("attempt");
const state = params.get("state");
const nonce = params.get("nonce");
const switchRequested = params.get("switch") === "1";
const status = document.getElementById("status");
const button = document.getElementById("approve");
const valid = attempt && state?.length >= 32 && nonce?.length >= 32;
let authorizationPromise;
const switchMarker = `keyboardWtf.desktopAccountSwitch.${attempt || "invalid"}`;

function deliverToJarvis(callbackUrl) {
  const callback = new URL(callbackUrl);
  if (callback.protocol !== "http:"
      || (callback.hostname !== "127.0.0.1" && callback.hostname !== "localhost")
      || callback.pathname !== "/keyboard-wtf/callback")
    throw new Error("Jarvis returned an invalid local callback.");

  return new Promise((resolve, reject) => {
    const iframe = document.createElement("iframe");
    iframe.hidden = true;
    iframe.title = "Jarvis connection callback";
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error("Jarvis did not answer the local connection. Return to Settings and choose Try again."));
    }, 12_000);
    const cleanup = () => {
      clearTimeout(timer);
      window.removeEventListener("message", receive);
      iframe.remove();
    };
    const receive = (event) => {
      if (event.source !== iframe.contentWindow
          || event.origin !== callback.origin
          || event.data?.type !== "keyboard-wtf-desktop-callback")
        return;
      cleanup();
      if (event.data.success === true) resolve();
      else reject(new Error("Jarvis rejected this connection. Return to Settings and try again."));
    };
    window.addEventListener("message", receive);
    document.body.append(iframe);
    iframe.src = callback.href;
  });
}

async function authorizeDesktop() {
  if (authorizationPromise) return authorizationPromise;
  authorizationPromise = (async () => {
    status.textContent = "Connecting Jarvis...";
    button.disabled = true;
    const result = await api("/v1/desktop-login/authorize", {
      authenticated: true,
      method: "POST",
      body: JSON.stringify({ attemptId: attempt, state, deviceNonce: nonce }),
    });
    status.textContent = "Google sign-in approved. Finishing the secure Jarvis connection...";
    await deliverToJarvis(result.callbackUrl);
    status.textContent = "Jarvis connected successfully. You can close this tab.";
    button.hidden = true;
  })();

  try {
    await authorizationPromise;
  } catch (error) {
    status.textContent = error.message;
    button.disabled = false;
    button.textContent = auth.currentUser ? "Retry connection" : "Sign in with Google";
    authorizationPromise = undefined;
  }
}

button.addEventListener("click", async () => {
  if (!valid || authorizationPromise) return;
  button.disabled = true;
  try {
    if (!auth.currentUser) {
      status.textContent = "Signing in with Google...";
      await googleSignIn();
    }
    await authorizeDesktop();
  } catch (error) {
    status.textContent = error.message;
    button.disabled = false;
    button.textContent = auth.currentUser ? "Retry connection" : "Sign in with Google";
  }
});

async function initialize() {
  if (!valid) {
    status.textContent = "This desktop sign-in request is invalid. Return to Jarvis and try again.";
    return;
  }

  status.textContent = "Checking sign-in...";
  try {
    if (switchRequested) {
      if (sessionStorage.getItem(switchMarker) !== "redirecting") {
        sessionStorage.setItem(switchMarker, "redirecting");
        status.textContent = "Opening the Google account chooser...";
        await switchGoogleAccount();
        return;
      }
      await completeGoogleRedirect();
      sessionStorage.removeItem(switchMarker);
    }
    const user = await waitForAuthInitialization();
    if (user) {
      status.textContent = `Signed in as ${user.email}. Connecting Jarvis...`;
      await authorizeDesktop();
      return;
    }
    status.textContent = "Sign in with Google to connect this Jarvis device.";
    button.textContent = "Sign in with Google";
    button.disabled = false;
  } catch (error) {
    sessionStorage.removeItem(switchMarker);
    status.textContent = error.message || "Google sign-in could not be checked. Retry.";
    button.textContent = "Retry";
    button.disabled = false;
  }
}

await initialize();
