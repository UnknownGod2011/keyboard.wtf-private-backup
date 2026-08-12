import { auth, googleSignIn, api, listenForAuth } from "./app.js";

const params = new URLSearchParams(location.search);
const attempt = params.get("attempt");
const state = params.get("state");
const nonce = params.get("nonce");
const status = document.getElementById("status");
const button = document.getElementById("approve");
const valid = attempt && state?.length >= 32 && nonce?.length >= 32;

if (!valid) {
  status.textContent = "This desktop sign-in request is invalid. Return to Jarvis and start again.";
} else {
  button.disabled = false;
}

listenForAuth((user) => {
  if (user && valid) {
    status.textContent = `Ready to connect as ${user.email}.`;
    button.textContent = "Connect Jarvis";
  }
});

button.addEventListener("click", async () => {
  button.disabled = true;
  try {
    if (!auth.currentUser) await googleSignIn();
    status.textContent = "Authorising this one-time desktop connection…";
    const result = await api("/v1/desktop-login/authorize", {
      method: "POST",
      body: JSON.stringify({ attemptId: attempt, state, deviceNonce: nonce }),
    });
    status.textContent = "Approved. Returning to Jarvis…";
    location.replace(result.callbackUrl);
  } catch (error) {
    status.textContent = error.message;
    button.disabled = false;
  }
});
