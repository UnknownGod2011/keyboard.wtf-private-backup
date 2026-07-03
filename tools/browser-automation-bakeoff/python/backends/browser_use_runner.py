from __future__ import annotations

import argparse
import asyncio
import base64
import json
import os
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from dotenv import load_dotenv


ROOT = Path(__file__).resolve().parents[2]
TASKS_PATH = ROOT / "tasks.json"
RESULTS_DIR = ROOT / "results"
SCREENSHOTS_DIR = ROOT / "screenshots"
PROFILES_DIR = ROOT / "profiles"


def ensure_dirs() -> None:
    for directory in (RESULTS_DIR, SCREENSHOTS_DIR, PROFILES_DIR):
        directory.mkdir(parents=True, exist_ok=True)


def safe_name(value: str) -> str:
    return re.sub(r"[^a-z0-9._-]+", "-", value, flags=re.I).strip("-").lower()


def redact(text: Any) -> str:
    raw = str(text)
    raw = re.sub(r"AQ\.Ab8[0-9A-Za-z._-]+", "[REDACTED_API_KEY]", raw)
    raw = re.sub(r"AIza[0-9A-Za-z._—-]+", "[REDACTED_API_KEY]", raw)
    raw = re.sub(r"(GEMINI|GOOGLE|BROWSERBASE|OPENAI|ANTHROPIC)(_GENERATIVE_AI)?_API_KEY=\S+", r"\1_API_KEY=[REDACTED]", raw, flags=re.I)
    return raw


def mask_key(value: str) -> str:
    return f"{value[:4]}...{value[-4:]}"


def load_bakeoff_keys() -> list[dict[str, str]]:
    keys: list[dict[str, str]] = []
    for name, value in sorted(os.environ.items()):
        if re.match(r"^BAKEOFF_GEMINI_API_KEY_\d+$", name, flags=re.I) and value:
            keys.append({"name": name, "value": value, "maskedKeyId": f"{name}:{mask_key(value)}"})
    return keys


def install_bakeoff_key(key: dict[str, str]) -> None:
    os.environ["GOOGLE_API_KEY"] = key["value"]
    os.environ["GEMINI_API_KEY"] = key["value"]
    os.environ["GOOGLE_GENERATIVE_AI_API_KEY"] = key["value"]


def classify_gemini_failure(value: Any) -> str | None:
    text = str(value)
    if re.search(r"RESOURCE_EXHAUSTED|429|quota|rate.?limit|capacity|overloaded|503|UNAVAILABLE", text, flags=re.I):
        return "quota_or_capacity"
    if re.search(r"PERMISSION_DENIED|API key not valid|invalid api key|403", text, flags=re.I):
        return "invalid_or_denied"
    return None


def load_tasks() -> list[dict[str, Any]]:
    return json.loads(TASKS_PATH.read_text(encoding="utf-8"))


def make_result(task: dict[str, Any]) -> dict[str, Any]:
    return {
        "backendName": "browser-use",
        "taskId": task["id"],
        "taskText": task["taskText"],
        "targetSite": task.get("targetSite"),
        "startedAt": datetime.now(timezone.utc).isoformat(),
        "endedAt": None,
        "success": False,
        "finalUrl": None,
        "screenshotPaths": [],
        "runtimeMs": None,
        "actionCount": None,
        "llmCallCount": None,
        "confirmationCount": 1 if task.get("cartMutationPreconfirmed") else 0,
        "maskedKeyId": None,
        "modelProvider": None,
        "keyPoolLoaded": None,
        "keyPoolExhausted": None,
        "browserProfileUsed": None,
        "browserUsed": "local browser-use controlled browser",
        "realChromeUsed": False,
        "controlledBrowserUsed": True,
        "wrongBrowserOpened": False,
        "looped": None,
        "manualBlocker": None,
        "stoppedAfterSuccess": None,
        "failureReason": None,
        "logsPath": None,
        "tracesPath": None,
        "summary": None,
        "rawBackendMetadata": {
            "package": "browser-use",
            "model": os.environ.get("BROWSER_USE_MODEL", "gemini-2.5-flash"),
        },
    }


def finish(result: dict[str, Any], start: float, **patch: Any) -> dict[str, Any]:
    result.update(patch)
    result["endedAt"] = datetime.now(timezone.utc).isoformat()
    result["runtimeMs"] = int((time.time() - start) * 1000)
    return result


async def run_task(task: dict[str, Any]) -> dict[str, Any]:
    start = time.time()
    result = make_result(task)
    result_path = RESULTS_DIR / f"browser-use-{safe_name(task['id'])}.json"
    try:
        from browser_use import Agent, ChatGoogle
    except Exception as exc:  # pragma: no cover - runtime setup evidence
        finish(result, start, failureReason=f"browser-use import failed: {redact(exc)}", stoppedAfterSuccess=False)
        result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
        return result

    bakeoff_keys = load_bakeoff_keys()
    result["keyPoolLoaded"] = len(bakeoff_keys)
    if not bakeoff_keys:
        finish(result, start, failureReason="No BAKEOFF_GEMINI_API_KEY_* values loaded for browser-use.", stoppedAfterSuccess=False, keyPoolExhausted=True)
        result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
        return result

    model = os.environ.get("BROWSER_USE_MODEL", "gemini-2.5-flash")
    instruction = "\n".join(
        [
            task["taskText"],
            "",
            "Safety constraints:",
            f"- Allowed action level: {task.get('allowedActionLevel')}.",
            "- Never checkout, pay, place an order, change address/payment settings, send email, delete, publish, or submit protected final actions.",
            "- Stop immediately when the success criteria are met.",
            "- If blocked by login, CAPTCHA, Cloudflare, site protection, unavailable item, or missing video/form control, report the exact blocker.",
            "- Do not claim success unless the real website success condition is visible or verifiable.",
            "- Do not use stale memory, previous URLs, or cached final links.",
            "- Use the requested entity/product/site exactly.",
            "- For cart tasks, cart mutation has one upfront benchmark confirmation only if cartMutationPreconfirmed is true; stop at cart/add-to-cart confirmation.",
            "",
            "Success criteria:",
            *[f"- {criterion}" for criterion in task.get("successCriteria", [])],
        ]
    )

    attempts: list[dict[str, str]] = []
    for key in bakeoff_keys:
        install_bakeoff_key(key)
        result["maskedKeyId"] = key["maskedKeyId"]
        result["modelProvider"] = "google-gemini"
        profile_dir = PROFILES_DIR / f"browser-use-{safe_name(task['id'])}-{key['name'].lower()}"
        profile_dir.mkdir(parents=True, exist_ok=True)
        result["browserProfileUsed"] = str(profile_dir)

        llm = ChatGoogle(model=model)
        agent = Agent(
            task=instruction,
            llm=llm,
            directly_open_url=False,
            max_failures=4,
        )
        try:
            history = await asyncio.wait_for(agent.run(max_steps=24), timeout=task.get("maxRuntimeMs", 120000) / 1000)
            summary = str(history)
            errors = history.errors()
            error_text = "\n".join(str(error) for error in errors)
            classification = classify_gemini_failure(error_text)
            if classification and not history.is_done():
                attempts.append({"key": key["maskedKeyId"], "classification": classification, "message": redact(error_text)[-500:]})
                continue

            final_result = history.final_result()
            final_url = None
            urls = history.urls()
            if urls:
                final_url = urls[-1]
            success = bool(history.is_done() and history.is_successful())

            screenshot_paths: list[str] = []
            try:
                for screenshot_path in history.screenshot_paths() or []:
                    if screenshot_path and Path(screenshot_path).exists():
                        screenshot_paths.append(str(Path(screenshot_path).resolve()))
            except Exception:
                pass
            try:
                screenshots = history.screenshots() or []
                if screenshots:
                    final_screenshot = SCREENSHOTS_DIR / f"browser-use-{safe_name(task['id'])}-final.png"
                    base64.b64decode(screenshots[-1].split(",", 1)[-1])
                    final_screenshot.write_bytes(base64.b64decode(screenshots[-1].split(",", 1)[-1]))
                    screenshot_paths.append(str(final_screenshot.resolve()))
            except Exception:
                pass

            finish(
                result,
                start,
                success=success,
                finalUrl=final_url,
                screenshotPaths=screenshot_paths,
                stoppedAfterSuccess=bool(history.is_done()),
                looped=(not history.is_done()),
                actionCount=history.number_of_steps(),
                summary=(final_result or summary)[:4000],
                rawBackendMetadata={
                    **result["rawBackendMetadata"],
                    "errors": [redact(error) for error in errors[-5:]],
                    "keyAttempts": attempts,
                    "isDone": history.is_done(),
                    "isSuccessful": history.is_successful(),
                    "isValidated": history.is_validated(),
                },
                failureReason=None if success else "browser-use did not report a successful completed task.",
            )
            break
        except Exception as exc:
            classification = classify_gemini_failure(exc)
            if classification:
                attempts.append({"key": key["maskedKeyId"], "classification": classification, "message": redact(exc)[-500:]})
                continue
            reason = redact(exc)
            if not reason.strip():
                reason = f"{type(exc).__name__} after {task.get('maxRuntimeMs', 120000)}ms"
            finish(result, start, success=False, stoppedAfterSuccess=False, failureReason=reason, rawBackendMetadata={**result["rawBackendMetadata"], "keyAttempts": attempts})
            break

    if result["endedAt"] is None:
        finish(
            result,
            start,
            success=False,
            stoppedAfterSuccess=False,
            keyPoolExhausted=True,
            failureReason=f"All bake-off Gemini keys exhausted or denied. Attempts: {json.dumps(attempts)}",
            rawBackendMetadata={**result["rawBackendMetadata"], "keyAttempts": attempts},
        )

    result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


async def main() -> int:
    ensure_dirs()
    load_dotenv(ROOT / ".env.local")
    load_dotenv(ROOT / ".env")

    parser = argparse.ArgumentParser()
    parser.add_argument("--task", action="append", default=[])
    parser.add_argument("--all", action="store_true")
    args = parser.parse_args()

    tasks = load_tasks()
    if args.task:
        wanted = set(args.task)
        tasks = [task for task in tasks if task["id"] in wanted]

    results = []
    for task in tasks:
        print(f"[browser-use] {task['id']}", flush=True)
        result = await run_task(task)
        print(json.dumps({"taskId": result["taskId"], "success": result["success"], "runtimeMs": result["runtimeMs"], "finalUrl": result["finalUrl"]}, indent=2), flush=True)
        results.append(result)

    (RESULTS_DIR / "browser-use-summary.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    return 0 if all(result["success"] for result in results) else 2


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
