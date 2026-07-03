from __future__ import annotations

import argparse
import asyncio
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
    raw = re.sub(r"(GEMINI|GOOGLE|BROWSERBASE|OPENAI|ANTHROPIC)(_GENERATIVE_AI)?_API_KEY=\S+", r"\1_API_KEY=[REDACTED]", raw, flags=re.I)
    return raw


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
        "browserProfileUsed": None,
        "realChromeUsed": False,
        "controlledBrowserUsed": True,
        "wrongBrowserOpened": False,
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
    profile_dir = PROFILES_DIR / f"browser-use-{safe_name(task['id'])}"
    profile_dir.mkdir(parents=True, exist_ok=True)
    result["browserProfileUsed"] = str(profile_dir)

    try:
        from browser_use import Agent, ChatGoogle
    except Exception as exc:  # pragma: no cover - runtime setup evidence
        finish(result, start, failureReason=f"browser-use import failed: {redact(exc)}", stoppedAfterSuccess=False)
        result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
        return result

    if not (os.environ.get("GOOGLE_API_KEY") or os.environ.get("GEMINI_API_KEY")):
        finish(result, start, failureReason="GOOGLE_API_KEY/GEMINI_API_KEY missing for browser-use ChatGoogle.", stoppedAfterSuccess=False)
        result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
        return result

    # Prefer GEMINI_API_KEY for this bake-off if both aliases exist; key health
    # is tracked separately and values are never printed.
    if os.environ.get("GEMINI_API_KEY"):
        os.environ["GOOGLE_API_KEY"] = os.environ["GEMINI_API_KEY"]

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

    try:
        llm = ChatGoogle(model=model)
        agent = Agent(
            task=instruction,
            llm=llm,
            directly_open_url=False,
            max_failures=4,
        )
        history = await asyncio.wait_for(agent.run(max_steps=24), timeout=task.get("maxRuntimeMs", 120000) / 1000)
        summary = str(history)
        errors = history.errors()
        final_result = history.final_result()
        final_url = None
        urls = history.urls()
        if urls:
            final_url = urls[-1]
        success = bool(history.is_done() and history.is_successful())
        finish(
            result,
            start,
            success=success,
            finalUrl=final_url,
            stoppedAfterSuccess=True,
            actionCount=history.number_of_steps(),
            summary=(final_result or summary)[:4000],
            rawBackendMetadata={
                **result["rawBackendMetadata"],
                "errors": [redact(error) for error in errors[-5:]],
                "isDone": history.is_done(),
                "isSuccessful": history.is_successful(),
                "isValidated": history.is_validated(),
            },
            failureReason=None if success else "browser-use did not report a successful completed task.",
        )
    except Exception as exc:
        reason = redact(exc)
        if not reason.strip():
            reason = f"{type(exc).__name__} after {task.get('maxRuntimeMs', 120000)}ms"
        finish(result, start, success=False, stoppedAfterSuccess=False, failureReason=reason)

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
