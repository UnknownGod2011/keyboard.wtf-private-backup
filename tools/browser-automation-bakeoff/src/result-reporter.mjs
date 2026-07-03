import fs from "node:fs";
import path from "node:path";
import { ensureDirs, resultsDir, rootDir, writeJson } from "./common.mjs";

ensureDirs();

const resultFiles = fs.existsSync(resultsDir)
  ? fs.readdirSync(resultsDir).filter((name) => name.endsWith(".json") && name !== "results.json" && !name.endsWith("-summary.json") && name !== "benchmark-runner-summary.json")
  : [];

const results = [];
for (const name of resultFiles) {
  const filePath = path.join(resultsDir, name);
  try {
    const value = JSON.parse(fs.readFileSync(filePath, "utf8"));
    if (Array.isArray(value)) results.push(...value);
    else results.push(value);
  } catch (error) {
    results.push({
      backendName: "reporter",
      taskId: name,
      success: false,
      failureReason: `Could not parse result: ${error.message}`
    });
  }
}

writeJson(path.join(resultsDir, "results.json"), results);

const lines = [];
lines.push("# Browser automation bake-off report");
lines.push("");
lines.push(`Generated: ${new Date().toISOString()}`);
lines.push("");
lines.push("| Backend | Task | Status | Runtime ms | Final URL | Confirmations | Failure / Summary |");
lines.push("|---|---|---:|---:|---|---:|---|");
for (const result of results.sort((a, b) => `${a.backendName}:${a.taskId}`.localeCompare(`${b.backendName}:${b.taskId}`))) {
  const note = (result.failureReason ?? result.summary ?? "").replaceAll("|", "\\|").replace(/\s+/g, " ").slice(0, 500);
  lines.push(`| ${result.backendName ?? ""} | ${result.taskId ?? ""} | ${result.success ? "PASS" : "FAIL"} | ${result.runtimeMs ?? ""} | ${result.finalUrl ?? ""} | ${result.confirmationCount ?? ""} | ${note} |`);
}
lines.push("");
lines.push("## Notes");
lines.push("");
lines.push("- This report is generated from isolated bake-off result files.");
lines.push("- It does not imply integration into the keyboard.wtf product route.");
lines.push("- Secrets are not included. Screenshots and traces are intentionally ignored by Git.");

fs.writeFileSync(path.join(resultsDir, "report.md"), `${lines.join("\n")}\n`, "utf8");
console.log(`Wrote ${path.relative(rootDir, path.join(resultsDir, "results.json"))}`);
console.log(`Wrote ${path.relative(rootDir, path.join(resultsDir, "report.md"))}`);
