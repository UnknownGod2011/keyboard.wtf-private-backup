import { Stagehand } from "@browserbasehq/stagehand";
import { z } from "zod";
import { startMockServer } from "./mock_pages.mjs";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const outputDir = path.resolve(__dirname, "../../output/browser-agent-spike");

async function loadLocalEnv() {
  for (const filename of [".env", ".env.local"]) {
    const file = path.resolve(__dirname, filename);
    try {
      const text = await import("node:fs/promises").then((fs) => fs.readFile(file, "utf8"));
      for (const rawLine of text.split(/\r?\n/)) {
        const line = rawLine.trim();
        if (!line || line.startsWith("#") || !line.includes("=")) continue;
        const index = line.indexOf("=");
        const key = line.slice(0, index).trim();
        const value = line.slice(index + 1).trim().replace(/^['"]|['"]$/g, "");
        if (key && value && !process.env[key]) {
          process.env[key] = value;
        }
      }
    } catch {
      // Missing local env files are allowed; the key check below fails closed.
    }
  }
}

function geminiKey() {
  const key = process.env.GEMINI_API_KEY || process.env.GOOGLE_API_KEY || process.env.MODEL_API_KEY;
  if (!key) {
    throw new Error("Gemini API key not found. Set GEMINI_API_KEY or GOOGLE_API_KEY in tools/browser-agent-spike/.env.local.");
  }
  return key;
}

const scrub = (value) => {
  if (typeof value === "string") {
    return value.replace(/http:\/\/127\.0\.0\.1:\d+/g, "http://127.0.0.1:<port>");
  }
  if (Array.isArray(value)) {
    return value.map(scrub);
  }
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, scrub(item)]));
  }
  return value;
};

async function withStagehand(taskName, run) {
  const started = performance.now();
  const safeTaskName = taskName.replace(/[^a-z0-9_-]+/gi, "-").toLowerCase();
  const profileDir = path.resolve(__dirname, `.stagehand-profile-${safeTaskName}`);
  await mkdir(profileDir, { recursive: true });
  const result = {
    backend: "stagehand",
    task: taskName,
    status: "failed",
    durationMs: null,
    steps: null,
    confirmations: 0,
    clarifications: 0,
    result: null,
    error: null,
  };

  const stagehand = new Stagehand({
    env: "LOCAL",
    verbose: 0,
    disablePino: true,
    model: {
      modelName: process.env.BROWSER_AGENT_STAGEHAND_MODEL || "google/gemini-2.5-flash",
      apiKey: geminiKey(),
      temperature: 0,
    },
    localBrowserLaunchOptions: {
      headless: true,
      userDataDir: profileDir,
      preserveUserDataDir: true,
      viewport: { width: 1280, height: 800 },
      args: ["--disable-dev-shm-usage"],
    },
    actTimeoutMs: 30000,
    domSettleTimeout: 500,
    logger: () => {},
  });

  try {
    await stagehand.init();
    const taskResult = await run(stagehand);
    result.status = "passed";
    result.result = taskResult;
  } catch (error) {
    result.error = `${error?.name || "Error"}: ${error?.message || error}`;
  } finally {
    result.durationMs = Math.round(performance.now() - started);
    try {
      await stagehand.close({ force: true });
    } catch {
      // spike cleanup only
    }
  }

  return scrub(result);
}

async function runMockTasks(baseUrl, requestedTask = null) {
  const prizeSchema = z.object({
    name: z.string(),
    amount: z.string(),
    reasoning: z.string().optional(),
  });
  const productSchema = z.object({
    name: z.string(),
    price: z.string(),
    rating: z.string(),
    reasoning: z.string().optional(),
  });
  const formSchema = z.object({
    previewVisible: z.boolean(),
    previewText: z.string().optional(),
    submitted: z.boolean().optional(),
  });
  const clarificationSchema = z.object({
    found: z.boolean(),
    clarificationNeeded: z.boolean(),
    alternatives: z.array(z.string()).optional(),
    message: z.string(),
  });

  const tasks = [
    ["mock-devpost-highest-prize", () => withStagehand("mock-devpost-highest-prize", async (stagehand) => {
      const page = stagehand.context.pages()[0];
      await page.goto(`${baseUrl}/devpost`);
      return stagehand.extract(
        "Read the visible hackathon cards and return the single hackathon with the highest prize pool and the amount.",
        prizeSchema,
      );
    })],
    ["mock-devpost-layout-recovery", () => withStagehand("mock-devpost-layout-recovery", async (stagehand) => {
      const page = stagehand.context.pages()[0];
      await page.goto(`${baseUrl}/devpost-changed`);
      return stagehand.extract(
        "The layout has changed. Read the visible tiles and return the single hackathon with the highest prize pool and the amount.",
        prizeSchema,
      );
    })],
    ["mock-ecommerce-cheapest-highrated", () => withStagehand("mock-ecommerce-cheapest-highrated", async (stagehand) => {
      const page = stagehand.context.pages()[0];
      await page.goto(`${baseUrl}/store`);
      return stagehand.extract(
        "Compare visible products and return the cheapest product with rating at least 4 stars, including price and rating.",
        productSchema,
      );
    })],
    ["mock-form-fill-preview", () => withStagehand("mock-form-fill-preview", async (stagehand) => {
      const page = stagehand.context.pages()[0];
      await page.goto(`${baseUrl}/form`);
      await stagehand.act("Fill Project name with keyboard.wtf and Summary with Local-first Windows Jarvis assistant.");
      await stagehand.act("Click Preview. Do not click Submit.");
      return stagehand.extract(
        "Return whether the preview is visible and whether the form was submitted.",
        formSchema,
      );
    })],
    ["mock-missing-project-clarification", () => withStagehand("mock-missing-project-clarification", async (stagehand) => {
      const page = stagehand.context.pages()[0];
      await page.goto(`${baseUrl}/portfolio`);
      return stagehand.extract(
        "Find project keyboard.wtf in this portfolio. If it is missing, do not invent a project; return that clarification is needed and list visible alternatives.",
        clarificationSchema,
      );
    })],
  ];
  const selected = requestedTask ? tasks.filter(([name]) => name === requestedTask) : tasks;
  const results = [];
  for (const [, run] of selected) {
    results.push(await run());
  }
  return results;
}

async function runRealTasks(requestedTask = null) {
  const prizeSchema = z.object({
    name: z.string(),
    amount: z.string(),
    reasoning: z.string().optional(),
  });
  const showSchema = z.object({
    status: z.string(),
    options: z.array(z.string()).optional(),
    blocker: z.string().optional(),
  });
  const genericSchema = z.object({
    status: z.string(),
    summary: z.string(),
    options: z.array(z.string()).optional(),
    blocker: z.string().optional(),
  });
  const formSchema = z.object({
    filled: z.boolean(),
    submitted: z.boolean(),
    summary: z.string(),
  });

  const runAgent = async (stagehand, instruction, maxSteps = 8) => {
    const agent = stagehand.agent({ mode: "dom", model: process.env.BROWSER_AGENT_STAGEHAND_MODEL || "google/gemini-2.5-flash" });
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 90000);
    try {
      return await agent.execute({ instruction, maxSteps, signal: controller.signal });
    } finally {
      clearTimeout(timeout);
    }
  };

  const tasks = [
    ["real-devpost-highest-prize", () => withStagehand("real-devpost-highest-prize", async (stagehand) => {
      const page = stagehand.context.pages()[0];
      await page.goto("https://devpost.com/hackathons", { waitUntil: "domcontentloaded", timeout: 45000 });
      return stagehand.extract(
        "Read visible hackathon listings. Return the hackathon with the highest visible prize pool and amount. Do not log in or submit anything.",
        prizeSchema,
      );
    })],
    ["real-bookmyshow-toy-story-mumbai", () => withStagehand("real-bookmyshow-toy-story-mumbai", async (stagehand) => {
      const page = stagehand.context.pages()[0];
      await page.goto("https://in.bookmyshow.com/explore/movies-mumbai", { waitUntil: "domcontentloaded", timeout: 45000 });
      return stagehand.extract(
        "Read the visible page. For Toy Story shows on Sunday June 28 2026 in Mumbai, return found visible options. If blocked or not visible, say so. Do not book tickets or select seats.",
        showSchema,
      );
    })],
    ["real-ecommerce-search-compare", () => withStagehand("real-ecommerce-search-compare", async (stagehand) => {
      return runAgent(
        stagehand,
        "Open https://www.amazon.in and search for Logitech MX Master mouse. Compare visible product options by title, price, and rating if visible. Do not add anything to cart; say that add-to-cart requires confirmation.",
        10,
      );
    })],
    ["real-form-fill-preview", () => withStagehand("real-form-fill-preview", async (stagehand) => {
      const page = stagehand.context.pages()[0];
      await page.goto("https://www.selenium.dev/selenium/web/web-form.html", { waitUntil: "domcontentloaded", timeout: 45000 });
      await stagehand.act("Fill the Text input with keyboard.wtf and the Textarea with Local-first Windows Jarvis assistant. Do not submit.");
      return stagehand.extract("Return whether the fields were filled and whether the form was submitted.", formSchema);
    })],
    ["real-travel-search", () => withStagehand("real-travel-search", async (stagehand) => {
      return runAgent(
        stagehand,
        "Open https://www.google.com/travel/flights. Search read-only flight options from Mumbai to Delhi for Friday June 26 2026. Return cheapest reasonable visible options if available. Do not book.",
        10,
      );
    })],
    ["real-hotel-research", () => withStagehand("real-hotel-research", async (stagehand) => {
      return runAgent(
        stagehand,
        "Open https://www.google.com/travel/hotels/Mumbai. Search for budget Mumbai hotels for June 27 to June 28 2026. Return the best-rated visible options under a reasonable price range if available. Do not book.",
        10,
      );
    })],
  ];
  const selected = requestedTask ? tasks.filter(([name]) => name === requestedTask) : tasks;
  const results = [];
  for (const [, run] of selected) {
    results.push(await run());
  }
  return results;
}

async function main() {
  await loadLocalEnv();
  await mkdir(outputDir, { recursive: true });
  const server = await startMockServer();
  try {
    const requestedTaskIndex = process.argv.indexOf("--task");
    const requestedTask = requestedTaskIndex >= 0 ? process.argv[requestedTaskIndex + 1] : null;
    let results = process.argv.includes("--real-only") ? [] : await runMockTasks(server.baseUrl, requestedTask);
    if (process.argv.includes("--real") || process.argv.includes("--real-only")) {
      results.push(...(await runRealTasks(requestedTask)));
    }
    if (requestedTask) {
      results = results.filter((result) => result.task === requestedTask);
      if (!results.length) {
        throw new Error(`Task not found: ${requestedTask}`);
      }
    }
    const out = path.join(
      outputDir,
      requestedTask ? `stagehand-${requestedTask}.json` : (process.argv.includes("--real") || process.argv.includes("--real-only") ? "stagehand-real.json" : "stagehand-mock.json"),
    );
    await writeFile(out, JSON.stringify(results, null, 2), "utf8");
    console.log(JSON.stringify(results, null, 2));
  } finally {
    await server.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
