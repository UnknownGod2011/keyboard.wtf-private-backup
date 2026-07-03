from __future__ import annotations

import asyncio
import json
import os
import re
import time
from contextlib import suppress
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Thread
from typing import Any

from browser_use import Agent, BrowserProfile, ChatGoogle


ROOT = Path(__file__).resolve().parent
OUTPUT = ROOT.parent.parent / "output" / "browser-agent-spike"
OUTPUT.mkdir(parents=True, exist_ok=True)


def load_local_env() -> None:
    for filename in (".env", ".env.local"):
        path = ROOT / filename
        if not path.exists():
            continue
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
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
        raise RuntimeError(
            "Gemini API key not found. Set GEMINI_API_KEY or GOOGLE_API_KEY in tools/browser-agent-spike/.env.local."
        )
    return key


MOCK_HTML: dict[str, str] = {
    "/devpost": """
<!doctype html><title>Mock Devpost</title>
<main>
  <article class="hackathon-card"><h2>Neighborhood Apps Sprint</h2><p>Prize pool: $12,000</p><p>Online</p></article>
  <article class="hackathon-card"><h2>Global AI Challenge</h2><p>Total prizes: $100,000</p><p>Online</p></article>
  <article class="hackathon-card"><h2>Climate Tools Jam</h2><p>USD 45,000 in awards</p><p>Hybrid</p></article>
</main>
""",
    "/devpost-changed": """
<!doctype html><title>Changed Mock Devpost</title>
<section data-feed="hackathons">
  <div class="tile"><strong>Neighborhood Apps Sprint</strong><span>Awards available: 12k USD</span></div>
  <div class="tile"><strong>Global AI Challenge</strong><span>Awards available: USD 100k</span></div>
  <div class="tile"><strong>Climate Tools Jam</strong><span>Awards available: $45,000</span></div>
</section>
""",
    "/store": """
<!doctype html><title>Mock Store</title>
<main>
  <article class="product"><h2>Pro Keyboard</h2><p>Price: $149</p><p>Rating: 4.8 stars</p></article>
  <article class="product"><h2>Budget Keyboard</h2><p>Price: $79</p><p>Rating: 4.1 stars</p></article>
  <article class="product"><h2>Studio Keyboard</h2><p>Price: $119</p><p>Rating: 4.9 stars</p></article>
</main>
""",
    "/form": """
<!doctype html><title>Mock Project Form</title>
<form>
  <label>Project name <input name="projectName"></label>
  <label>Summary <textarea name="summary"></textarea></label>
  <button type="button" id="preview">Preview</button>
  <button type="submit" id="submit">Submit</button>
</form>
<div id="previewResult" aria-live="polite"></div>
<script>
document.querySelector('#preview').addEventListener('click', () => {
  const fd = new FormData(document.querySelector('form'));
  document.querySelector('#previewResult').textContent = 'Preview ready: ' + fd.get('projectName');
});
document.querySelector('form').addEventListener('submit', e => {
  e.preventDefault();
  document.body.dataset.submitted = 'true';
});
</script>
""",
    "/portfolio": """
<!doctype html><title>Mock Portfolio</title>
<main>
  <h1>My Devpost portfolio</h1>
  <article class="project"><h2>LaunchPilot</h2><p>AI launch planning workspace.</p></article>
  <article class="project"><h2>Cursivis</h2><p>Voice-first writing companion.</p></article>
</main>
""",
}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        body = MOCK_HTML.get(self.path.split("?", 1)[0], MOCK_HTML["/devpost"])
        encoded = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def log_message(self, fmt: str, *args: Any) -> None:
        return


@dataclass
class MockServer:
    server: ThreadingHTTPServer
    thread: Thread

    @property
    def base_url(self) -> str:
        host, port = self.server.server_address
        return f"http://127.0.0.1:{port}"

    def stop(self) -> None:
        self.server.shutdown()
        self.thread.join(timeout=5)


def start_server() -> MockServer:
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return MockServer(server, thread)


def scrub(value: Any) -> Any:
    if isinstance(value, str):
        return re.sub(r"http://127\.0\.0\.1:\d+", "http://127.0.0.1:<port>", value)
    if isinstance(value, dict):
        return {k: scrub(v) for k, v in value.items()}
    if isinstance(value, list):
        return [scrub(v) for v in value]
    return value


async def run_task(name: str, task: str, allowed_domains: list[str], max_steps: int = 10) -> dict[str, Any]:
    started = time.perf_counter()
    steps = 0
    profile = BrowserProfile(
        headless=True,
        allowed_domains=allowed_domains,
        wait_between_actions=0.05,
        minimum_wait_page_load_time=0.1,
        wait_for_network_idle_page_load_time=0.25,
        highlight_elements=False,
        user_data_dir=str(ROOT / ".browser-use-profile"),
    )

    async def on_step_end(agent: Agent) -> None:
        nonlocal steps
        steps += 1

    result: dict[str, Any] = {
        "backend": "browser-use",
        "task": name,
        "status": "failed",
        "durationMs": None,
        "steps": None,
        "confirmations": 0,
        "clarifications": 0,
        "result": None,
        "error": None,
    }

    agent = Agent(
        task=task,
        llm=ChatGoogle(
            model=os.environ.get("BROWSER_AGENT_GEMINI_MODEL", "gemini-2.5-flash"),
            temperature=0,
            api_key=gemini_key(),
        ),
        browser_profile=profile,
        use_vision=False,
        max_actions_per_step=4,
        enable_planning=False,
    )

    try:
        history = await asyncio.wait_for(agent.run(max_steps=max_steps, on_step_end=on_step_end), timeout=180)
        final = history.final_result()
        result.update(
            {
                "status": "passed" if final else "failed",
                "result": final,
                "steps": steps,
            }
        )
    except Exception as exc:  # noqa: BLE001 - spike records framework errors
        result["error"] = f"{type(exc).__name__}: {exc}"
        result["steps"] = steps
    finally:
        result["durationMs"] = round((time.perf_counter() - started) * 1000)
        with suppress(Exception):
            await agent.close()

    return scrub(result)


async def main() -> None:
    requested_task = None
    if "--task" in os.sys.argv:
        idx = os.sys.argv.index("--task")
        if idx + 1 < len(os.sys.argv):
            requested_task = os.sys.argv[idx + 1]
    real_only = "--real-only" in os.sys.argv
    server = start_server()
    try:
        base = server.base_url
        tasks = [] if real_only else [
            (
                "mock-devpost-highest-prize",
                f"Open {base}/devpost. Read the visible hackathon cards. Return only the hackathon with the highest prize pool and the prize amount. Do not ask for confirmation because this is read-only.",
                ["127.0.0.1"],
                8,
            ),
            (
                "mock-devpost-layout-recovery",
                f"Open {base}/devpost-changed. The layout is changed. Read the visible hackathon tiles and return only the highest prize pool and amount. Do not ask for confirmation.",
                ["127.0.0.1"],
                8,
            ),
            (
                "mock-ecommerce-cheapest-highrated",
                f"Open {base}/store. Compare visible products. Return the cheapest product with rating at least 4 stars and include price and rating. Do not add to cart.",
                ["127.0.0.1"],
                8,
            ),
            (
                "mock-form-fill-preview",
                f"Open {base}/form. Fill Project name with keyboard.wtf and Summary with Local-first Windows Jarvis assistant. Click only Preview. Stop before Submit. Return whether a preview is visible.",
                ["127.0.0.1"],
                12,
            ),
            (
                "mock-missing-project-clarification",
                f"Open {base}/portfolio. Find project keyboard.wtf in the portfolio. If it is missing, do not invent or create a project; say that clarification is needed and list the visible alternatives.",
                ["127.0.0.1"],
                8,
            ),
        ]

        if "--real" in os.sys.argv or real_only:
            tasks.extend(
                [
                    (
                        "real-devpost-highest-prize",
                        "Open https://devpost.com/hackathons. Read visible hackathon listings. Return the hackathon with the highest visible prize pool and amount. This is read-only; do not log in or submit anything.",
                        ["devpost.com", "*.devpost.com"],
                        10,
                    ),
                    (
                        "real-bookmyshow-toy-story-mumbai",
                        "Open https://in.bookmyshow.com/explore/movies-mumbai. Search or inspect read-only listings for Toy Story shows on Sunday June 28 2026 in Mumbai. Return found options or say none visible. Do not book tickets or select seats.",
                        ["bookmyshow.com", "*.bookmyshow.com", "in.bookmyshow.com"],
                        12,
                    ),
                    (
                        "real-ecommerce-search-compare",
                        "Open https://www.amazon.in and search for Logitech MX Master mouse. Compare visible product options by title, price, and rating if visible. Do not add anything to cart; say that add-to-cart requires confirmation.",
                        ["amazon.in", "*.amazon.in"],
                        14,
                    ),
                    (
                        "real-form-fill-preview",
                        "Open https://www.selenium.dev/selenium/web/web-form.html. Fill the Text input with keyboard.wtf and the Textarea with Local-first Windows Jarvis assistant. Do not submit. Return whether the fields were filled.",
                        ["selenium.dev", "*.selenium.dev"],
                        12,
                    ),
                    (
                        "real-travel-search",
                        "Open https://www.google.com/travel/flights. Search read-only flight options from Mumbai to Delhi for Friday June 26 2026. Return cheapest reasonable visible options if available. Do not book.",
                        ["google.com", "*.google.com"],
                        14,
                    ),
                    (
                        "real-hotel-research",
                        "Open https://www.google.com/travel/hotels/Mumbai. Search for budget Mumbai hotels for June 27 to June 28 2026. Return the best-rated visible options under a reasonable price range if available. Do not book.",
                        ["google.com", "*.google.com"],
                        14,
                    ),
                ]
            )

        if requested_task:
            tasks = [task for task in tasks if task[0] == requested_task]
            if not tasks:
                raise RuntimeError(f"Task not found: {requested_task}")

        results = []
        for name, task, allowed, max_steps in tasks:
            results.append(await run_task(name, task, allowed, max_steps))

        out = OUTPUT / (
            f"browser-use-{requested_task}.json"
            if requested_task
            else ("browser-use-real.json" if "--real" in os.sys.argv or real_only else "browser-use-mock.json")
        )
        out.write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(json.dumps(results, indent=2))
    finally:
        server.stop()


if __name__ == "__main__":
    asyncio.run(main())
