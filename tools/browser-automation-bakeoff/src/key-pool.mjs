import dotenv from "dotenv";
import path from "node:path";
import { rootDir } from "./common.mjs";

export class ExhaustedBakeoffKeyError extends Error {
  constructor(message, attempts = []) {
    super(message);
    this.name = "ExhaustedBakeoffKeyError";
    this.attempts = attempts;
  }
}

export function loadBakeoffEnv() {
  dotenv.config({ path: path.join(rootDir, ".env.local"), quiet: true });
  dotenv.config({ path: path.join(rootDir, ".env"), quiet: true });
}

export function maskKey(value) {
  if (!value) return "";
  return `${value.slice(0, Math.min(4, value.length))}...${value.slice(Math.max(0, value.length - 4))}`;
}

export function loadBakeoffKeys() {
  loadBakeoffEnv();
  return Object.entries(process.env)
    .filter(([name, value]) => /^BAKEOFF_GEMINI_API_KEY_\d+$/i.test(name) && value)
    .sort(([a], [b]) => a.localeCompare(b, undefined, { numeric: true }))
    .map(([name, value], index) => ({
      name,
      index,
      value,
      maskedKeyId: `${name}:${maskKey(value)}`
    }));
}

export function installBakeoffKey(key) {
  process.env.GOOGLE_API_KEY = key.value;
  process.env.GEMINI_API_KEY = key.value;
  process.env.GOOGLE_GENERATIVE_AI_API_KEY = key.value;
  return key.maskedKeyId;
}

export function classifyGeminiFailure(errorOrText) {
  const text = String(errorOrText?.message ?? errorOrText ?? "");
  if (/RESOURCE_EXHAUSTED|429|quota|rate.?limit|capacity|overloaded|503|UNAVAILABLE/i.test(text)) {
    return "quota_or_capacity";
  }
  if (/PERMISSION_DENIED|API key not valid|invalid api key|403|ByteString|greater than 255|API_KEY_INVALID/i.test(text)) {
    return "invalid_or_denied";
  }
  return null;
}

export async function withBakeoffKeyPool(runOnce, onAttempt) {
  const keys = loadBakeoffKeys();
  if (!keys.length) throw new ExhaustedBakeoffKeyError("No BAKEOFF_GEMINI_API_KEY_* values loaded.");

  const attempts = [];
  for (const key of keys) {
    installBakeoffKey(key);
    onAttempt?.(key, { keysLoaded: keys.length, attempts });
    try {
      return await runOnce(key, { keysLoaded: keys.length, attempts });
    } catch (error) {
      const classification = classifyGeminiFailure(error);
      attempts.push({
        key: key.maskedKeyId,
        classification: classification ?? "non_retryable",
        message: String(error?.message ?? error ?? "")
          .replace(/AQ\.Ab8[0-9A-Za-z._-]+/g, "[REDACTED_API_KEY]")
          .replace(/AIza[0-9A-Za-z._—-]+/g, "[REDACTED_API_KEY]")
          .slice(0, 500)
      });
      if (!classification) throw error;
    }
  }

  throw new ExhaustedBakeoffKeyError("All bake-off Gemini keys were exhausted or denied.", attempts);
}
