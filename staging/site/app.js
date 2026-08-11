import { initializeApp } from "https://www.gstatic.com/firebasejs/11.10.0/firebase-app.js";
import {
  browserLocalPersistence,
  getAuth,
  GoogleAuthProvider,
  onAuthStateChanged,
  reauthenticateWithPopup,
  setPersistence,
  signInWithPopup,
  signOut,
} from "https://www.gstatic.com/firebasejs/11.10.0/firebase-auth.js";
import { runtimeConfig } from "./config.js";

const app = initializeApp(runtimeConfig.firebase);
export const auth = getAuth(app);
await setPersistence(auth, browserLocalPersistence);

let resolveInitialAuth;
let rejectInitialAuth;
const initialAuth = new Promise((resolve, reject) => {
  resolveInitialAuth = resolve;
  rejectInitialAuth = reject;
});
let initialAuthSettled = false;
onAuthStateChanged(
  auth,
  (user) => {
    if (!initialAuthSettled) {
      initialAuthSettled = true;
      resolveInitialAuth(user);
    }
  },
  (error) => {
    if (!initialAuthSettled) {
      initialAuthSettled = true;
      rejectInitialAuth(error);
    }
  },
);

const state = {
  account: null,
  catalog: null,
  checkoutOpen: false,
};
let razorpayLoader;
const pendingPurchaseStorageKey = "keyboardWtf.pendingPurchases.v1";
const purchaseWaiters = new Map();

function pendingPurchasesByUser() {
  try {
    const parsed = JSON.parse(localStorage.getItem(pendingPurchaseStorageKey) || "{}");
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function writePendingPurchases(value) {
  try {
    localStorage.setItem(pendingPurchaseStorageKey, JSON.stringify(value));
  } catch {
    // Private browsing/storage policy can disable localStorage. The active-page
    // reconciliation loop still works; persistence across reloads is best effort.
  }
}

function normalizePendingEntries(value) {
  if (!Array.isArray(value)) return [];
  const now = Date.now();
  return value.map((entry) => typeof entry === "string"
    ? { id: entry, firstSeenAt: now }
    : { id: entry?.id, firstSeenAt: Number(entry?.firstSeenAt) || now })
    .filter((entry) => typeof entry.id === "string" && entry.id.length > 0);
}

function rememberPendingPurchase(userId, purchaseId) {
  if (!userId || !purchaseId) return;
  const byUser = pendingPurchasesByUser();
  const current = normalizePendingEntries(byUser[userId]);
  if (!current.some((entry) => entry.id === purchaseId))
    current.push({ id: purchaseId, firstSeenAt: Date.now() });
  byUser[userId] = current.slice(-10);
  writePendingPurchases(byUser);
}

function forgetPendingPurchase(userId, purchaseId) {
  if (!userId || !purchaseId) return;
  const byUser = pendingPurchasesByUser();
  const current = normalizePendingEntries(byUser[userId]);
  if (!current.length) return;
  byUser[userId] = current.filter((entry) => entry.id !== purchaseId);
  if (!byUser[userId].length) delete byUser[userId];
  writePendingPurchases(byUser);
}

function forgetAllPendingPurchases(userId) {
  if (!userId) return;
  const byUser = pendingPurchasesByUser();
  if (!(userId in byUser)) return;
  delete byUser[userId];
  writePendingPurchases(byUser);
}

export function getPendingPurchaseIds() {
  const userId = auth.currentUser?.uid;
  if (!userId) return [];
  return normalizePendingEntries(pendingPurchasesByUser()[userId]).map((entry) => entry.id);
}

function pendingPurchaseFirstSeenAt(userId, purchaseId) {
  return normalizePendingEntries(pendingPurchasesByUser()[userId])
    .find((entry) => entry.id === purchaseId)?.firstSeenAt;
}

export function trackServerPendingPurchases(account) {
  const userId = auth.currentUser?.uid;
  if (!userId) return;
  const pendingStatuses = new Set(["creating", "pending", "verification_pending", "authorized"]);
  for (const purchase of account?.recentPurchases || []) {
    if (!purchase.fulfilled && pendingStatuses.has(String(purchase.status || "").toLowerCase()))
      rememberPendingPurchase(userId, purchase.purchaseId);
  }
}

async function loadRazorpay() {
  if (window.Razorpay)
    return;
  razorpayLoader ??= new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = "https://checkout.razorpay.com/v1/checkout.js";
    script.async = true;
    script.onload = () => window.Razorpay
      ? resolve()
      : reject(new Error("Razorpay Checkout did not initialize."));
    script.onerror = () => reject(new Error("Razorpay Checkout could not be loaded."));
    document.head.append(script);
  }).catch((error) => {
    razorpayLoader = undefined;
    throw error;
  });
  await razorpayLoader;
}

export function listenForAuth(callback, onError) {
  return onAuthStateChanged(auth, callback, onError);
}

export async function waitForAuthInitialization() {
  await initialAuth;
  return auth.currentUser;
}

export async function googleSignIn() {
  const provider = new GoogleAuthProvider();
  provider.setCustomParameters({ prompt: "select_account" });
  return signInWithPopup(auth, provider);
}

export async function googleSignOut() {
  await signOut(auth);
  state.account = null;
}

async function sendApiRequest(path, options, user, forceRefresh) {
  const headers = new Headers(options.headers || {});
  headers.set("Content-Type", "application/json");
  if (user) {
    const token = await user.getIdToken(forceRefresh);
    headers.set("Authorization", `Bearer ${token}`);
  }
  return fetch(`${runtimeConfig.backendBaseUrl}${path}`, {
    ...options,
    headers,
    cache: "no-store",
  });
}

export async function api(path, options = {}) {
  const {
    authenticated = false,
    retryAuthentication = true,
    forceAuthenticationRefresh = false,
    ...fetchOptions
  } = options;
  const user = await waitForAuthInitialization();
  if (authenticated && !user) {
    const error = new Error("Sign in with Google to continue.");
    error.status = 401;
    error.code = "firebase-signed-out";
    throw error;
  }

  let response;
  try {
    response = await sendApiRequest(
      path,
      fetchOptions,
      authenticated ? user : null,
      forceAuthenticationRefresh,
    );
    if (response.status === 401 && authenticated && user && retryAuthentication)
      response = await sendApiRequest(path, fetchOptions, user, true);
  } catch (cause) {
    const error = new Error("The account service could not be reached. Check your connection and retry.");
    error.code = "network-failure";
    error.cause = cause;
    throw error;
  }

  const contentType = response.headers.get("content-type") || "";
  const body = contentType.includes("application/json")
    ? await response.json()
    : { detail: await response.text() };
  if (!response.ok) {
    const error = new Error(body.detail || body.title || `Request failed (${response.status})`);
    error.status = response.status;
    error.code = response.status === 401 ? "backend-auth-rejected" : "backend-rejected";
    throw error;
  }
  return body;
}

export async function loadCatalog() {
  state.catalog = await api("/v1/catalog");
  return state.catalog;
}

export async function loadAccount() {
  state.account = await api("/v1/account", { authenticated: true });
  return state.account;
}

export async function loadUsagePage(cursor = null, limit = 20) {
  const query = new URLSearchParams({ limit: String(limit) });
  if (cursor) query.set("cursor", cursor);
  return api(`/v1/usage?${query}`, { authenticated: true });
}

export async function revokeDevice(deviceId) {
  await api("/v1/devices/revoke", {
    authenticated: true,
    method: "POST",
    body: JSON.stringify({ deviceId }),
  });
  return loadAccount();
}

export async function deleteAccountPermanently() {
  const user = await waitForAuthInitialization();
  if (!user)
    throw new Error("Sign in with Google to continue.");
  const provider = new GoogleAuthProvider();
  provider.setCustomParameters({ prompt: "select_account" });
  await reauthenticateWithPopup(user, provider);
  await api("/v1/account/delete", {
    authenticated: true,
    retryAuthentication: false,
    forceAuthenticationRefresh: true,
    method: "POST",
    body: JSON.stringify({ confirmPermanentDeletion: true }),
  });
  forgetAllPendingPurchases(user.uid);
  await signOut(auth);
  state.account = null;
}

export async function grantTestCredits(recipientEmail, creditMicros, reason) {
  return api("/v1/admin/grants", {
    authenticated: true,
    method: "POST",
    body: JSON.stringify({
      recipientEmail,
      creditMicros,
      reason,
      idempotencyKey: crypto.randomUUID(),
    }),
  });
}

export async function beginCheckout(product) {
  if (state.checkoutOpen) return;
  state.checkoutOpen = true;
  try {
    await loadRazorpay();
    const idempotencyKey = crypto.randomUUID();
    const checkout = await api("/v1/billing/checkout", {
      authenticated: true,
      method: "POST",
      body: JSON.stringify({ productSku: product.sku, idempotencyKey }),
    });
    return await new Promise((resolve, reject) => {
      let checkoutHandlerStarted = false;
      const cancelPendingCheckout = async () => {
        try {
          await api("/v1/billing/cancel", {
            authenticated: true,
            method: "POST",
            body: JSON.stringify({ purchaseId: checkout.purchaseId }),
          });
        } catch {
          // A provider webhook may already have advanced the purchase. The account
          // refresh remains authoritative, so cancellation cleanup is best-effort.
        }
      };
      const options = {
        key: checkout.publicKeyId,
        name: "keyboard.wtf",
        description: product.displayName,
        currency: product.currency,
        amount: product.amountMinor,
        order_id: checkout.providerOrderId,
        subscription_id: checkout.providerSubscriptionId,
        notes: { purchase_id: checkout.purchaseId },
        modal: {
          ondismiss: () => {
            if (checkoutHandlerStarted) return;
            checkoutHandlerStarted = true;
            void cancelPendingCheckout().finally(() => reject(new Error("Checkout was cancelled.")));
          },
          escape: true,
          animation: true,
        },
        handler: async (result) => {
          checkoutHandlerStarted = true;
          try {
            await api("/v1/billing/confirm", {
              authenticated: true,
              method: "POST",
              body: JSON.stringify({
                purchaseId: checkout.purchaseId,
                providerPaymentId: result.razorpay_payment_id,
                providerOrderOrSubscriptionId:
                  result.razorpay_order_id || result.razorpay_subscription_id,
                signature: result.razorpay_signature,
              }),
            });
            resolve(checkout.purchaseId);
          } catch (error) {
            reject(error);
          }
        },
      };
      Object.keys(options).forEach((key) => options[key] === undefined && delete options[key]);
      const razorpay = new window.Razorpay(options);
      razorpay.on("payment.failed", (result) => {
        if (checkoutHandlerStarted) return;
        // Record the locally observed terminal failure promptly. A later
        // provider-authoritative webhook may still advance this purchase, but
        // an abandoned failed checkout must not sit in `pending` until the
        // account's long-term abandonment cleanup runs.
        checkoutHandlerStarted = true;
        void cancelPendingCheckout().finally(() =>
          reject(new Error(result.error?.description || "Payment failed.")));
      });
      razorpay.open();
    });
  } finally {
    state.checkoutOpen = false;
  }
}

export async function waitForAuthoritativePurchase(purchaseId, timeoutMs = 10 * 60_000) {
  if (!purchaseId) throw new Error("The checkout purchase ID is missing.");
  const user = auth.currentUser;
  if (!user) throw new Error("Sign in with Google to verify this payment.");
  const waiterKey = `${user.uid}:${purchaseId}`;
  if (purchaseWaiters.has(waiterKey)) return purchaseWaiters.get(waiterKey);

  rememberPendingPurchase(user.uid, purchaseId);
  const waiter = (async () => {
    const started = pendingPurchaseFirstSeenAt(user.uid, purchaseId) || Date.now();
    while (Date.now() - started < timeoutMs) {
      if (auth.currentUser?.uid !== user.uid) {
        const error = new Error("The signed-in account changed while payment verification was in progress.");
        error.code = "payment-account-changed";
        throw error;
      }

      let account;
      try {
        account = await loadAccount();
      } catch (error) {
        const transient = error.code === "network-failure"
          || (Number.isInteger(error.status) && error.status >= 500);
        if (!transient) throw error;
        const elapsed = Date.now() - started;
        const delay = elapsed < 30_000 ? 1500 : elapsed < 120_000 ? 3000 : 10_000;
        await new Promise((resolve) => setTimeout(resolve, delay));
        continue;
      }
      const purchase = account.recentPurchases?.find((item) => item.purchaseId === purchaseId);
      if (purchase && (purchase.fulfilled === true || ["paid", "captured", "charged"].includes(purchase.status))) {
        forgetPendingPurchase(user.uid, purchaseId);
        return account;
      }
      if (purchase && ["failed", "provider_failed", "cancelled", "expired", "halted", "refunded", "disputed"].includes(purchase.status)) {
        forgetPendingPurchase(user.uid, purchaseId);
        const error = new Error(`Payment verification ended with status: ${purchase.status}.`);
        error.code = "payment-terminal";
        error.purchaseStatus = purchase.status;
        throw error;
      }

      const elapsed = Date.now() - started;
      const delay = elapsed < 30_000 ? 1500 : elapsed < 120_000 ? 3000 : 10_000;
      await new Promise((resolve) => setTimeout(resolve, delay));
    }

    const error = new Error("Payment confirmation is delayed. Use Refresh usage later; the server will still apply any verified webhook exactly once.");
    error.code = "payment-pending";
    throw error;
  })();

  purchaseWaiters.set(waiterKey, waiter);
  try {
    return await waiter;
  } finally {
    purchaseWaiters.delete(waiterKey);
  }
}

export function formatCredits(micros) {
  return new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format((micros || 0) / 1_000_000);
}

export function formatMoney(minor, currency) {
  return new Intl.NumberFormat(undefined, { style: "currency", currency }).format((minor || 0) / 100);
}

export { state };
