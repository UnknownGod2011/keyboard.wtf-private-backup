const { chromium } = require("playwright");
const http = require("node:http");
const path = require("node:path");
const fs = require("node:fs");

const root = path.resolve(__dirname, "..", "..");
const extensionPath = path.join(root, "extensions", "browser-companion");
const profile = path.join(root, "output", "browser-companion-playwright-profile");
fs.rmSync(profile, { recursive: true, force: true });
fs.mkdirSync(profile, { recursive: true });

const channelHtml = `<!doctype html><html><head><title>Mock YouTube Channel</title></head><body>
<nav><button>Home</button><button id="videos">Videos</button><button>Shorts</button><button>Posts</button></nav>
<section aria-label="Sort videos"><button>Latest</button><button>Popular</button><button id="oldest" onclick="document.body.dataset.sorted='oldest'; document.getElementById('grid').setAttribute('aria-label','Sorted oldest')">Oldest</button></section>
<form role="search"><input type="search" placeholder="Search channel"><button type="button">Search</button></form>
<main id="grid" aria-label="Video grid">
  <article class="video-card"><a href="/watch?v=oldest001">I Made A Video In 2008 - 1M views</a><button aria-label="Play I Made A Video In 2008">Play</button></article>
  <article class="video-card"><a href="/watch?v=older002">Another Early Video - 2M views</a></article>
</main>
<a href="/playlists" id="playlists">Playlists</a>
</body></html>`;
const watchHtml = '<!doctype html><html><head><title>Watch oldest</title></head><body><video controls autoplay></video><button aria-label="Play">Play</button><h1>I Made A Video In 2008</h1></body></html>';
const playlistsHtml = '<!doctype html><html><head><title>Playlists</title></head><body><button>Videos</button><button>Oldest</button><button>Create playlist</button></body></html>';

function prop(object, camel, pascal) {
  return object?.[camel] ?? object?.[pascal];
}

function controls(graph) {
  return graph?.controls || graph?.Controls || [];
}

function text(control) {
  return [
    prop(control, "text", "Text"),
    prop(control, "ariaLabel", "AriaLabel"),
    prop(control, "title", "Title"),
    prop(control, "placeholder", "Placeholder"),
    prop(control, "nearbyText", "NearbyText"),
  ].filter(Boolean).join(" ");
}

async function waitFor(predicate, label, ms = 15000) {
  const start = Date.now();
  while (Date.now() - start < ms) {
    const value = await predicate();
    if (value) return value;
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`Timed out waiting for ${label}`);
}

async function main() {
  const server = http.createServer((req, res) => {
    res.setHeader("content-type", "text/html; charset=utf-8");
    if (req.url?.startsWith("/watch")) return res.end(watchHtml);
    if (req.url?.startsWith("/playlists")) return res.end(playlistsHtml);
    return res.end(channelHtml);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const base = `http://127.0.0.1:${server.address().port}`;

  const settingsHeader = {
    "X-Keyboard-Wtf-Settings-Action": "explicit-user-action",
    "Content-Type": "application/json",
  };
  const readGraph = async () => {
    const response = await fetch("http://localhost:8090/api/browser-companion/graph", { headers: settingsHeader });
    const json = await response.json();
    return json.graph || json.Graph;
  };
  const command = async (body) => {
    const response = await fetch("http://localhost:8090/api/browser-companion/command", {
      method: "POST",
      headers: settingsHeader,
      body: JSON.stringify(body),
    });
    const json = await response.json();
    if (!response.ok || !(json.ok ?? json.Ok)) {
      throw new Error(json.message || json.Message || "Browser Companion command failed.");
    }
    return json;
  };

  const context = await chromium.launchPersistentContext(profile, {
    headless: false,
    args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`],
  });
  try {
    const page = context.pages()[0] || await context.newPage();
    await page.goto(`${base}/@MrBeast/videos`, { waitUntil: "domcontentloaded" });
    await waitFor(async () => {
      const response = await fetch("http://localhost:8090/api/browser-companion/status");
      const json = await response.json();
      const status = json.status || json.Status;
      return status && (status.available ?? status.Available);
    }, "extension bridge connection");

    const initialGraph = await waitFor(async () => {
      const graph = await readGraph();
      return graph && controls(graph).length >= 8 ? graph : null;
    }, "initial graph");
    const labels = controls(initialGraph).map(text).join(" | ");
    for (const required of ["Videos", "Latest", "Popular", "Oldest", "Search"]) {
      if (!labels.toLowerCase().includes(required.toLowerCase())) {
        throw new Error(`Missing control: ${required}`);
      }
    }

    const oldest = controls(initialGraph).find((control) => {
      const primary = [prop(control, "text", "Text"), prop(control, "ariaLabel", "AriaLabel"), prop(control, "title", "Title")].filter(Boolean);
      return primary.some((value) => String(value).trim().toLowerCase() === "oldest") || text(control).toLowerCase().includes("oldest");
    });
    if (!oldest) throw new Error("Oldest control not found.");
    await command({ type: "click", controlId: prop(oldest, "controlId", "ControlId"), timeoutMs: 10000 });

    const afterOldest = await readGraph();
    if (!controls(afterOldest).some((control) => text(control).toLowerCase().includes("2008"))) {
      throw new Error("Video card not present after Oldest click.");
    }
    const video = controls(afterOldest).find((control) =>
      (prop(control, "href", "Href") || "").includes("/watch"))
      || controls(afterOldest).find((control) => text(control).toLowerCase().includes("2008"));
    if (!video) throw new Error("First video card control not found.");
    await command({ type: "click", controlId: prop(video, "controlId", "ControlId"), timeoutMs: 10000 });

    const afterVideo = await waitFor(async () => {
      const graph = await readGraph();
      return (prop(graph, "url", "Url") || "").includes("/watch") ? graph : null;
    }, "watch page graph");

    await page.goto(`${base}/playlists`, { waitUntil: "domcontentloaded" });
    const remapped = await waitFor(async () => {
      const graph = await readGraph();
      return (prop(graph, "url", "Url") || "").includes("/playlists")
        && controls(graph).some((control) => text(control).includes("Create playlist"))
        ? graph
        : null;
    }, "dynamic remap");

    console.log(JSON.stringify({
      ok: true,
      extensionPath,
      initialControls: controls(initialGraph).length,
      hasVideos: labels.includes("Videos"),
      hasLatest: labels.includes("Latest"),
      hasPopular: labels.includes("Popular"),
      hasOldest: labels.includes("Oldest"),
      clickedOldest: true,
      watchUrl: prop(afterVideo, "url", "Url"),
      remappedUrl: prop(remapped, "url", "Url"),
      remappedControls: controls(remapped).length,
    }, null, 2));
  } finally {
    await context.close();
    server.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
