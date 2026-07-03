import http from "node:http";

export const pages = {
  "/devpost": `<!doctype html>
<html><head><title>Mock Devpost Hackathons</title></head>
<body>
  <main>
    <article class="hackathon-card"><h2>Neighborhood Climate Hack</h2><p>Prize pool: $12,000</p><p>Online</p></article>
    <article class="hackathon-card"><h2>Global AI Challenge</h2><p>Total prizes: $100,000</p><p>Online</p></article>
    <article class="hackathon-card"><h2>Student Builder Jam</h2><p>Prize pool: $25,000</p><p>Hybrid</p></article>
  </main>
</body></html>`,
  "/devpost-changed": `<!doctype html>
<html><head><title>Mock Devpost Changed Layout</title></head>
<body>
  <main>
    <section data-card="event"><header>Student Builder Jam</header><span>Awards available: USD 25k</span></section>
    <section data-card="event"><header>Global AI Challenge</header><span>Awards available: USD 100k</span></section>
    <section data-card="event"><header>Neighborhood Climate Hack</header><span>Awards available: USD 12k</span></section>
  </main>
</body></html>`,
  "/store": `<!doctype html>
<html><head><title>Mock Store</title></head>
<body>
  <main>
    <article class="product"><h2>Alpha Keyboard</h2><p>Price: $129</p><p>Rating: 4.8 stars</p><button>Add to cart</button></article>
    <article class="product"><h2>Budget Keyboard</h2><p>Price: $79</p><p>Rating: 4.1 stars</p><button>Add to cart</button></article>
    <article class="product"><h2>Pro Creator Keyboard</h2><p>Price: $149</p><p>Rating: 4.9 stars</p><button>Add to cart</button></article>
  </main>
</body></html>`,
  "/form": `<!doctype html>
<html><head><title>Mock Project Form</title></head>
<body>
  <main>
    <label>Project name <input id="project" /></label>
    <label>Summary <textarea id="summary"></textarea></label>
    <button type="button" id="preview" onclick="document.getElementById('result').textContent='Preview: '+document.getElementById('project').value+' / '+document.getElementById('summary').value">Preview</button>
    <button type="submit">Submit</button>
    <div id="result">No preview yet.</div>
  </main>
</body></html>`,
  "/portfolio": `<!doctype html>
<html><head><title>Mock Portfolio</title></head>
<body>
  <main>
    <h1>My Devpost portfolio</h1>
    <article class="project"><h2>LaunchPilot</h2><p>AI launch planning workspace.</p></article>
    <article class="project"><h2>Cursivis</h2><p>Voice-first writing companion.</p></article>
  </main>
</body></html>`,
};

export async function startMockServer() {
  const server = http.createServer((request, response) => {
    const body = pages[request.url] ?? pages["/devpost"];
    response.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    response.end(body);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  return {
    baseUrl: `http://127.0.0.1:${port}`,
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}
