import { loadCatalog, formatMoney, formatCredits } from "./app.js";

const products = document.getElementById("products");
const status = document.getElementById("status");
try {
  const catalog = await loadCatalog();
  for (const product of catalog.products) {
    const card = document.createElement("section");
    card.className = "card";
    const kind = document.createElement("div");
    kind.className = "eyebrow";
    kind.textContent = product.kind;
    const title = document.createElement("h2");
    title.textContent = product.displayName;
    const price = document.createElement("div");
    price.className = "balance";
    price.style.fontSize = "30px";
    price.textContent = formatMoney(product.amountMinor, product.currency);
    const credits = document.createElement("p");
    credits.textContent = `${formatCredits(product.creditMicros)} configured credits`;
    const signIn = document.createElement("a");
    signIn.className = "button";
    signIn.href = "./account.html";
    signIn.textContent = "Sign in to continue";
    card.append(kind, title, price, credits, signIn);
    products.append(card);
  }
  status.textContent = "Prices are loaded from the authoritative staging catalog.";
} catch (error) {
  status.textContent = error.message;
  status.classList.add("error");
}
