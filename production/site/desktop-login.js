import {
  auth,
  googleSignIn,
  api,
  waitForAuthInitialization,
} from "./app.js";

const params = new URLSearchParams(location.search);
const attempt = params.get("attempt");
const state = params.get("state");
const nonce = params.get("nonce");
const status = document.getElementById("status");
const button = document.getElementById("approve");
const valid = attempt && state?.length >= 32 && nonce?.length >= 32;
let authorizationPromise;

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
    location.replace(result.callbackUrl);
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
    status.textContent = error.message || "Google sign-in could not be checked. Retry.";
    button.textContent = "Retry";
    button.disabled = false;
  }
}

await initialize();
