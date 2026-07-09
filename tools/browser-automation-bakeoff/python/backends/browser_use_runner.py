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


def install_fast_timeouts() -> None:
    os.environ.setdefault("BROWSER_USE_SETUP_LOGGING", "true")
    os.environ.setdefault("BROWSER_USE_VERBOSE_OBSERVABILITY", "false")
    os.environ.setdefault("TIMEOUT_NavigateToUrlEvent", "10")
    os.environ.setdefault("TIMEOUT_ClickElementEvent", "8")
    os.environ.setdefault("TIMEOUT_ClickCoordinateEvent", "8")
    os.environ.setdefault("TIMEOUT_TypeTextEvent", "20")
    os.environ.setdefault("TIMEOUT_ScrollEvent", "5")
    os.environ.setdefault("TIMEOUT_ScreenshotEvent", "6")
    os.environ.setdefault("TIMEOUT_BrowserStateRequestEvent", "16")
    os.environ.setdefault("TIMEOUT_BrowserStartEvent", "20")
    os.environ.setdefault("TIMEOUT_BrowserLaunchEvent", "20")
    os.environ.setdefault("TIMEOUT_SwitchTabEvent", "6")


def classify_model_failure(value: Any) -> str | None:
    text = str(value)
    if re.search(r"RESOURCE_EXHAUSTED|429|quota|rate.?limit|capacity|overloaded|credits?|free.?tier|503|UNAVAILABLE", text, flags=re.I):
        return "quota_or_capacity"
    if re.search(r"PERMISSION_DENIED|API key not valid|invalid api key|unauthorized|authentication|401|403|ByteString|greater than 255|API_KEY_INVALID", text, flags=re.I):
        return "invalid_or_denied"
    return None


def vision_mode_for_task(task: dict[str, Any]) -> bool | str:
    mode = str(task.get("visionMode", "false")).strip().lower()
    if mode == "auto":
        return "auto"
    return mode in {"1", "true", "yes", "on"}


def browser_use_profile_for_task(task: dict[str, Any]) -> str:
    """Select a browser-use runtime profile.

    The previous runner forced browser-use flash mode for every task. That made
    Wikipedia-style extraction fast, but it made product/media pages too shallow:
    icon-only CTAs, popups, tabs, and channel/video flows need the normal agent
    evaluation loop. Keep the fast profile only for simple read/search tasks.
    """

    explicit = str(task.get("browserUseProfile", "")).strip().lower()
    if explicit == "standard":
        return "smart"
    if explicit in {"fast", "smart", "recovery"}:
        return explicit

    task_text = str(task.get("taskText", "")).lower()
    task_id = str(task.get("id", "")).lower()
    action_level = str(task.get("allowedActionLevel", "")).lower()

    complex_markers = [
        "add to cart",
        "cart",
        "buy now",
        "notify me",
        "unavailable",
        "oldest",
        "most recent",
        "official channel",
        "inside that page",
        "close the login popup",
        "popup",
        "google maps",
        "rating",
    ]
    if action_level.startswith("cart") or task.get("receiptVerification") or task.get("visionMode") == "auto":
        return "smart"
    if any(marker in task_text or marker in task_id for marker in complex_markers):
        return "smart"
    return "fast"


def browser_use_profiles_to_try(task: dict[str, Any]) -> list[str]:
    primary = browser_use_profile_for_task(task)
    if primary == "fast":
        return [primary]
    if primary == "recovery":
        return [primary]
    return [primary, "recovery"]


def browser_use_profile_settings(profile: str) -> dict[str, Any]:
    if profile == "fast":
        return {
            "max_failures": 2,
            "max_actions_per_step": 4,
            "max_history_items": 8,
            "step_timeout": 30,
            "llm_timeout": 45,
            "use_vision": False,
            "use_thinking": False,
            "flash_mode": True,
            "max_steps": 16,
        }
    if profile == "recovery":
        return {
            "max_failures": 2,
            "max_actions_per_step": 2,
            "max_history_items": 16,
            "step_timeout": 50,
            "llm_timeout": 70,
            "use_vision": True,
            "use_thinking": False,
            "flash_mode": False,
            "max_steps": 10,
        }
    return {
        "max_failures": 3,
        "max_actions_per_step": 3,
        "max_history_items": 12,
        "step_timeout": 45,
        "llm_timeout": 60,
        "use_vision": "auto",
        "use_thinking": False,
        "flash_mode": False,
        "max_steps": 24,
    }


async def verify_receipt(task: dict[str, Any], browser: Any) -> dict[str, Any] | None:
    if task.get("receiptVerification") != "product_cta":
        return None

    script = r"""() => {
      const visible = (el) => {
        const rect = el.getBoundingClientRect();
        const style = getComputedStyle(el);
        return rect.width > 1 && rect.height > 1 && style.visibility !== 'hidden' && style.display !== 'none' && rect.bottom > 0 && rect.right > 0 && rect.top < innerHeight && rect.left < innerWidth;
      };
      const tagName = (el) => (el.tagName || '').toLowerCase();
      const compact = (value, max = 180) => String(value || '').replace(/\s+/g, ' ').trim().slice(0, max);
      const textOf = (el) => {
        const tag = tagName(el);
        const isDirectControl = el.matches('button,a,input,select,textarea,[role="button"],[role="link"],[role="menuitem"],[role="tab"],[role="option"]');
        const text = isDirectControl ? (el.innerText || el.textContent || '') : '';
        return [
          text,
          el.getAttribute('aria-label'),
          el.getAttribute('title'),
          el.getAttribute('placeholder'),
          el.getAttribute('value'),
          tag === 'a' ? el.getAttribute('href') : '',
          el.getAttribute('class')
        ].filter(Boolean).map((part) => compact(part)).join(' ').replace(/\s+/g, ' ').trim();
      };
      const allElements = Array.from(document.querySelectorAll('*'));
      const controls = allElements
        .filter((el) => {
          const style = getComputedStyle(el);
          const rect = el.getBoundingClientRect();
          const tag = tagName(el);
          if (['html', 'body', 'main', 'section', 'article', 'header', 'footer', 'script', 'style', 'link', 'meta'].includes(tag)) return false;
          const isDirectControl = el.matches('button,a,input,select,textarea,[role="button"],[role="link"],[role="menuitem"],[role="tab"],[role="option"],svg');
          const tooLargeContainer = !isDirectControl && rect.width * rect.height > innerWidth * innerHeight * 0.25;
          if (tooLargeContainer) return false;
          const blob = `${textOf(el)} ${el.getAttribute('class') || ''} ${el.getAttribute('href') || ''}`.toLowerCase();
          return el.matches('button,a,[role="button"],[role="link"],[role="menuitem"],[role="tab"],[role="option"],input[type="button"],input[type="submit"],[tabindex],svg')
            || typeof el.onclick === 'function'
            || style.cursor === 'pointer'
            || el.querySelector('svg')
            || /cart|basket|bag|buy|notify|stock|emi/.test(blob)
            || (rect.bottom > innerHeight - 120 && rect.width >= 20 && rect.height >= 20);
        })
        .map((el) => {
          let target = el;
          if (el.tagName && el.tagName.toLowerCase() === 'svg') {
            target = el.closest('button,a,[role="button"],[tabindex],div,span') || el;
          }
          const rect = target.getBoundingClientRect();
          const label = textOf(target);
          return {
            tag: target.tagName ? target.tagName.toLowerCase() : '',
            role: target.getAttribute('role') || '',
            label: compact(label, 220),
            className: compact(target.getAttribute('class') || '', 120),
            href: target.getAttribute('href') || '',
            visible: visible(target),
            bounds: { x: Math.round(rect.x), y: Math.round(rect.y), w: Math.round(rect.width), h: Math.round(rect.height) }
          };
        })
        .filter((control, index, array) => control.visible && array.findIndex((other) => JSON.stringify(other.bounds) === JSON.stringify(control.bounds) && other.label === control.label) === index);

      const combined = controls.map((control) => `${control.label} ${control.className} ${control.href}`).join('\n');
      const bodyText = (document.body.innerText || '').replace(/\s+/g, ' ').slice(0, 20000);
      const buyNow = /buy\s*(now|at|with\s*emi)|buy\s*with\s*emi/i.test(combined) || /buy\s*(now|at|with\s*emi)|buy\s*with\s*emi/i.test(bodyText);
      const notify = /notify\s*me|coming\s*soon|alert/i.test(combined) || /notify\s*me|coming\s*soon|alert/i.test(bodyText);
      const unavailable = /out\s*of\s*stock|unavailable|sold\s*out|currently\s*unavailable/i.test(bodyText);
      const explicitCart = /add\s*(to)?\s*cart|add\s*(to)?\s*bag|add\s*(to)?\s*basket/i.test(combined);
      const buyNowControls = controls.filter((control) => /buy\s*(now|at|with\s*emi)|buy\s*with\s*emi/i.test(`${control.label} ${control.className}`));
      const bottomPurchaseControls = controls.filter((control) => control.bounds.y > innerHeight - 130 && control.bounds.w >= 20 && control.bounds.h >= 20);
      const iconCartNearBuyNow = controls.some((control) => {
        const blob = `${control.label} ${control.className} ${control.href}`.toLowerCase();
        const looksCart = blob.includes('cart') || blob.includes('basket') || blob.includes('bag') || (control.label.trim().length < 24 && control.bounds.w >= 20 && control.bounds.w <= 95 && control.bounds.h >= 20 && control.bounds.h <= 95);
        const nearPurchaseRow = buyNowControls.some((buy) => Math.abs((control.bounds.y + control.bounds.h / 2) - (buy.bounds.y + buy.bounds.h / 2)) < 140 && control.bounds.x < buy.bounds.x)
          || (buyNow && control.bounds.y > innerHeight - 130 && control.bounds.x < innerWidth * 0.7);
        return looksCart && nearPurchaseRow;
      });
      const bottomIconCart = buyNow && bottomPurchaseControls.some((control) => {
        const blob = `${control.label} ${control.className} ${control.href}`.toLowerCase();
        return control.bounds.x < innerWidth * 0.7
          && control.bounds.w >= 20
          && control.bounds.w <= 95
          && control.bounds.h >= 20
          && control.bounds.h <= 95
          && (blob.includes('cart') || blob.includes('basket') || blob.includes('bag') || control.label.trim().length < 24);
      });
      return {
        url: location.href,
        title: document.title,
        cta: {
          buyNow,
          addToCart: explicitCart || iconCartNearBuyNow || bottomIconCart,
          explicitCart,
          iconCartNearBuyNow: iconCartNearBuyNow || bottomIconCart,
          notify,
          unavailable
        },
        sampleControls: controls
          .filter((control) => /cart|bag|basket|buy|notify|stock|emi/i.test(`${control.label} ${control.className} ${control.href}`) || buyNowControls.some((buy) => Math.abs(control.bounds.y - buy.bounds.y) < 100) || (control.bounds.y > innerHeight - 130 && control.bounds.w <= 260))
          .slice(0, 20)
      };
    }"""

    try:
        page = await browser.get_current_page()
        raw = await page.evaluate(script)
        return json.loads(raw) if raw else None
    except Exception as exc:
        return {"error": redact(exc)}


def load_tasks() -> list[dict[str, Any]]:
    return json.loads(TASKS_PATH.read_text(encoding="utf-8"))


def make_result(task: dict[str, Any], provider: str, run_mode: str, model: str) -> dict[str, Any]:
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
            "usesBrowserUseAgentApi": True,
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
            "- Do not create TODO files, notes, plans, local files, or code snippets. Execute the browser task directly.",
            "- Do not use browser evaluate/devtools code to simulate unrelated tools. Use visible browser controls unless a built-in browser-use action is clearly appropriate.",
            "- If a visible login/cookie/newsletter popup blocks the requested read/navigation task, close or dismiss it only when the close/dismiss control is clear and safe.",
            "- For product/media/result pages, inspect icon-only controls too. A cart-shaped icon near product purchase CTAs counts as an Add to Cart CTA even if no visible text says Add to Cart.",
            "- Do not loop. If the same action/search fails twice, report the blocker.",
            "- Stop immediately when the success criteria are met.",
            "- If a final URL/page/player/product/result satisfies the success criteria, answer done and do not refresh, retry, or continue exploring.",
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
            headless=False,
            highlight_elements=True,
            viewport={"width": 1440, "height": 900},
            window_size={"width": 1440, "height": 1000},
        )
    else:
        browser_profile = BrowserProfile(
            headless=False,
            user_data_dir=str(profile_dir),
            traces_dir=str(trace_dir),
            highlight_elements=True,
            viewport={"width": 1440, "height": 900},
            window_size={"width": 1440, "height": 1000},
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
    model = os.environ.get("BROWSER_USE_MODEL", "gemini-2.5-flash") if provider == "gemini" else os.environ.get("BROWSER_USE_AGENT_MODEL", "bu-2-0")
    model_id = safe_name(model.replace("models/", ""))
    runtime_profile = browser_use_profile_for_task(task)
    result = make_result(task, provider, run_mode, model)
    result_path = RESULTS_DIR / f"browser-use-{provider}-{run_mode}-{model_id}-{safe_name(task['id'])}.json"
    try:
        from browser_use import Agent, Browser, BrowserProfile, ChatBrowserUse, ChatGoogle, Tools
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
        trace_dir = TRACES_DIR / f"browser-use-{provider}-{run_mode}-{model_id}-{safe_name(task['id'])}"
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
        log_path = LOGS_DIR / f"browser-use-{provider}-{run_mode}-{model_id}-{safe_name(task['id'])}-{key['name'].lower()}.json"
        result["logsPath"] = str(log_path)

        initial_actions = None
        if task.get("startUrl"):
            initial_actions = [{"navigate": {"url": task["startUrl"], "new_tab": False}}]

        tools = Tools(
            exclude_actions=["screenshot", "write_file", "replace_file", "read_file", "evaluate"],
            display_files_in_done_text=False,
        )

        profile_settings = browser_use_profile_settings(runtime_profile)
        profile_vision = profile_settings["use_vision"]
        if runtime_profile == "smart" and task.get("visionMode"):
            profile_vision = vision_mode_for_task(task)
        agent = Agent(
            task=instruction,
            llm=llm,
            browser=browser,
            tools=tools,
            initial_actions=initial_actions,
            directly_open_url=True,
            max_failures=profile_settings["max_failures"],
            max_actions_per_step=profile_settings["max_actions_per_step"],
            max_history_items=profile_settings["max_history_items"],
            step_timeout=profile_settings["step_timeout"],
            llm_timeout=profile_settings["llm_timeout"],
            loop_detection_enabled=True,
            loop_detection_window=8,
            final_response_after_failure=False,
            use_vision=profile_vision,
            vision_detail_level="low",
            use_judge=False,
            use_thinking=profile_settings["use_thinking"],
            flash_mode=profile_settings["flash_mode"],
            enable_planning=False,
            planning_replan_on_stall=1,
            llm_screenshot_size=(1000, 700),
            display_files_in_done_text=False,
            save_conversation_path=str(log_path) if os.environ.get("BROWSER_USE_SAVE_CONVERSATION", "").lower() in {"1", "true", "yes"} else None,
        )

        try:
            history = await asyncio.wait_for(agent.run(max_steps=profile_settings["max_steps"]), timeout=task.get("maxRuntimeMs", 120000) / 1000)
            errors = history.errors()
            error_text = "\n".join(str(error) for error in errors)
            classification = classify_model_failure(error_text)
            if classification and not history.is_done():
                attempts.append({"key": key["maskedKeyId"], "classification": classification, "message": redact(error_text)[-500:]})
                continue

            final_result = history.final_result()
            urls = history.urls()
            final_url = urls[-1] if urls else None
            try:
                final_url = await browser.get_current_page_url() or final_url
            except Exception:
                pass
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
                    final_screenshot = SCREENSHOTS_DIR / f"browser-use-{provider}-{run_mode}-{model_id}-{safe_name(task['id'])}-final.png"
                    final_screenshot.write_bytes(base64.b64decode(screenshots[-1].split(",", 1)[-1]))
                    screenshot_paths.append(str(final_screenshot.resolve()))
            except Exception:
                pass
            try:
                final_screenshot = SCREENSHOTS_DIR / f"browser-use-{provider}-{run_mode}-{model_id}-{safe_name(task['id'])}-final-direct.png"
                await browser.take_screenshot(path=str(final_screenshot), full_page=False)
                if final_screenshot.exists():
                    screenshot_paths.append(str(final_screenshot.resolve()))
            except Exception:
                pass

            receipt = await verify_receipt(task, browser)
            adjusted_summary = (final_result or str(history))[:4000]
            receipt_disagrees = False
            if receipt and receipt.get("cta"):
                cta = receipt["cta"]
                task_needs_cta_report = re.search(r"cta|add\s*to\s*cart\s*/\s*buy\s*now|notify\s*me|unavailable", f"{task.get('id', '')} {task.get('taskText', '')}", flags=re.I) is not None
                summary_claims_no_cart = re.search(r"(no|not|without|absent|missing|did not observe|not visible|no explicit).{0,40}(add\s*(to)?\s*cart|cart)", adjusted_summary, flags=re.I) is not None
                summary_mentions_cart = re.search(r"add\s*(to)?\s*cart|cart", adjusted_summary, flags=re.I) is not None
                if task_needs_cta_report and cta.get("addToCart") and (summary_claims_no_cart or not summary_mentions_cart):
                    receipt_disagrees = True
                adjusted_summary = (
                    f"{adjusted_summary}\n\n"
                    f"Receipt verifier: buyNow={cta.get('buyNow')}, addToCart={cta.get('addToCart')}, "
                    f"notify={cta.get('notify')}, unavailable={cta.get('unavailable')}."
                )[:4000]

            finish(
                result,
                start,
                success=success,
                finalUrl=final_url,
                screenshotPaths=screenshot_paths,
                stoppedAfterSuccess=bool(history.is_done()),
                looped=(not history.is_done()),
                actionCount=history.number_of_steps(),
                summary=adjusted_summary,
                rawBackendMetadata={
                    **result["rawBackendMetadata"],
                    "runnerProfile": runtime_profile,
                    "usesBrowserUseAgentApi": True,
                    "errors": [redact(error) for error in errors[-5:]],
                    "keyAttempts": attempts,
                    "isDone": history.is_done(),
                    "isSuccessful": history.is_successful(),
                    "isValidated": history.is_validated(),
                    "receiptVerification": receipt,
                    "receiptDisagreesWithSummary": receipt_disagrees,
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
    install_fast_timeouts()

    parser = argparse.ArgumentParser()
    parser.add_argument("--task", action="append", default=[])
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--provider", choices=["gemini", "browser-use"], default=os.environ.get("BROWSER_USE_PROVIDER", "gemini"))
    parser.add_argument("--mode", choices=["cold", "warm"], default=os.environ.get("BROWSER_USE_RUN_MODE", "cold"))
    parser.add_argument("--model", default=os.environ.get("BROWSER_USE_MODEL"))
    parser.add_argument("--profile", choices=["fast", "smart", "recovery"], default=None)
    parser.add_argument("--real-chrome", action="store_true", default=os.environ.get("BROWSER_USE_REAL_CHROME", "").lower() in {"1", "true", "yes"})
    args = parser.parse_args()
    if args.model:
        os.environ["BROWSER_USE_MODEL"] = args.model

    tasks = load_tasks()
    if args.task:
        wanted = set(args.task)
        tasks = [task for task in tasks if task["id"] in wanted]

    results = []
    try:
        for task in tasks:
            if args.profile:
                task = {**task, "browserUseProfile": args.profile}
            print(f"[browser-use:{args.provider}:{args.mode}{':real-chrome' if args.real_chrome else ''}] {task['id']}", flush=True)
            result = await run_task(task, args.provider, args.mode, args.real_chrome)
            print(json.dumps({"taskId": result["taskId"], "success": result["success"], "runtimeMs": result["runtimeMs"], "finalUrl": result["finalUrl"]}, indent=2), flush=True)
            results.append(result)
    finally:
        global SHARED_BROWSER
        if SHARED_BROWSER is not None:
            await close_browser(SHARED_BROWSER)
            SHARED_BROWSER = None

    summary_model = safe_name((os.environ.get("BROWSER_USE_MODEL") if args.provider == "gemini" else os.environ.get("BROWSER_USE_AGENT_MODEL", "bu-2-0")) or "default")
    (RESULTS_DIR / f"browser-use-{args.provider}-{args.mode}-{summary_model}-summary.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    return 0 if all(result["success"] for result in results) else 2


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
