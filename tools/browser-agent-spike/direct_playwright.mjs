import { chromium } from "playwright";
import { startMockServer } from "./mock_pages.mjs";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const outputDir = path.resolve(__dirname, "../../output/browser-agent-spike");

const moneyPattern = /(?:(?:[$₹€£]|rs\.?|inr|usd)\s*[\d,.]+(?:\s*(?:k|m|b|lakh|lakhs|crore|cr))?)|(?:[\d,.]+\s*(?:k|m|b|lakh|lakhs|crore|cr)?\s*(?:usd|inr|dollars?|rupees?))/i;

function amountValue(text) {
  const match = text.match(moneyPattern);
  if (!match) return null;
  const raw = match[0].toLowerCase();
  let multiplier = 1;
  if (/crore|\scr\b/.test(raw)) multiplier = 10_000_000;
  else if (/lakh|lac/.test(raw)) multiplier = 100_000;
  else if (/\d\s*k\b/.test(raw)) multiplier = 1_000;
  else if (/\d\s*m\b/.test(raw)) multiplier = 1_000_000;
  const number = Number((raw.match(/[\d,.]+/)?.[0] ?? "0").replaceAll(",", ""));
  return { value: number * multiplier, display: match[0] };
}

async function visibleCards(page, query) {
  const rows = await page.locator("article,section,li,tr,div").evaluateAll((elements, q) => {
    const words = String(q).toLowerCase().split(/[^a-z0-9]+/).filter((w) => w.length > 2);
    return elements
      .map((element) => {
        const rect = element.getBoundingClientRect();
        const text = (element.innerText || element.textContent || "").replace(/\s+/g, " ").trim();
        return { text, tag: element.tagName.toLowerCase(), top: Math.round(rect.top + window.scrollY), hits: words.filter((word) => text.toLowerCase().includes(word)).length };
      })
      .filter((row) => row.text.length > 15 && row.text.length < 700 && (row.hits || /[$₹€£]|usd|inr|price|rating|prize|award/i.test(row.text)))
      .slice(0, 80);
  }, query);
  return rows;
}

function bestMoney(rows, mode = "highest") {
  const ranked = rows
    .map((row) => {
      const amount = amountValue(row.text);
      const title = row.text.replace(moneyPattern, "").split(/[•|]/)[0].trim().slice(0, 120) || row.text.slice(0, 120);
      return { ...row, amount, title };
    })
    .filter((row) => row.amount)
    .sort((a, b) => mode === "lowest" ? a.amount.value - b.amount.value : b.amount.value - a.amount.value);
  const deduped = [];
  for (const row of ranked) {
    if (!deduped.some((other) => other.amount.value === row.amount.value && (row.title.includes(other.title) || other.title.includes(row.title)))) {
      deduped.push(row);
    }
  }
  return deduped;
}

async function runTask(name, fn) {
  const started = performance.now();
  const result = await fn();
  return { backend: "direct-playwright", task: name, seconds: Number(((performance.now() - started) / 1000).toFixed(2)), ...result };
}

export async function runDirectPlaywright({ includeReal = false } = {}) {
  const server = await startMockServer();
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();
  const results = [];
  try {
    results.push(await runTask("mock-devpost-highest-prize", async () => {
      await page.goto(`${server.baseUrl}/devpost`, { waitUntil: "domcontentloaded", timeout: 15000 });
      const ranked = bestMoney(await visibleCards(page, "highest prize pool hackathon"), "highest");
      return { success: ranked[0]?.title.includes("Global AI Challenge") && ranked[0]?.amount.value === 100000, confirmations: 0, clarifications: 0, result: ranked[0] ? `${ranked[0].title} — ${ranked[0].amount.display}` : "no candidate" };
    }));
    results.push(await runTask("mock-devpost-layout-recovery", async () => {
      await page.goto(`${server.baseUrl}/devpost-changed`, { waitUntil: "domcontentloaded", timeout: 15000 });
      const ranked = bestMoney(await visibleCards(page, "highest prize pool hackathon"), "highest");
      return { success: ranked[0]?.title.includes("Global AI Challenge") && ranked[0]?.amount.value === 100000, confirmations: 0, clarifications: 0, result: ranked[0] ? `${ranked[0].title} — ${ranked[0].amount.display}` : "no candidate" };
    }));
    results.push(await runTask("mock-ecommerce-cheapest-highrated", async () => {
      await page.goto(`${server.baseUrl}/store`, { waitUntil: "domcontentloaded", timeout: 15000 });
      const rows = await visibleCards(page, "keyboard price rating");
      const products = rows.map((row) => {
        const price = amountValue(row.text);
        const rating = Number(row.text.match(/rating:\s*([\d.]+)/i)?.[1] ?? "0");
        return { text: row.text, price, rating };
      }).filter((row) => row.price && row.rating >= 4);
      products.sort((a, b) => a.price.value - b.price.value);
      return { success: /Budget Keyboard/.test(products[0]?.text ?? ""), confirmations: 0, clarifications: 0, result: products[0]?.text ?? "no product" };
    }));
    results.push(await runTask("mock-form-fill-preview", async () => {
      await page.goto(`${server.baseUrl}/form`, { waitUntil: "domcontentloaded", timeout: 15000 });
      await page.getByLabel("Project name").fill("keyboard.wtf");
      await page.getByLabel("Summary").fill("Local browser agent comparison");
      await page.getByRole("button", { name: "Preview" }).click();
      const text = await page.locator("#result").innerText();
      return { success: text.includes("keyboard.wtf") && text.includes("Local browser agent comparison"), confirmations: 0, clarifications: 0, result: text };
    }));
    results.push(await runTask("mock-missing-project-clarification", async () => {
      await page.goto(`${server.baseUrl}/portfolio`, { waitUntil: "domcontentloaded", timeout: 15000 });
      const body = await page.locator("body").innerText({ timeout: 5000 });
      const found = /keyboard\.wtf/i.test(body);
      return {
        success: !found,
        confirmations: 0,
        clarifications: found ? 0 : 1,
        result: found ? "keyboard.wtf found" : "keyboard.wtf was not found; clarification required before creating or selecting another project",
      };
    }));
    if (includeReal) {
      results.push(await runTask("real-devpost-highest-prize", async () => {
        await page.goto("https://devpost.com/hackathons", { waitUntil: "domcontentloaded", timeout: 30000 });
        const body = await page.locator("body").innerText({ timeout: 10000 }).catch(() => "");
        if (/captcha|blocked|attention required/i.test(body)) return { success: false, blocked: true, confirmations: 0, clarifications: 0, result: "blocked/challenged" };
        const ranked = bestMoney(await visibleCards(page, "highest prize pool hackathon"), "highest");
        return { success: Boolean(ranked[0] && ranked[0].amount.value > 0), confirmations: 0, clarifications: 0, result: ranked[0] ? `${ranked[0].title} — ${ranked[0].amount.display}` : "no candidate" };
      }));
      results.push(await runTask("real-bookmyshow-toy-story-mumbai", async () => {
        await page.goto("https://in.bookmyshow.com/explore/movies-mumbai", { waitUntil: "domcontentloaded", timeout: 30000 });
        const body = await page.locator("body").innerText({ timeout: 10000 }).catch(() => "");
        if (/captcha|blocked|attention required|unable to access/i.test(body)) return { success: false, blocked: true, confirmations: 0, clarifications: 0, result: "blocked/challenged" };
        return { success: /toy story/i.test(body), confirmations: 0, clarifications: 0, result: /toy story/i.test(body) ? "Toy Story visible on movies page; stopped before booking" : "Toy Story not visible" };
      }));
      results.push(await runTask("real-ecommerce-search-compare", async () => {
        await page.goto("https://www.amazon.in/s?k=Logitech+MX+Master+mouse", { waitUntil: "domcontentloaded", timeout: 30000 }).catch(() => null);
        const body = await page.locator("body").innerText({ timeout: 10000 }).catch(() => "");
        if (/captcha|robot check|enter the characters|blocked|attention required/i.test(body)) return { success: false, blocked: true, confirmations: 0, clarifications: 0, result: "blocked/challenged" };
        return { success: /logitech|mx master/i.test(body), confirmations: 0, clarifications: 0, result: /logitech|mx master/i.test(body) ? "Visible product search results found; did not add to cart" : "No matching visible product results" };
      }));
      results.push(await runTask("real-form-fill-preview", async () => {
        await page.goto("https://www.selenium.dev/selenium/web/web-form.html", { waitUntil: "domcontentloaded", timeout: 30000 });
        await page.getByLabel("Text input").fill("keyboard.wtf");
        await page.getByLabel("Textarea").fill("Local-first Windows Jarvis assistant");
        const text = await page.locator("body").innerText({ timeout: 5000 });
        return { success: /keyboard\.wtf/i.test(await page.getByLabel("Text input").inputValue()), confirmations: 0, clarifications: 0, result: "Filled public test form fields and stopped before submit" };
      }));
      results.push(await runTask("real-travel-search", async () => {
        await page.goto("https://www.google.com/travel/flights", { waitUntil: "domcontentloaded", timeout: 30000 }).catch(() => null);
        const body = await page.locator("body").innerText({ timeout: 10000 }).catch(() => "");
        if (/captcha|unusual traffic|blocked|attention required/i.test(body)) return { success: false, blocked: true, confirmations: 0, clarifications: 0, result: "blocked/challenged" };
        return { success: body.length > 200, confirmations: 0, clarifications: 0, result: "Flight search page loaded for manual/no-booking comparison baseline" };
      }));
      results.push(await runTask("real-hotel-research", async () => {
        await page.goto("https://www.google.com/travel/hotels/Mumbai", { waitUntil: "domcontentloaded", timeout: 30000 }).catch(() => null);
        const body = await page.locator("body").innerText({ timeout: 10000 }).catch(() => "");
        if (/captcha|unusual traffic|blocked|attention required/i.test(body)) return { success: false, blocked: true, confirmations: 0, clarifications: 0, result: "blocked/challenged" };
        return { success: body.length > 200, confirmations: 0, clarifications: 0, result: "Hotel research page loaded for read-only/no-booking baseline" };
      }));
    }
    return results;
  } finally {
    await context.close().catch(() => {});
    await browser.close().catch(() => {});
    await server.close();
  }
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  const includeReal = process.argv.includes("--real");
  const results = await runDirectPlaywright({ includeReal });
  await mkdir(outputDir, { recursive: true });
  await writeFile(
    path.join(outputDir, includeReal ? "direct-playwright-real.json" : "direct-playwright-mock.json"),
    JSON.stringify(results, null, 2),
    "utf8",
  );
  console.log(JSON.stringify(results, null, 2));
}
