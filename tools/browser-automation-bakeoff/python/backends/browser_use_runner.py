from __future__ import annotations

import argparse
import asyncio
import base64
import json
import os
import re
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
TRACES_DIR = ROOT / "traces"
LOGS_DIR = ROOT / "logs"
SHARED_BROWSER: Any | None = None


def ensure_dirs() -> None:
    for directory in (RESULTS_DIR, SCREENSHOTS_DIR, PROFILES_DIR, TRACES_DIR, LOGS_DIR):
        directory.mkdir(parents=True, exist_ok=True)


def safe_name(value: str) -> str:
    return re.sub(r"[^a-z0-9._-]+", "-", value, flags=re.I).strip("-").lower()


def redact(text: Any) -> str:
    raw = str(text)
    raw = re.sub(r"AQ\.Ab8[0-9A-Za-z._-]+", "[REDACTED_API_KEY]", raw)
    raw = re.sub(r"AIza[0-9A-Za-z._\-\u2014]+", "[REDACTED_API_KEY]", raw)
    raw = re.sub(
        r"(GEMINI|GOOGLE|BROWSERBASE|BROWSER_USE|OPENAI|ANTHROPIC)(_GENERATIVE_AI)?_API_KEY=\S+",
        r"\1_API_KEY=[REDACTED]",
        raw,
        flags=re.I,
    )
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


def classify_model_failure(value: Any) -> str | None:
    text = str(value)
    if re.search(r"RESOURCE_EXHAUSTED|429|quota|rate.?limit|capacity|overloaded|credits?|free.?tier|503|UNAVAILABLE", text, flags=re.I):
        return "quota_or_capacity"
    if re.search(r"PERMISSION_DENIED|API key not valid|invalid api key|unauthorized|authentication|401|403|ByteString|greater than 255|API_KEY_INVALID", text, flags=re.I):
        return "invalid_or_denied"
    return None


def load_tasks() -> list[dict[str, Any]]:
    return json.loads(TASKS_PATH.read_text(encoding="utf-8"))


def make_result(task: dict[str, Any], provider: str, run_mode: str) -> dict[str, Any]:
    model = os.environ.get("BROWSER_USE_MODEL", "gemini-2.5-flash") if provider == "gemini" else os.environ.get("BROWSER_USE_AGENT_MODEL", "bu-2-0")
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
        "browserUsed": "browser-use controlled Chrome profile",
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
        "runTemperature": run_mode,
        "rawBackendMetadata": {
            "package": "browser-use",
            "provider": provider,
            "model": model,
            "usesBrowserUseAgentApi": provider == "browser-use",
            "profileMode": "controlled-persistent-profile",
        },
    }


def finish(result: dict[str, Any], start: float, **patch: Any) -> dict[str, Any]:
    result.update(patch)
    result["endedAt"] = datetime.now(timezone.utc).isoformat()
    result["runtimeMs"] = int((time.time() - start) * 1000)
    return result


def make_profile_dir(task: dict[str, Any], key_name: str, provider: str, run_mode: str) -> Path:
    if run_mode == "warm":
        return PROFILES_DIR / f"browser-use-{provider}-warm-shared"
    mode_suffix = f"cold-{int(time.time())}"
    return PROFILES_DIR / f"browser-use-{provider}-{safe_name(task['id'])}-{key_name.lower()}-{mode_suffix}"


def build_instruction(task: dict[str, Any]) -> str:
    return "\n".join(
        [
            "Complete this real browser task on the live website, then stop.",
            "",
            f"Task: {task['taskText']}",
            "",
            "Operational rules:",
            "- Use the requested entity/product/site exactly. Do not substitute stale examples.",
            "- Prefer direct page controls and visible page evidence.",
            "- Do not loop. If the same action/search fails twice, report the blocker.",
            "- Stop immediately when the success criteria are met.",
            "- Do not use old memory, previous URLs, or cached final links.",
            "",
            "Safety constraints:",
            f"- Allowed action level: {task.get('allowedActionLevel')}.",
            "- Search, read/extract, open links/pages, inspect CTA, and open videos are allowed without confirmation.",
            "- Never checkout, pay, place an order, change address/payment settings, send email, delete, publish, or submit protected final actions.",
            "- If blocked by login, CAPTCHA, Cloudflare, site protection, unavailable item, or missing video/form control, report the exact blocker.",
            "- Do not claim success unless the real website success condition is visible or verifiable.",
            "- For cart tasks, cart mutation has one upfront benchmark confirmation only if cartMutationPreconfirmed is true; stop at cart/add-to-cart confirmation.",
            "",
            "Success criteria:",
            *[f"- {criterion}" for criterion in task.get("successCriteria", [])],
        ]
    )


def provider_keys(provider: str) -> list[dict[str, str]]:
    if provider == "browser-use":
        keys: list[dict[str, str]] = []
        for name, value in sorted(os.environ.items()):
            if re.match(r"^BROWSER_USE_API_KEY_\d+$", name, flags=re.I) and value:
                keys.append({"name": name, "value": value, "maskedKeyId": f"{name}:{mask_key(value)}"})
        browser_use_key = os.environ.get("BROWSER_USE_API_KEY", "")
        if browser_use_key:
            keys.append({"name": "BROWSER_USE_API_KEY", "value": browser_use_key, "maskedKeyId": f"BROWSER_USE_API_KEY:{mask_key(browser_use_key)}"})
        return keys
    return load_bakeoff_keys()


async def close_browser(browser: Any) -> None:
    try:
        await browser.close()
    except Exception:
        try:
            await browser.stop()
        except Exception:
            pass


async def get_browser(Browser: Any, BrowserProfile: Any, profile_dir: Path, trace_dir: Path, run_mode: str, real_chrome: bool) -> Any:
    global SHARED_BROWSER
    if run_mode == "warm" and SHARED_BROWSER is not None:
        return SHARED_BROWSER

    if real_chrome:
        browser = Browser.from_system_chrome(
            profile_directory=os.environ.get("BROWSER_USE_CHROME_PROFILE", "Default"),
            keep_alive=run_mode == "warm",
            traces_dir=str(trace_dir),
            highlight_elements=True,
        )
    else:
        browser_profile = BrowserProfile(
            headless=False,
            user_data_dir=str(profile_dir),
            traces_dir=str(trace_dir),
            highlight_elements=True,
            wait_between_actions=0.1,
            minimum_wait_page_load_time=0.25,
            wait_for_network_idle_page_load_time=0.5,
            keep_alive=run_mode == "warm",
        )
        browser = Browser(browser_profile=browser_profile)

    if run_mode == "warm":
        SHARED_BROWSER = browser
    return browser


async def run_task(task: dict[str, Any], provider: str, run_mode: str, real_chrome: bool = False) -> dict[str, Any]:
    start = time.time()
    result = make_result(task, provider, run_mode)
    result_path = RESULTS_DIR / f"browser-use-{provider}-{run_mode}-{safe_name(task['id'])}.json"
    try:
        from browser_use import Agent, Browser, BrowserProfile, ChatBrowserUse, ChatGoogle
    except Exception as exc:  # pragma: no cover - runtime setup evidence
        finish(result, start, failureReason=f"browser-use import failed: {redact(exc)}", stoppedAfterSuccess=False)
        result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
        return result

    keys = provider_keys(provider)
    result["keyPoolLoaded"] = len(keys)
    if not keys:
        reason = "Browser Use API key not configured." if provider == "browser-use" else "No BAKEOFF_GEMINI_API_KEY_* values loaded for browser-use."
        finish(result, start, failureReason=reason, stoppedAfterSuccess=False, keyPoolExhausted=(provider != "browser-use"))
        result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
        return result

    instruction = build_instruction(task)
    attempts: list[dict[str, str]] = []
    for key in keys:
        if provider == "gemini":
            install_bakeoff_key(key)
            result["modelProvider"] = "google-gemini"
        else:
            result["modelProvider"] = "browser-use"
        result["maskedKeyId"] = key["maskedKeyId"]

        profile_dir = make_profile_dir(task, key["name"], provider, run_mode)
        profile_dir.mkdir(parents=True, exist_ok=True)
        trace_dir = TRACES_DIR / f"browser-use-{provider}-{run_mode}-{safe_name(task['id'])}"
        trace_dir.mkdir(parents=True, exist_ok=True)
        result["browserProfileUsed"] = str(profile_dir)
        result["tracesPath"] = str(trace_dir)
        result["browserUsed"] = "browser-use real system Chrome profile" if real_chrome else "browser-use controlled Chrome profile"
        result["realChromeUsed"] = real_chrome
        result["controlledBrowserUsed"] = not real_chrome

        if provider == "browser-use":
            llm = ChatBrowserUse(model=os.environ.get("BROWSER_USE_AGENT_MODEL", "bu-2-0"), api_key=key["value"])
        else:
            llm = ChatGoogle(model=os.environ.get("BROWSER_USE_MODEL", "gemini-2.5-flash"))

        browser = await get_browser(Browser, BrowserProfile, profile_dir, trace_dir, run_mode, real_chrome)
        log_path = LOGS_DIR / f"browser-use-{provider}-{run_mode}-{safe_name(task['id'])}-{key['name'].lower()}.json"
        result["logsPath"] = str(log_path)

        agent = Agent(
            task=instruction,
            llm=llm,
            browser=browser,
            directly_open_url=True,
            max_failures=3,
            max_actions_per_step=4,
            max_history_items=12,
            step_timeout=60,
            loop_detection_enabled=True,
            loop_detection_window=12,
            final_response_after_failure=True,
            use_vision="auto",
            planning_replan_on_stall=2,
            save_conversation_path=str(log_path),
        )

        try:
            history = await asyncio.wait_for(agent.run(max_steps=24), timeout=task.get("maxRuntimeMs", 120000) / 1000)
            errors = history.errors()
            error_text = "\n".join(str(error) for error in errors)
            classification = classify_model_failure(error_text)
            if classification and not history.is_done():
                attempts.append({"key": key["maskedKeyId"], "classification": classification, "message": redact(error_text)[-500:]})
                continue

            final_result = history.final_result()
            urls = history.urls()
            final_url = urls[-1] if urls else None
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
                    final_screenshot = SCREENSHOTS_DIR / f"browser-use-{provider}-{run_mode}-{safe_name(task['id'])}-final.png"
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
                summary=(final_result or str(history))[:4000],
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
            classification = classify_model_failure(exc)
            if classification:
                attempts.append({"key": key["maskedKeyId"], "classification": classification, "message": redact(exc)[-500:]})
                continue
            reason = redact(exc)
            if not reason.strip():
                reason = f"{type(exc).__name__} after {task.get('maxRuntimeMs', 120000)}ms"
            finish(result, start, success=False, stoppedAfterSuccess=False, failureReason=reason, rawBackendMetadata={**result["rawBackendMetadata"], "keyAttempts": attempts})
            break
        finally:
            if run_mode != "warm":
                await close_browser(browser)

    if result["endedAt"] is None:
        exhausted_message = "All Browser Use API keys exhausted or denied." if provider == "browser-use" else "All bake-off Gemini keys exhausted or denied."
        finish(
            result,
            start,
            success=False,
            stoppedAfterSuccess=False,
            keyPoolExhausted=True,
            failureReason=f"{exhausted_message} Attempts: {json.dumps(attempts)}",
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
    parser.add_argument("--provider", choices=["gemini", "browser-use"], default=os.environ.get("BROWSER_USE_PROVIDER", "gemini"))
    parser.add_argument("--mode", choices=["cold", "warm"], default=os.environ.get("BROWSER_USE_RUN_MODE", "cold"))
    parser.add_argument("--real-chrome", action="store_true", default=os.environ.get("BROWSER_USE_REAL_CHROME", "").lower() in {"1", "true", "yes"})
    args = parser.parse_args()

    tasks = load_tasks()
    if args.task:
        wanted = set(args.task)
        tasks = [task for task in tasks if task["id"] in wanted]

    results = []
    try:
        for task in tasks:
            print(f"[browser-use:{args.provider}:{args.mode}{':real-chrome' if args.real_chrome else ''}] {task['id']}", flush=True)
            result = await run_task(task, args.provider, args.mode, args.real_chrome)
            print(json.dumps({"taskId": result["taskId"], "success": result["success"], "runtimeMs": result["runtimeMs"], "finalUrl": result["finalUrl"]}, indent=2), flush=True)
            results.append(result)
    finally:
        global SHARED_BROWSER
        if SHARED_BROWSER is not None:
            await close_browser(SHARED_BROWSER)
            SHARED_BROWSER = None

    (RESULTS_DIR / f"browser-use-{args.provider}-{args.mode}-summary.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    return 0 if all(result["success"] for result in results) else 2


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
