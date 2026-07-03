import path from "node:path";
import { ensureDirs, loadLocalEnv, parseArgs, rootDir, runProcess, selectTasks, writeJson, resultsDir } from "./common.mjs";

ensureDirs();
loadLocalEnv();

const args = parseArgs();
const requestedBackends = args.backend?.length ? args.backend : ["stagehand", "playwright-mcp"];
const tasks = selectTasks(args);
const allResults = [];

const backendCommands = {
  "stagehand": (task) => ["node", ["src/backends/stagehand-runner.mjs", "--task", task.id]],
  "playwright-mcp": (task) => ["node", ["src/backends/playwright-mcp-runner.mjs", "--task", task.id]]
};

for (const backend of requestedBackends) {
  if (!backendCommands[backend]) {
    allResults.push({
      backendName: backend,
      success: false,
      failureReason: `Unknown backend: ${backend}`
    });
    continue;
  }
  for (const task of tasks) {
    console.log(`[${backend}] ${task.id}`);
    const [command, commandArgs] = backendCommands[backend](task);
    const result = await runProcess(command, commandArgs, { cwd: rootDir, echo: true });
    if (result.code !== 0) {
      allResults.push({
        backendName: backend,
        taskId: task.id,
        success: false,
        failureReason: `Runner exited ${result.code}`,
        logsPath: null
      });
    }
  }
}

writeJson(path.join(resultsDir, "benchmark-runner-summary.json"), allResults);

