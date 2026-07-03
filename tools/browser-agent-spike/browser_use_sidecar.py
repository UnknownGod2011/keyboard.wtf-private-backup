from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import re
import sys
import time
from contextlib import suppress
from pathlib import Path
from typing import Any

from browser_use import Agent, BrowserProfile, ChatGoogle


ROOT = Path(__file__).resolve().parent
LOG_BUFFER: list[str] = []


class BufferedLogHandler(logging.Handler):
    def emit(self, record: logging.LogRecord) -> None:
        message = self.format(record)
        LOG_BUFFER.append(message)
        if len(LOG_BUFFER) > 120:
            del LOG_BUFFER[: len(LOG_BUFFER) - 120]


def configure_logging() -> None:
    formatter = logging.Formatter("%(levelname)s %(name)s %(message)s")
    logging.basicConfig(
        stream=sys.stderr,
        level=logging.WARNING,
        format="%(levelname)s %(name)s %(message)s",
    )
    handler = BufferedLogHandler(level=logging.WARNING)
    handler.setFormatter(formatter)
    logging.getLogger().addHandler(handler)


def load_local_env() -> None:
    for filename in (".env", ".env.local"):
        path = ROOT / filename
        if not path.exists():
            continue
        for raw in path.read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            key = key.strip()
            value = value.strip().strip('"').strip("'")
            if key and value and key not in os.environ:
                os.environ[key] = value


def gemini_key() -> str:
    load_local_env()
    key = os.environ.get("GEMINI_API_KEY") or os.environ.get("GOOGLE_API_KEY") or os.environ.get("MODEL_API_KEY")
    if not key:
        raise RuntimeError("Browser-use backend needs Gemini API key configured.")
    return key


def gemini_backup_key() -> str:
    load_local_env()
    return os.environ.get("GEMINI_API_KEY_BACKUP") or gemini_key()


def allowed_domains(start_url: str) -> list[str] | None:
    match = re.match(r"^https?://([^/:]+)", start_url or "", flags=re.I)
    if not match:
        return None
    host = match.group(1).lower()
    parts = host.split(".")
    root = ".".join(parts[-2:]) if len(parts) >= 2 else host
    return [host, f"*.{root}", root]


def classify_error(error: BaseException) -> tuple[str, str]:
    text = str(error)
    lowered = text.lower()
    if (
        "resource_exhausted" in lowered
        or "quota" in lowered
        or "rate-limit" in lowered
        or "rate limits" in lowered
        or "503 unavailable" in lowered
        or "high demand" in lowered
    ):
        return "quota_limited", "Browser-use backend is temporarily quota-limited or capacity-limited by Gemini."
    if "timeout" in lowered or isinstance(error, asyncio.TimeoutError):
        return "timed_out", "Browser-use backend timed out."
    if any(term in lowered for term in ("captcha", "otp", "login", "blocked", "security", "access denied", "attention required")):
        return "blocked", "The website blocked automation or requires manual login/CAPTCHA/OTP."
    return "failed", "Browser-use backend failed before completing the task."


def classify_text(text: str) -> tuple[str, str] | None:
    lowered = text.lower()
    if (
        "resource_exhausted" in lowered
        or "quota" in lowered
        or "rate-limit" in lowered
        or "rate limits" in lowered
        or "503 unavailable" in lowered
        or "high demand" in lowered
    ):
        return "quota_limited", "Browser-use backend is temporarily quota-limited or capacity-limited by Gemini."
    if "timeout" in lowered:
        return "timed_out", "Browser-use backend timed out."
    if any(term in lowered for term in ("captcha", "otp", "login", "blocked", "security", "access denied", "attention required")):
        return "blocked", "The website blocked automation or requires manual login/CAPTCHA/OTP."
    return None


def final_result_failed(final: str | None) -> bool:
    lowered = (final or "").lower()
    failure_markers = (
        "could not be completed",
        "could not complete",
        "was unable",
        "unable to",
        "failed to",
        "not able to",
        "cannot",
        "can't",
        "did not",
    )
    return any(marker in lowered for marker in failure_markers)


def build_task(payload: dict[str, Any]) -> str:
    command = payload.get("original_user_command") or payload.get("task") or ""
    start_url = payload.get("start_url") or ""
    context = payload.get("context_summary") or ""
    risky_confirmed = bool(payload.get("risky_action_confirmed"))
    safety = (
        "A risky state-changing boundary was confirmed by the user. You may add requested items to cart if the target is clear, "
        "but you must still stop before checkout, payment, booking finalization, publishing, sending, deletion, or account changes."
        if risky_confirmed
        else "This task is not confirmed for risky state changes. Do not add to cart, submit, publish, send, delete, upload, change account settings, book, checkout, or pay."
    )
    parts = [
        f"The browser is already being opened at {start_url}. Start by reading the visible page." if start_url else "",
        command,
        f"Context explicitly provided by user: {context}" if context else "",
        safety,
        "If a site shows login, OTP, CAPTCHA, access denied, bot protection, or security challenge, stop and report that manual action is required.",
        "Return a concise human-readable summary. Do not expose internal logs, browser session IDs, cookies, or secrets.",
    ]
    return "\n".join(part for part in parts if part.strip())


async def run(payload: dict[str, Any]) -> dict[str, Any]:
    started = time.perf_counter()
    task_text = build_task(payload)
    steps = 0
    model = payload.get("model") or os.environ.get("BROWSER_AGENT_GEMINI_MODEL") or "gemini-2.5-flash"
    fallback_model = (
        payload.get("fallback_model")
        or os.environ.get("BROWSER_AGENT_GEMINI_FALLBACK_MODEL")
        or "gemini-2.5-flash-lite"
    )
    profile = BrowserProfile(
        headless=bool(payload.get("headless", False)),
        allowed_domains=allowed_domains(payload.get("start_url", "")),
        wait_between_actions=0.1,
        minimum_wait_page_load_time=0.2,
        wait_for_network_idle_page_load_time=0.5,
        highlight_elements=False,
        user_data_dir=str(ROOT / ".browser-use-sidecar-profile"),
    )

    async def on_step_end(agent: Agent) -> None:
        nonlocal steps
        steps += 1

    agent = Agent(
        task=task_text,
        llm=ChatGoogle(
            model=model,
            temperature=0,
            api_key=gemini_key(),
        ),
        browser_profile=profile,
        initial_actions=[
            {"navigate": {"url": payload.get("start_url", ""), "new_tab": False}},
        ] if payload.get("start_url") else None,
        directly_open_url=True,
        fallback_llm=ChatGoogle(
            model=fallback_model,
            temperature=0,
            api_key=gemini_backup_key(),
        ) if fallback_model else None,
        use_vision=bool(payload.get("use_vision", False)),
        max_actions_per_step=int(payload.get("max_actions_per_step", 4)),
        enable_planning=bool(payload.get("enable_planning", False)),
    )
    try:
        timeout_seconds = max(10, int(payload.get("timeout_seconds", 120)))
        max_steps = max(1, int(payload.get("max_steps", 14)))
        history = await asyncio.wait_for(
            agent.run(max_steps=max_steps, on_step_end=on_step_end),
            timeout=timeout_seconds,
        )
        final = history.final_result()
        ok = bool(final) and not final_result_failed(final)
        status = "completed" if ok else "failed"
        message = final or "Browser-use did not produce a final answer."
        if not ok:
            history_errors = ""
            with suppress(Exception):
                history_errors = "\n".join(str(error) for error in history.errors() if error)
            classified = classify_text(history_errors + "\n" + "\n".join(LOG_BUFFER))
            if classified:
                status, message = classified
        return {
            "ok": ok,
            "status": status,
            "message": message,
            "summary": final if ok else "",
            "backend": f"browser-use + Gemini ({model}; fallback {fallback_model})",
            "duration_ms": round((time.perf_counter() - started) * 1000),
            "steps": steps,
            "clarifications": 0,
            "confirmations": 0,
            "can_fallback_to_direct": status in ("quota_limited", "timed_out", "failed"),
            "progress": [
                {"title": "Started browser-use + Gemini", "status": "completed", "detail": "Smart browser backend launched."},
                {"title": "Ran browser task", "status": "completed" if ok else "failed", "detail": message},
            ],
        }
    except Exception as exc:  # noqa: BLE001 - sidecar converts framework errors to structured JSON
        classified = classify_text("\n".join(LOG_BUFFER))
        status, message = classified or classify_error(exc)
        return {
            "ok": False,
            "status": status,
            "message": message,
            "summary": "",
            "backend": f"browser-use + Gemini ({model}; fallback {fallback_model})",
            "duration_ms": round((time.perf_counter() - started) * 1000),
            "steps": steps,
            "clarifications": 0,
            "confirmations": 0,
            "can_fallback_to_direct": status in ("quota_limited", "timed_out", "failed"),
            "progress": [
                {"title": "Started browser-use + Gemini", "status": "completed", "detail": "Smart browser backend launched."},
                {"title": "Browser-use stopped", "status": "failed", "detail": message},
            ],
        }
    finally:
        with suppress(Exception):
            await agent.close()


async def main() -> None:
    configure_logging()
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    args = parser.parse_args()
    payload = json.loads(Path(args.input).read_text(encoding="utf-8-sig"))
    result = await run(payload)
    print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    asyncio.run(main())
