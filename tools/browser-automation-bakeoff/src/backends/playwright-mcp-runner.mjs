import fs from "node:fs";
import path from "node:path";
import { GoogleGenAI } from "@google/genai";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
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
  writeJson
} from "../common.mjs";

ensureDirs();
loadLocalEnv();

const args = parseArgs();
const task = selectTasks(args)[0];
if (!task) {
  console.error("No task selected.");
  process.exit(1);
}

const startTime = Date.now();
const result = makeBaseResult("playwright-mcp", task);
const resultPath = path.join(resultsDir, `playwright-mcp-${safeName(task.id)}.json`);
const screenshotPath = path.join(screenshotsDir, `playwright-mcp-${safeName(task.id)}-final.png`);
let client = null;
let transport = null;

function contentToText(content) {
  if (!content) return "";
  if (typeof content === "string") return content;
  if (Array.isArray(content)) {
    return content.map((part) => {
      if (typeof part?.text === "string") return part.text;
      if (typeof part?.data === "string") return `[${part.type ?? "data"}:${part.data.length}]`;
      return JSON.stringify(part);
    }).join("\n");
  }
  return JSON.stringify(content);
}

function compactTools(tools) {
  return tools.map((tool) => ({
    name: tool.name,
    description: tool.description,
    inputSchema: tool.inputSchema
  }));
}

async function callTool(client, name, args = {}) {
  const response = await client.callTool({ name, arguments: args });
  return contentToText(response.content);
}

function extractJson(text) {
  const trimmed = text.trim();
  const fenced = trimmed.match(/```(?:json)?\s*([\s\S]*?)```/i);
  const candidate = fenced ? fenced[1] : trimmed;
  const start = candidate.indexOf("{");
  const end = candidate.lastIndexOf("}");
  if (start === -1 || end === -1 || end <= start) throw new Error(`Planner did not return JSON: ${trimmed.slice(0, 500)}`);
  return JSON.parse(candidate.slice(start, end + 1));
}

try {
  if (!process.env.GOOGLE_API_KEY && !process.env.GEMINI_API_KEY) {
    throw new Error("GOOGLE_API_KEY/GEMINI_API_KEY is required for the Playwright MCP planner.");
  }
  const ai = new GoogleGenAI({ apiKey: process.env.GOOGLE_API_KEY || process.env.GEMINI_API_KEY });
  const model = process.env.PLAYWRIGHT_MCP_MODEL || "gemini-2.5-flash";
  const userDataDir = path.join(profilesDir, `playwright-mcp-${safeName(task.id)}`);
  fs.mkdirSync(userDataDir, { recursive: true });

  result.rawBackendMetadata = {
    package: "@playwright/mcp",
    model,
    userDataDir,
    command: "npx @playwright/mcp@latest --browser=chrome --user-data-dir <profile>"
  };
  result.browserProfileUsed = userDataDir;
  result.controlledBrowserUsed = true;

  transport = new StdioClientTransport({
    command: process.platform === "win32" ? "npx.cmd" : "npx",
    args: ["@playwright/mcp@latest", "--browser=chrome", "--user-data-dir", userDataDir]
  });
  client = new Client({ name: "keyboard-wtf-bakeoff", version: "0.1.0" }, { capabilities: {} });
  await client.connect(transport);

  const toolsResponse = await client.listTools();
  const tools = toolsResponse.tools ?? [];
  result.rawBackendMetadata.tools = tools.map((tool) => tool.name);

  if (task.startUrl && tools.some((tool) => tool.name === "browser_navigate")) {
    await callTool(client, "browser_navigate", { url: task.startUrl });
    result.actionCount += 1;
  }

  let finalAnswer = null;
  let lastSnapshot = "";
  for (let step = 0; step < 24; step += 1) {
    const snapshotTool = tools.find((tool) => tool.name === "browser_snapshot");
    if (!snapshotTool) throw new Error("Playwright MCP server did not expose browser_snapshot.");
    lastSnapshot = await callTool(client, "browser_snapshot", {});

    const prompt = [
      "You are controlling a real browser through Playwright MCP. Choose one next MCP tool call or return a final answer.",
      "Return strict JSON only.",
      "",
      `Task: ${task.taskText}`,
      `Allowed action level: ${task.allowedActionLevel}`,
      `Cart mutation preconfirmed: ${Boolean(task.cartMutationPreconfirmed)}`,
      "",
      "Safety:",
      "- Never checkout, pay, place an order, alter address/payment settings, send email, delete, publish, or complete protected final submissions.",
      "- For cart tasks, adding requested items to cart is allowed only when preconfirmed; stop at cart/add-to-cart confirmation.",
      "- Stop immediately when success criteria are met.",
      "",
      "Success criteria:",
      ...task.successCriteria.map((criterion) => `- ${criterion}`),
      "",
      "Available tools:",
      JSON.stringify(compactTools(tools), null, 2).slice(0, 12000),
      "",
      "Current accessibility snapshot:",
      lastSnapshot.slice(0, 22000),
      "",
      "Output schema:",
      "{\"status\":\"tool\",\"tool\":\"browser_click\",\"arguments\":{\"element\":\"...\",\"ref\":\"...\"},\"reason\":\"...\"}",
      "or",
      "{\"status\":\"final\",\"success\":true,\"summary\":\"...\",\"finalUrlGuess\":\"...\"}",
      "or",
      "{\"status\":\"final\",\"success\":false,\"summary\":\"...\",\"failureReason\":\"...\"}"
    ].join("\n");

    const response = await ai.models.generateContent({
      model,
      contents: prompt
    });
    result.llmCallCount = (result.llmCallCount ?? 0) + 1;
    const decision = extractJson(response.text ?? "");
    if (decision.status === "final") {
      finalAnswer = decision;
      break;
    }
    if (decision.status !== "tool" || !decision.tool) {
      throw new Error(`Planner returned unsupported decision: ${JSON.stringify(decision)}`);
    }
    if (!tools.some((tool) => tool.name === decision.tool)) {
      throw new Error(`Planner selected unavailable MCP tool: ${decision.tool}`);
    }
    const toolText = await callTool(client, decision.tool, decision.arguments ?? {});
    result.actionCount += 1;
    if (/checkout|payment|place order/i.test(toolText)) {
      finalAnswer = { success: false, failureReason: "Safety stop: checkout/payment/order flow detected.", summary: toolText.slice(0, 1000) };
      break;
    }
  }

  if (tools.some((tool) => tool.name === "browser_take_screenshot")) {
    try {
      await callTool(client, "browser_take_screenshot", { filename: screenshotPath, fullPage: false });
      result.screenshotPaths.push(screenshotPath);
    } catch {
      // Some MCP versions return binary data instead of writing filename. Missing screenshot is recorded by absence.
    }
  }

  if (tools.some((tool) => tool.name === "browser_evaluate")) {
    try {
      const urlText = await callTool(client, "browser_evaluate", { function: "() => location.href" });
      result.finalUrl = urlText.trim().split(/\s+/).find((part) => /^https?:\/\//.test(part)) ?? urlText.trim();
    } catch {
      result.finalUrl = finalAnswer?.finalUrlGuess ?? null;
    }
  } else {
    result.finalUrl = finalAnswer?.finalUrlGuess ?? null;
  }

  finishResult(result, startTime, {
    success: Boolean(finalAnswer?.success),
    stoppedAfterSuccess: Boolean(finalAnswer),
    summary: finalAnswer?.summary ?? "Step budget exhausted without final answer.",
    failureReason: finalAnswer?.success ? null : (finalAnswer?.failureReason ?? "Step budget exhausted or success criteria not met."),
    rawBackendMetadata: {
      ...result.rawBackendMetadata,
      lastSnapshotChars: lastSnapshot.length
    }
  });
} catch (error) {
  finishResult(result, startTime, {
    success: false,
    stoppedAfterSuccess: false,
    failureReason: redactError(error)
  });
} finally {
  try {
    if (client) await client.close();
  } catch {
    // ignore cleanup
  }
  try {
    if (transport) await transport.close();
  } catch {
    // ignore cleanup
  }
}

writeJson(resultPath, result);
console.log(JSON.stringify({ resultPath, success: result.success, runtimeMs: result.runtimeMs, finalUrl: result.finalUrl }, null, 2));
process.exit(result.success ? 0 : 2);
