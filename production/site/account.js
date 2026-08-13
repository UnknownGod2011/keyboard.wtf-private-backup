import {
  auth,
  listenForAuth,
  googleSignIn,
  googleSignOut,
  loadAccount,
  loadUsagePage,
  loadCatalog,
  revokeDevice,
  revokeAllDesktopSessions,
  deleteAccountPermanently,
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
let usageCursor = null;
let usageRows = [];
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

const activityLabels = {
  fast_text: "Jarvis command",
  selected_text: "Selected text",
  agent_planner: "Jarvis command",
  browser_planner: "Browser task",
  screen_reasoning: "Screen reasoning",
  live_audio: "Jarvis Live",
  ai_request: "Jarvis AI",
};

function formatMonthlyPrice(minor, currency) {
  const amount = Number(minor || 0) / 100;
  const hasMinorUnits = Number(minor || 0) % 100 !== 0;
  return new Intl.NumberFormat(undefined, {
    style: "currency",
    currency,
    minimumFractionDigits: hasMinorUnits ? 2 : 0,
    maximumFractionDigits: 2,
  }).format(amount);
}

function renderUsageRows() {
  byId("usageList").innerHTML = usageRows.length
    ? usageRows.map((row) => {
      const label = activityLabels[row.activityType] || "Jarvis AI";
      const status = row.accountingStatus && row.accountingStatus !== "settled"
        ? "Finalizing usage…"
        : row.outcome === "completed" ? "Verified" : row.outcome;
      const audio = Number(row.inputAudioTokens || 0) + Number(row.outputAudioTokens || 0);
      const images = Number(row.inputImageTokens || 0) + Number(row.outputImageTokens || 0);
      return `<li class="usage-row"><details><summary><span>${escape(label)}<br><span class="small">${new Date(row.createdAt).toLocaleString()} · ${escape(status)}</span></span><strong>${formatCredits(row.chargedCreditMicros)}</strong></summary><div class="small usage-details">Model: ${escape(row.model || "—")}<br>Input/output: ${Number(row.inputTokens || 0).toLocaleString()} / ${Number(row.outputTokens || 0).toLocaleString()} tokens${audio ? `<br>Audio: ${audio.toLocaleString()} tokens` : ""}${images ? `<br>Images: ${images.toLocaleString()} tokens` : ""}<br>Pricing: ${escape(row.pricingVersion || "—")} · ${row.usageEstimated ? "estimated" : "provider-reported"}</div></details></li>`;
    }).join("")
    : "<li>No usage yet.</li>";
  byId("loadMoreUsage").classList.toggle("hidden", !usageCursor);
}

function clearAccountUi() {
  byId("identity").textContent = "—";
  byId("plan").textContent = "No paid plan";
  byId("balance").textContent = "0";
  byId("balanceBar").style.width = "0";
  byId("usage").textContent = "0 used this cycle";
  byId("allowanceBreakdown").textContent = "Plan allowance 0";
  byId("subscription").textContent = "None";
  byId("renewal").textContent = "No renewal scheduled.";
  byId("usageList").replaceChildren();
  byId("loadMoreUsage").classList.add("hidden");
  usageCursor = null;
  usageRows = [];
  byId("purchaseList").replaceChildren();
  byId("deviceList").replaceChildren();
  byId("products").replaceChildren();
}

async function render(expectedUser, epoch, quiet = false) {
  try {
    if (!quiet)
      setStatus("Signed in. Loading your account…");
    const [account, catalog] = await Promise.all([loadAccount(), loadCatalog()]);
    if (epoch !== authEpoch || auth.currentUser?.uid !== expectedUser.uid)
      return;
    trackServerPendingPurchases(account);
    byId("identity").textContent = `${account.displayName || "Google user"} · ${account.email}`;
    byId("plan").textContent = account.planId === "none" ? "No paid plan" : account.planId;
    const subscriptionOriginal = Number(account.subscriptionOriginalCreditMicros || 0);
    const subscriptionRemaining = Number(account.subscriptionAllowanceCreditMicros || 0)
      + Number(account.subscriptionReservedCreditMicros || 0);
    const totalUsable = Number(account.availableCreditMicros || 0)
      + Number(account.reservedCreditMicros || 0);
    const remaining = subscriptionOriginal > 0
      ? subscriptionRemaining
      : totalUsable;
    const used = subscriptionOriginal > 0
      ? Number(account.subscriptionConsumedCreditMicros || 0)
      : Number(account.cycleUsageCreditMicros || 0);
    const total = subscriptionOriginal > 0 ? subscriptionOriginal : Math.max(1, remaining + used);
    const percentage = Math.max(0, Math.min(100, remaining / total * 100));
    byId("balance").textContent = formatCredits(totalUsable);
    byId("balanceBar").style.width = `${percentage}%`;
    byId("usage").textContent = `${formatCredits(used)} of ${formatCredits(total)} credits used this cycle · ${percentage.toFixed(1)}% remaining`;
    const otherCredits = Number(account.topUpCreditMicros || 0) + Number(account.otherAllowanceCreditMicros || 0);
    byId("allowanceBreakdown").textContent = `Plan allowance ${formatCredits(subscriptionRemaining)}${otherCredits > 0 ? ` · Account adjustments ${formatCredits(otherCredits)}` : ""}`;
    byId("subscription").textContent = account.subscriptionStatus || "none";
    byId("renewal").textContent = account.cycleEndsAt
      ? `Current cycle ends ${new Date(account.cycleEndsAt).toLocaleDateString()}.`
      : "No renewal scheduled.";
    usageRows = account.recentUsage || [];
    usageCursor = usageRows.length === 20 ? usageRows.at(-1).requestId : null;
    renderUsageRows();
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
    const products = (catalog.products || []).filter((product) => product.kind === "subscription");
    const checkoutClosed = account.paymentsEnabled !== true;
    const subscriptionRequiresManagement = !["", "none", "cancelled", "completed", "expired", "failed"]
      .includes(String(account.subscriptionStatus || "none").trim().toLowerCase());
    byId("products").innerHTML = products.map((product) => {
      const currentPlan = subscriptionRequiresManagement && account.planId === product.sku;
      const disabled = checkoutClosed || currentPlan;
      const buttonText = currentPlan
        ? "Current plan — Active"
        : checkoutClosed
          ? "Coming soon"
          : `Subscribe — ${formatMonthlyPrice(product.amountMinor, product.currency)}/month`;
      return `<article class="card"><div class="eyebrow">Monthly plan</div><h2>${escape(product.displayName)}</h2><div class="balance" style="font-size:32px">${formatMoney(product.amountMinor, product.currency)}<span class="price-suffix">/month</span></div><p>${formatCredits(product.creditMicros)} provider-cost-backed credits per billing cycle.</p><button class="buy" data-sku="${escape(product.sku)}" ${disabled ? "disabled" : ""}>${buttonText}</button></article>`;
    }).join("");
    document.querySelectorAll(".buy").forEach((button) => button.addEventListener("click", async () => {
      button.disabled = true;
      try {
        const product = catalog.products.find((item) => item.sku === button.dataset.sku);
        if (account.paymentsEnabled !== true)
          throw new Error(catalog.paymentsKillSwitchMessage || "Subscriptions are not open yet.");
        setStatus("Opening secure Razorpay Checkout…");
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
byId("signOutAllDevices").addEventListener("click", async () => {
  const button = byId("signOutAllDevices");
  button.disabled = true;
  setStatus("Signing out connected Jarvis devices…");
  try {
    await revokeAllDesktopSessions();
    setStatus("All connected Jarvis devices were signed out.", "success");
    if (auth.currentUser)
      await refreshSignedIn(auth.currentUser, authEpoch, true);
  } catch (error) {
    setStatus(error.message, "error");
  } finally {
    button.disabled = false;
  }
});
byId("loadMoreUsage").addEventListener("click", async () => {
  if (!usageCursor) return;
  const button = byId("loadMoreUsage");
  button.disabled = true;
  try {
    const page = await loadUsagePage(usageCursor);
    const known = new Set(usageRows.map((row) => row.requestId));
    usageRows.push(...(page.items || []).filter((row) => !known.has(row.requestId)));
    usageCursor = page.nextCursor || null;
    renderUsageRows();
  } catch (error) {
    setStatus(error.message, "error");
  } finally {
    button.disabled = false;
  }
});
window.addEventListener("focus", refreshAfterReturningToPage);
document.addEventListener("visibilitychange", refreshAfterReturningToPage);
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
