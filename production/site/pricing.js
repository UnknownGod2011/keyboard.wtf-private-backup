import {
  loadAccount,
  loadCatalog,
  formatMoney,
  formatCredits,
  waitForAuthInitialization,
} from "./app.js";
import { runtimeConfig } from "./config.js";

const products = document.getElementById("products");
const status = document.getElementById("status");
try {
  const [catalog, user] = await Promise.all([
    loadCatalog(),
    waitForAuthInitialization(),
  ]);
  const account = user ? await loadAccount() : null;
  const subscriptionStatus = String(account?.subscriptionStatus || "none").trim().toLowerCase();
  const hasManagedSubscription = !["", "none", "cancelled", "completed", "expired", "failed"]
    .includes(subscriptionStatus);
  for (const product of (catalog.products || []).filter((item) => item.kind === "subscription")) {
    const card = document.createElement("section");
    card.className = "card";
    const kind = document.createElement("div");
    kind.className = "eyebrow";
    kind.textContent = "Monthly plan";
    const title = document.createElement("h2");
    title.textContent = product.displayName;
    const price = document.createElement("div");
    price.className = "balance";
    price.style.fontSize = "30px";
    price.textContent = formatMoney(product.amountMinor, product.currency);
    const credits = document.createElement("p");
    credits.textContent = `${formatCredits(product.creditMicros)} provider-cost-backed credits per billing cycle`;
    const reference = document.createElement("p");
    reference.className = "price-reference";
    const display = runtimeConfig.pricingDisplay;
    const matchesConfiguredPrice = product.currency === display.baseCurrency
      && product.amountMinor === display.baseAmountMinor;
    reference.textContent = matchesConfiguredPrice
      ? `Approx. ${display.approximateEquivalents.join(" · ")}`
      : "";
    const note = document.createElement("p");
    note.className = "small price-note";
    const referenceMonth = new Date(`${display.asOf}T00:00:00Z`).toLocaleDateString(undefined, {
      month: "long",
      year: "numeric",
      timeZone: "UTC",
    });
    note.textContent = matchesConfiguredPrice
      ? `Approximate currency equivalents for reference only (updated ${referenceMonth}). The actual charge is ₹999 INR; exchange rates, taxes, and payment-provider or bank fees may vary.`
      : "";
    const signIn = document.createElement("a");
    signIn.className = "button";
    signIn.href = "./account.html";
    signIn.textContent = catalog.paymentsKillSwitch !== false
      ? "Coming soon"
      : !user
        ? "Sign in to subscribe"
        : hasManagedSubscription
          ? "Manage current plan"
          : "Subscribe from Account";
    if (catalog.paymentsKillSwitch !== false) {
      signIn.setAttribute("aria-disabled", "true");
      signIn.classList.add("disabled");
    }
    card.append(kind, title, price, credits, reference, note, signIn);
    products.append(card);
  }
  status.textContent = catalog.paymentsKillSwitch === false
    ? !user
      ? "Sign in with your keyboard.wtf account to subscribe."
      : hasManagedSubscription
        ? "Your current subscription is managed from Account & Billing."
        : "Subscriptions are available from your signed-in account."
    : (catalog.paymentsKillSwitchMessage || "Subscriptions are not open yet.");
} catch (error) {
  status.textContent = error.message;
  status.classList.add("error");
}
