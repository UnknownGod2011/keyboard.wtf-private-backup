import fs from "node:fs";
import path from "node:path";
import { Stagehand } from "@browserbasehq/stagehand";
import {
  ensureDirs,
  finishResult,
  loadLocalEnv,
  makeBaseResult,
  parseArgs,
  profilesDir,
  redactError,
  resultsDir,
  safeName,
  screenshotsDir,
  selectTasks,
  tracesDir,
  writeJson
} from "../common.mjs";
import { ExhaustedBakeoffKeyError, withBakeoffKeyPool } from "../key-pool.mjs";

ensureDirs();
loadLocalEnv();

const args = parseArgs();
const task = selectTasks(args)[0];
if (!task) {
  console.error("No task selected.");
  process.exit(1);
}

const startTime = Date.now();
const result = makeBaseResult("stagehand", task);
const resultPath = path.join(resultsDir, `stagehand-${safeName(task.id)}.json`);
const screenshotPath = path.join(screenshotsDir, `stagehand-${safeName(task.id)}-final.png`);
const traceDir = path.join(tracesDir, `stagehand-${safeName(task.id)}`);
fs.mkdirSync(traceDir, { recursive: true });

let stagehand;
async function closeStagehand() {
  try {
    if (stagehand) await stagehand.close();
  } catch {
    // ignored during cleanup
  } finally {
    stagehand = null;
  }
}

try {
  await withBakeoffKeyPool(async (key, pool) => {
    try {
      const env = process.env.BROWSERBASE_API_KEY && task.cloudBackendAllowed ? "BROWSERBASE" : "LOCAL";
      const modelName = process.env.STAGEHAND_MODEL || "google/gemini-2.5-flash";
      const userDataDir = path.join(profilesDir, `stagehand-${safeName(task.id)}-${key.name.toLowerCase()}`);
      fs.mkdirSync(userDataDir, { recursive: true });
      result.maskedKeyId = key.maskedKeyId;
      result.modelProvider = "google-gemini";
      result.keyPoolLoaded = pool.keysLoaded;
      result.keyPoolExhausted = false;
      result.rawBackendMetadata = {
        package: "@browserbasehq/stagehand",
        requestedEnv: env,
        model: modelName,
        userDataDir,
        cloudBackendAllowed: task.cloudBackendAllowed,
        keyAttempts: pool.attempts
      };
      result.controlledBrowserUsed = env === "LOCAL";
      result.browserUsed = env === "LOCAL" ? "local Chrome via Stagehand" : "Browserbase";
      result.browserProfileUsed = userDataDir;

      stagehand = new Stagehand({
        env,
        model: modelName,
        verbose: 1,
        localBrowserLaunchOptions: {
          headless: false,
          userDataDir
        }
      });
      await stagehand.init();

      if (stagehand.page && task.startUrl) {
        await stagehand.page.goto(task.startUrl, { waitUntil: "domcontentloaded", timeout: Math.min(task.maxRuntimeMs, 45000) });
        result.actionCount += 1;
      }

      const instruction = [
        task.taskText,
        "",
        "Safety constraints:",
        `- Allowed action level: ${task.allowedActionLevel}.`,
        "- Never checkout, pay, place an order, change address/payment settings, send email, delete, publish, or submit protected final actions.",
        task.cartMutationPreconfirmed
          ? "- Cart mutation has one upfront benchmark confirmation. Add to cart only if safe; stop at cart/add-to-cart confirmation."
          : "- Do not perform cart mutation unless the task explicitly requires and it is preconfirmed.",
        "- Stop immediately when the success criteria are met.",
        "",
        "Success criteria:",
        ...task.successCriteria.map((criterion) => `- ${criterion}`)
      ].join("\n");

      let executionSummary = null;
      if (typeof stagehand.agent === "function") {
        const agent = stagehand.agent({ model: modelName });
        const maybeResult = await agent.execute({
          instruction,
          maxSteps: 24
        });
        executionSummary = typeof maybeResult === "string" ? maybeResult : JSON.stringify(maybeResult);
      } else if (stagehand.page && typeof stagehand.page.act === "function") {
        await stagehand.page.act(instruction);
        executionSummary = "Executed Stagehand page.act instruction.";
      } else {
        throw new Error("Installed Stagehand package does not expose agent() or page.act() as expected.");
      }

      if (stagehand.page) {
        result.finalUrl = stagehand.page.url();
        await stagehand.page.screenshot({ path: screenshotPath, fullPage: false });
        result.screenshotPaths.push(screenshotPath);
      }

      const success = Boolean(result.finalUrl) && !/checkout|payment|place-order/i.test(result.finalUrl);
      const quotaBlocked = /quota|RESOURCE_EXHAUSTED|rate-limit|rate limits|PERMISSION_DENIED|403/i.test(executionSummary ?? "");
      if (quotaBlocked) {
        throw new Error(executionSummary);
      }
      finishResult(result, startTime, {
        success,
        stoppedAfterSuccess: success,
        summary: executionSummary?.slice(0, 2000) ?? "Stagehand execution completed.",
        failureReason: success ? null : "Stagehand completed but success criteria require manual review."
      });
    } catch (error) {
      await closeStagehand();
      throw error;
    }
  }, (key, pool) => {
    result.maskedKeyId = key.maskedKeyId;
    result.keyPoolLoaded = pool.keysLoaded;
  });
} catch (error) {
  const exhausted = error instanceof ExhaustedBakeoffKeyError;
  finishResult(result, startTime, {
    success: false,
    stoppedAfterSuccess: false,
    keyPoolExhausted: exhausted ? true : result.keyPoolExhausted,
    failureReason: exhausted ? `${error.message} Attempts: ${JSON.stringify(error.attempts)}` : redactError(error)
  });
} finally {
  await closeStagehand();
  writeJson(resultPath, result);
  console.log(JSON.stringify({ resultPath, success: result.success, runtimeMs: result.runtimeMs, finalUrl: result.finalUrl }, null, 2));
  if (!result.success) process.exitCode = 2;
}
