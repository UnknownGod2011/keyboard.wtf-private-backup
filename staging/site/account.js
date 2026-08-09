import {
  auth,
  listenForAuth,
  googleSignIn,
  googleSignOut,
  loadAccount,
  loadCatalog,
  revokeDevice,
  deleteAccountPermanently,
  grantTestCredits,
  beginCheckout,
  waitForAuthoritativePurchase,
  getPendingPurchaseIds,
  trackServerPendingPurchases,
  formatCredits,
  formatMoney,
} from "./app.js";

const byId = (id) => document.getElementById(id);
let authEpoch = 0;
let renderInFlight = false;
let lastPassiveRefreshAt = 0;
let resumedPendingEpoch = -1;
const setStatus = (text, kind = "") => {
  byId("status").textContent = text;
  byId("status").className = `status ${kind}`;
};
const escape = (value) => String(value ?? "").replace(
  /[&<>"']/g,
  (character) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#39;",
  })[character],
);

function clearAccountUi() {
  byId("identity").textContent = "—";
  byId("plan").textContent = "No paid plan";
  byId("balance").textContent = "0";
  byId("balanceBar").style.width = "0";
  byId("usage").textContent = "0 used this cycle";
  byId("allowanceBreakdown").textContent = "Subscription 0 · top-ups 0";
  byId("subscription").textContent = "None";
  byId("renewal").textContent = "No renewal scheduled.";
  byId("usageList").replaceChildren();
  byId("purchaseList").replaceChildren();
  byId("deviceList").replaceChildren();
  byId("products").replaceChildren();
  byId("adminGrant").classList.add("hidden");
  byId("grantEmail").value = "";
}

async function render(expectedUser, epoch, quiet = false) {
  try {
    if (!quiet)
      setStatus("Signed in. Loading your authoritative account…");
    const [account, catalog] = await Promise.all([loadAccount(), loadCatalog()]);
    if (epoch !== authEpoch || auth.currentUser?.uid !== expectedUser.uid)
      return;
    trackServerPendingPurchases(account);
    byId("identity").textContent = `${account.displayName || "Google user"} · ${account.email}`;
    byId("plan").textContent = account.planId === "none" ? "No paid plan" : account.planId;
    byId("balance").textContent = formatCredits(account.availableCreditMicros);
    const total = Math.max(1, account.availableCreditMicros + account.cycleUsageCreditMicros);
    byId("balanceBar").style.width = `${Math.max(0, Math.min(100, account.availableCreditMicros / total * 100))}%`;
    byId("usage").textContent = `${formatCredits(account.cycleUsageCreditMicros)} credits used this cycle`;
    byId("allowanceBreakdown").textContent = `Subscription ${formatCredits(account.subscriptionAllowanceCreditMicros)} · top-ups ${formatCredits(account.topUpCreditMicros)}${account.otherAllowanceCreditMicros ? ` · other ${formatCredits(account.otherAllowanceCreditMicros)}` : ""}`;
    byId("subscription").textContent = account.subscriptionStatus || "none";
    byId("adminGrant").classList.toggle("hidden", !account.isAdmin);
    if (account.isAdmin && !byId("grantEmail").value)
      byId("grantEmail").value = account.email;
    byId("renewal").textContent = account.cycleEndsAt
      ? `Current cycle ends ${new Date(account.cycleEndsAt).toLocaleDateString()}.`
      : "No renewal scheduled.";
    byId("usageList").innerHTML = account.recentUsage?.length
      ? account.recentUsage.map((row) => `<li><span>${escape(row.model)}<br><span class="small">${escape(row.outcome)}</span></span><strong>${formatCredits(row.chargedCreditMicros)}</strong></li>`).join("")
      : "<li>No usage yet.</li>";
    byId("purchaseList").innerHTML = account.recentPurchases?.length
      ? account.recentPurchases.map((row) => `<li><span>${escape(row.productSku)}<br><span class="small">${escape(row.status)}</span></span><strong>${formatMoney(row.amountMinor, row.currency)}</strong></li>`).join("")
      : "<li>No purchases yet.</li>";
    byId("deviceList").innerHTML = account.devices?.length
      ? account.devices.map((row) => `<li><span>${escape(row.name)}<br><span class="small">${escape(row.status)} · last seen ${new Date(row.lastSeenAt).toLocaleString()}</span></span><button class="secondary revoke" data-device="${escape(row.deviceId)}">Remove</button></li>`).join("")
      : "<li>No desktop devices registered.</li>";
    document.querySelectorAll(".revoke").forEach((button) => button.addEventListener("click", async () => {
      button.disabled = true;
      try {
        await revokeDevice(button.dataset.device);
        if (auth.currentUser)
          await refreshSignedIn(auth.currentUser, authEpoch);
      } catch (error) {
        setStatus(error.message, "error");
      }
    }));
    byId("products").innerHTML = catalog.products.map((product) => `<article class="card"><div class="eyebrow">${escape(product.kind)}</div><h2>${escape(product.displayName)}</h2><div class="balance" style="font-size:32px">${formatMoney(product.amountMinor, product.currency)}</div><button class="buy" data-sku="${escape(product.sku)}">${product.kind === "subscription" ? "Upgrade" : "Add credits"}</button></article>`).join("");
    document.querySelectorAll(".buy").forEach((button) => button.addEventListener("click", async () => {
      button.disabled = true;
      try {
        const product = catalog.products.find((item) => item.sku === button.dataset.sku);
        setStatus("Opening Razorpay Test Mode…");
        const purchaseId = await beginCheckout(product);
        setStatus("Payment accepted by Checkout. Finalizing with Razorpay; this page will update automatically…");
        await waitForAuthoritativePurchase(purchaseId);
        setStatus("Payment confirmed by the server.", "success");
        if (auth.currentUser)
          await refreshSignedIn(auth.currentUser, authEpoch);
      } catch (error) {
        setStatus(error.message, "error");
      } finally {
        button.disabled = false;
      }
    }));
    if (!quiet) {
      setStatus(
        account.availableCreditMicros > 0
          ? "Account active."
          : "Signed in. This account has zero usable credits.",
        account.availableCreditMicros > 0 ? "success" : "",
      );
    }
  } catch (error) {
    if (epoch !== authEpoch || auth.currentUser?.uid !== expectedUser.uid)
      return;
    if (error.code === "backend-auth-rejected") {
      setStatus("Google sign-in succeeded, but the backend rejected this session. Sign out, then sign in again.", "error");
      return;
    }
    setStatus(error.message, "error");
  }
}

async function refreshSignedIn(user, epoch, quiet = false) {
  if (renderInFlight || !user || epoch !== authEpoch)
    return;
  renderInFlight = true;
  try {
    await render(user, epoch, quiet);
  } finally {
    renderInFlight = false;
  }
}

function refreshAfterReturningToPage() {
  if (document.hidden || !auth.currentUser || Date.now() - lastPassiveRefreshAt < 5_000)
    return;
  lastPassiveRefreshAt = Date.now();
  void refreshSignedIn(auth.currentUser, authEpoch, true);
  void resumePendingPurchaseVerification();
}

async function resumePendingPurchaseVerification() {
  const user = auth.currentUser;
  const epoch = authEpoch;
  if (!user || resumedPendingEpoch === epoch) return;
  const purchaseIds = getPendingPurchaseIds();
  if (!purchaseIds.length) return;
  resumedPendingEpoch = epoch;
  setStatus("Finishing a recent payment with Razorpay. This page will update automatically…");
  for (const purchaseId of purchaseIds) {
    if (auth.currentUser?.uid !== user.uid || epoch !== authEpoch) return;
    try {
      await waitForAuthoritativePurchase(purchaseId);
      if (auth.currentUser?.uid !== user.uid || epoch !== authEpoch) return;
      setStatus("Payment confirmed by the server.", "success");
      await refreshSignedIn(user, epoch, true);
    } catch (error) {
      if (auth.currentUser?.uid !== user.uid || epoch !== authEpoch) return;
      if (error.code === "payment-pending") {
        setStatus(error.message);
        return;
      }
      setStatus(error.message, "error");
    }
  }
}

byId("signIn").addEventListener("click", async () => {
  byId("signIn").disabled = true;
  setStatus("Opening Google sign-in…");
  try {
    await googleSignIn();
  } catch (error) {
    const cancelled = ["auth/popup-closed-by-user", "auth/cancelled-popup-request"].includes(error.code);
    setStatus(cancelled ? "Google sign-in was cancelled." : error.message, cancelled ? "" : "error");
  } finally {
    byId("signIn").disabled = false;
  }
});
byId("signOut").addEventListener("click", () => googleSignOut().catch((error) => setStatus(error.message, "error")));
byId("deleteAccount").addEventListener("click", async () => {
  const confirmation = window.prompt("This permanently closes your keyboard.wtf account and revokes every device. Type DELETE to continue.");
  if (confirmation !== "DELETE") {
    setStatus("Account deletion was cancelled.");
    return;
  }
  const button = byId("deleteAccount");
  button.disabled = true;
  setStatus("Confirm your Google identity to delete this account…");
  try {
    await deleteAccountPermanently();
    setStatus("Your keyboard.wtf account was deleted.", "success");
  } catch (error) {
    setStatus(error.message, "error");
  } finally {
    button.disabled = false;
  }
});
byId("refreshAccount").addEventListener("click", async () => {
  const button = byId("refreshAccount");
  button.disabled = true;
  try {
    await refreshSignedIn(auth.currentUser, authEpoch);
  } finally {
    button.disabled = false;
  }
});
window.addEventListener("focus", refreshAfterReturningToPage);
document.addEventListener("visibilitychange", refreshAfterReturningToPage);
byId("grantForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = byId("grantSubmit");
  button.disabled = true;
  try {
    const credits = Number(byId("grantCredits").value);
    if (!Number.isFinite(credits) || credits <= 0 || credits > 10000)
      throw new Error("Credits must be between 0.01 and 10,000.");
    await grantTestCredits(
      byId("grantEmail").value.trim(),
      Math.round(credits * 1_000_000),
      byId("grantReason").value.trim(),
    );
    setStatus("Audited staging credits granted.", "success");
    if (auth.currentUser)
      await refreshSignedIn(auth.currentUser, authEpoch);
  } catch (error) {
    setStatus(error.message, "error");
  } finally {
    button.disabled = false;
  }
});
listenForAuth((user) => {
  const epoch = ++authEpoch;
  clearAccountUi();
  byId("signedOut").classList.toggle("hidden", !!user);
  byId("signedIn").classList.toggle("hidden", !user);
  byId("signOut").classList.toggle("hidden", !user);
  byId("refreshAccount").classList.toggle("hidden", !user);
  if (user) {
    void refreshSignedIn(user, epoch).then(() => resumePendingPurchaseVerification());
  }
  else setStatus("Signed out.");
}, (error) => {
  ++authEpoch;
  clearAccountUi();
  byId("signedOut").classList.remove("hidden");
  byId("signedIn").classList.add("hidden");
  byId("signOut").classList.add("hidden");
  byId("refreshAccount").classList.add("hidden");
  setStatus(`Google sign-in could not initialize: ${error.message}`, "error");
});
