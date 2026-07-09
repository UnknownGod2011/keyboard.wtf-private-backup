from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import time
from pathlib import Path
from typing import Any


def load_env_file(path: str | None) -> None:
    if not path:
        return
    env_path = Path(path)
    if not env_path.exists():
        return
    for raw_line in env_path.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        name, value = line.split("=", 1)
        name = name.strip()
        value = value.strip().strip('"').strip("'")
        if name and name not in os.environ:
            os.environ[name] = value


def redact(value: Any) -> str:
    text = str(value)
    text = re.sub(r"AQ\.Ab8[0-9A-Za-z._-]+", "[REDACTED_API_KEY]", text)
    text = re.sub(r"AIza[0-9A-Za-z._\-\u2014]+", "[REDACTED_API_KEY]", text)
    text = re.sub(r"bu_[0-9A-Za-z._-]+", "[REDACTED_API_KEY]", text)
    return text


def select_key() -> tuple[str, str] | None:
    candidates: list[tuple[str, str]] = []
    for name in ("GEMINI_BROWSER_AGENT_API_KEY", "GOOGLE_BROWSER_AGENT_API_KEY"):
        value = os.environ.get(name)
        if value:
            candidates.append((name, value))
    for name, value in sorted(os.environ.items()):
        if re.match(r"^BAKEOFF_GEMINI_API_KEY_\d+$", name, flags=re.I) and value:
            candidates.append((name, value))
    return candidates[0] if candidates else None


def profile_settings(profile: str) -> dict[str, Any]:
    if profile == "fast":
        return {
            "max_failures": 2,
            "max_actions_per_step": 4,
            "max_history_items": 8,
            "step_timeout": 30,
            "llm_timeout": 45,
            "use_vision": False,
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
        "flash_mode": False,
        "max_steps": 24,
    }


def start_url_for_site(site: str) -> str:
    site = (site or "").lower()
    if site == "youtube.com":
        return "https://www.youtube.com"
    if site == "amazon.in":
        return "https://www.amazon.in"
    if site == "amazon.com":
        return "https://www.amazon.com"
    if site == "flipkart.com":
        return "https://www.flipkart.com"
    if site == "github.com":
        return "https://github.com"
    if site == "wikipedia.org":
        return "https://www.wikipedia.org"
    if site == "imdb.com":
        return "https://www.imdb.com"
    if site == "google.com/maps":
        return "https://www.google.com/maps"
    if site == "google.com":
        return "https://www.google.com"
    return ""


def choose_final_url(current_url: str | None, history_urls: list[str]) -> str:
    if current_url and current_url != "about:blank":
        return current_url
    for url in reversed(history_urls or []):
        if url and url != "about:blank":
            return url
    return current_url or ""


def build_instruction(request: dict[str, Any]) -> str:
    criteria = request.get("SuccessCriteria") or request.get("successCriteria") or []
    memory = request.get("PatternContext") or request.get("patternContext") or ""
    lines = [
        "Complete this real browser task on the live website, then stop.",
        "",
        f"Task: {request.get('TaskText') or request.get('taskText') or ''}",
        "",
        "Operational rules:",
        "- Use the requested entity/product/site exactly. Do not substitute stale examples.",
        "- Prefer direct visible page controls and real page evidence.",
        "- Do not create TODO files, notes, local files, or code snippets.",
        "- Do not use unrelated devtools/evaluate tricks. Use visible controls and browser-use actions.",
        "- If a visible login/cookie/newsletter popup blocks a safe navigation/read task, close or dismiss it only when the control is clear and safe.",
        "- Product/media/result pages can have icon-only controls. Inspect nearby context and sticky bottom bars.",
        "- Do not loop. If the same action/search fails twice, report the exact blocker.",
        "- Stop immediately when the success criteria are met. Do not refresh, retry, or continue exploring after success.",
        "",
        "Safety:",
        f"- Allowed action level: {request.get('ActionLevel') or request.get('actionLevel')}.",
        "- Search, read/extract, open links/pages, inspect CTAs, dismiss non-destructive popups, and open videos are allowed.",
        "- For cart tasks, the user has given one upfront cart confirmation only when CartMutationPreconfirmed is true.",
        "- Never checkout, pay, place an order, change address/payment settings, enter passwords, solve OTP/CAPTCHA, send/delete/post, or submit protected final actions.",
    ]
    if memory:
        lines.extend(["", "Reusable pattern hints from previous successful tasks:", memory])
    if criteria:
        lines.append("")
        lines.append("Success criteria:")
        lines.extend(f"- {item}" for item in criteria)
    return "\n".join(lines)


async def verify_receipt(request: dict[str, Any], browser: Any) -> dict[str, Any] | None:
    task_text = f"{request.get('TaskText') or request.get('taskText') or ''} {request.get('TaskType') or request.get('taskType') or ''}"
    needs_product_receipt = re.search(r"cta|add to cart|buy now|notify|unavailable|price|product", task_text, flags=re.I)
    if not needs_product_receipt:
        return None
    script = r"""() => {
      const compact = (value, max = 140) => String(value || '').replace(/\s+/g, ' ').trim().slice(0, max);
      const visible = (el) => {
        const rect = el.getBoundingClientRect();
        const style = getComputedStyle(el);
        return rect.width > 1 && rect.height > 1 && style.visibility !== 'hidden' && style.display !== 'none' && rect.bottom > 0 && rect.right > 0 && rect.top < innerHeight && rect.left < innerWidth;
      };
      const textOf = (el) => [
        el.matches('button,a,input,[role="button"],[role="link"]') ? (el.innerText || el.textContent || '') : '',
        el.getAttribute('aria-label'),
        el.getAttribute('title'),
        el.getAttribute('placeholder'),
        el.getAttribute('value'),
        el.getAttribute('href'),
        el.getAttribute('class')
      ].filter(Boolean).map((part) => compact(part)).join(' ');
      const candidates = Array.from(document.querySelectorAll('button,a,input,[role="button"],[role="link"],[tabindex],svg,span,div'))
        .map((el) => {
          const target = el.tagName && el.tagName.toLowerCase() === 'svg'
            ? (el.closest('button,a,[role="button"],[tabindex],div,span') || el)
            : el;
          const rect = target.getBoundingClientRect();
          const label = textOf(target);
          const blob = `${label} ${target.getAttribute('class') || ''} ${target.getAttribute('href') || ''}`.toLowerCase();
          return {
            tag: (target.tagName || '').toLowerCase(),
            label: compact(label, 180),
            href: compact(target.getAttribute('href') || '', 160),
            className: compact(target.getAttribute('class') || '', 100),
            visible: visible(target),
            bounds: { x: Math.round(rect.x), y: Math.round(rect.y), w: Math.round(rect.width), h: Math.round(rect.height) },
            blob
          };
        })
        .filter((c, i, arr) => c.visible && c.bounds.w * c.bounds.h < innerWidth * innerHeight * 0.35 && arr.findIndex((o) => o.label === c.label && JSON.stringify(o.bounds) === JSON.stringify(c.bounds)) === i);
      const combined = candidates.map((c) => c.blob).join('\n');
      const bodyText = (document.body.innerText || '').replace(/\s+/g, ' ').slice(0, 20000);
      const buyNow = /buy\s*(now|at|with\s*emi)/i.test(combined) || /buy\s*(now|at|with\s*emi)/i.test(bodyText);
      const explicitCart = /add\s*(to)?\s*(cart|bag|basket)/i.test(combined) || /add\s*(to)?\s*(cart|bag|basket)/i.test(bodyText);
      const bottomIconCart = buyNow && candidates.some((c) => c.bounds.y > innerHeight - 140 && c.bounds.x < innerWidth * 0.75 && c.bounds.w >= 20 && c.bounds.w <= 100 && c.bounds.h >= 20 && c.bounds.h <= 100 && (c.blob.includes('cart') || c.blob.includes('bag') || c.label.length < 24));
      const notify = /notify\s*me|coming\s*soon|alert/i.test(combined) || /notify\s*me|coming\s*soon|alert/i.test(bodyText);
      const unavailable = /out\s*of\s*stock|unavailable|sold\s*out|currently\s*unavailable/i.test(bodyText);
      const price = (bodyText.match(/₹\s?[\d,]+(?:\.\d{1,2})?|\$\s?[\d,]+(?:\.\d{1,2})?/) || [''])[0];
      const title = compact(document.querySelector('#productTitle, h1, [data-automation-id="product-title"]')?.innerText || document.title, 220);
      return {
        url: location.href,
        title,
        price,
        cta: { buyNow, addToCart: explicitCart || bottomIconCart, explicitCart, iconCartNearBuyNow: bottomIconCart, notify, unavailable },
        sampleControls: candidates.filter((c) => /cart|bag|basket|buy|notify|stock|emi/i.test(c.blob) || c.bounds.y > innerHeight - 140).slice(0, 12).map(({blob, ...rest}) => rest)
      };
    }"""
    try:
        page = await browser.get_current_page()
        return await page.evaluate(script)
    except Exception as exc:
        return {"error": redact(exc)}


async def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--env")
    args = parser.parse_args()

    load_env_file(args.env or os.environ.get("KEYBOARD_WTF_BROWSER_AUTOMATION_ENV"))
    os.environ.setdefault("BROWSER_USE_VERBOSE_OBSERVABILITY", "false")
    os.environ.setdefault("TIMEOUT_NavigateToUrlEvent", "10")
    os.environ.setdefault("TIMEOUT_BrowserStateRequestEvent", "16")
    os.environ.setdefault("TIMEOUT_ScreenshotEvent", "8")

    request = json.loads(Path(args.request).read_text(encoding="utf-8-sig"))
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    start = time.time()

    key = select_key()
    model = request.get("Model") or request.get("model") or os.environ.get("BROWSER_USE_MODEL") or "gemini-3.1-flash-lite"
    profile = (request.get("Profile") or request.get("profile") or "smart").lower()
    settings = profile_settings(profile)

    if not key:
        output_path.write_text(json.dumps({
            "success": False,
            "message": "Browser automation Gemini key is not configured.",
            "failureReason": "Browser automation Gemini key is not configured.",
            "model": model,
            "profile": profile,
        }, indent=2), encoding="utf-8")
        return 2

    os.environ["GOOGLE_API_KEY"] = key[1]
    os.environ["GEMINI_API_KEY"] = key[1]
    os.environ["GOOGLE_GENERATIVE_AI_API_KEY"] = key[1]

    try:
        from browser_use import Agent, Browser, BrowserProfile, ChatGoogle, Tools

        appdata = Path(os.environ.get("LOCALAPPDATA") or Path.home())
        root = appdata / "keyboard.wtf" / "browser-automation"
        screenshots = root / "screenshots"
        profiles = root / "profiles"
        traces = root / "traces"
        screenshots.mkdir(parents=True, exist_ok=True)
        profiles.mkdir(parents=True, exist_ok=True)
        traces.mkdir(parents=True, exist_ok=True)

        use_persistent_session = bool(request.get("UsePersistentSession", request.get("usePersistentSession", True)))
        profile_dir = profiles / "browser-use-experimental"
        if not use_persistent_session:
            profile_dir = profiles / f"ephemeral-{int(time.time())}"

        browser_profile = BrowserProfile(
            headless=False,
            user_data_dir=str(profile_dir),
            traces_dir=str(traces),
            highlight_elements=True,
            viewport={"width": 1440, "height": 900},
            window_size={"width": 1440, "height": 1000},
            wait_between_actions=0.1,
            minimum_wait_page_load_time=0.25,
            wait_for_network_idle_page_load_time=0.5,
            keep_alive=True,
        )
        browser = Browser(browser_profile=browser_profile)
        tools = Tools(
            exclude_actions=["screenshot", "write_file", "replace_file", "read_file", "evaluate"],
            display_files_in_done_text=False,
        )
        start_url = start_url_for_site(request.get("TargetSite") or request.get("targetSite") or "")
        initial_actions = [{"navigate": {"url": start_url, "new_tab": False}}] if start_url else None
        agent = Agent(
            task=build_instruction(request),
            llm=ChatGoogle(model=model),
            browser=browser,
            tools=tools,
            initial_actions=initial_actions,
            directly_open_url=True,
            max_failures=settings["max_failures"],
            max_actions_per_step=settings["max_actions_per_step"],
            max_history_items=settings["max_history_items"],
            step_timeout=settings["step_timeout"],
            llm_timeout=settings["llm_timeout"],
            loop_detection_enabled=True,
            loop_detection_window=8,
            final_response_after_failure=False,
            use_vision=settings["use_vision"],
            vision_detail_level="low",
            use_judge=False,
            use_thinking=False,
            flash_mode=settings["flash_mode"],
            enable_planning=False,
            llm_screenshot_size=(1000, 700),
            display_files_in_done_text=False,
            save_conversation_path=None,
        )
        max_runtime = int(request.get("MaxRuntimeMs") or request.get("maxRuntimeMs") or 150000)
        history = await asyncio.wait_for(agent.run(max_steps=settings["max_steps"]), timeout=max_runtime / 1000)
        history_urls = []
        try:
            history_urls = history.urls()
        except Exception:
            history_urls = []
        current_url = None
        try:
            current_url = await browser.get_current_page_url()
        except Exception:
            current_url = ""
        final_url = choose_final_url(current_url, history_urls)

        receipt = await verify_receipt(request, browser)
        screenshot_path = ""
        try:
            screenshot_file = screenshots / f"{int(time.time())}-{profile}.png"
            try:
                await browser.take_screenshot(path=str(screenshot_file), full_page=False)
            except Exception:
                page = await browser.get_current_page()
                await page.screenshot(path=str(screenshot_file), full_page=False)
            if screenshot_file.exists():
                screenshot_path = str(screenshot_file)
        except Exception:
            pass
        try:
            await browser.close()
        except Exception:
            pass

        final_text = history.final_result() or ""
        success = bool(history.is_done() and history.is_successful())
        task_type = (request.get("TaskType") or request.get("taskType") or "").lower()
        if not success and task_type.startswith("youtube") and final_url and "youtube.com/watch" in final_url:
            success = True
        if not success and task_type == "ecommerce_add_to_cart" and final_url and "/cart" in final_url:
            success = True

        receipt_summary = ""
        receipt_disagrees = False
        if receipt:
            cta = receipt.get("cta") or {}
            if cta:
                receipt_summary = f"CTA receipt: buyNow={cta.get('buyNow')}, addToCart={cta.get('addToCart')}, notify={cta.get('notify')}, unavailable={cta.get('unavailable')}"
                needs_cta_report = re.search(r"cta|add to cart / buy now|notify me|unavailable", f"{request.get('TaskText')} {request.get('TaskType')}", flags=re.I)
                if needs_cta_report and cta.get("addToCart") and re.search(r"(no|not|missing|not visible).{0,40}(cart|add)", final_text, flags=re.I):
                    receipt_disagrees = True

        output_path.write_text(json.dumps({
            "success": success,
            "message": final_text[:2000] if final_text else ("Task completed." if success else "Browser automation did not complete."),
            "failureReason": "" if success else "browser-use did not report a successful completed task.",
            "finalUrl": final_url or "",
            "runtimeMs": int((time.time() - start) * 1000),
            "actionCount": history.number_of_steps(),
            "model": model,
            "profile": profile,
            "screenshotPath": screenshot_path,
            "receiptSummary": receipt_summary,
            "receiptDisagreesWithSummary": receipt_disagrees,
        }, indent=2), encoding="utf-8")
        return 0 if success else 2
    except Exception as exc:
        output_path.write_text(json.dumps({
            "success": False,
            "message": redact(exc),
            "failureReason": redact(exc),
            "model": model,
            "profile": profile,
            "runtimeMs": int((time.time() - start) * 1000),
        }, indent=2), encoding="utf-8")
        return 2


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
