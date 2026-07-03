namespace KeyboardWtf.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class PlaywrightBrowserBackendIntegrationTests
{
    [Fact]
    [Trait("Category", "BrowserIntegration")]
    public async Task HarmlessPublicWebsiteCanBeInspected()
    {
        var profile = TempProfile();
        try
        {
            await using var backend = new PlaywrightBrowserBackend(headless: true, profilePath: profile);

            var page = await backend.OpenAsync(
                "public-example",
                "https://example.com/",
                CancellationToken.None);

            Assert.True(page.Loaded);
            Assert.Contains("Example Domain", page.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Example Domain", page.VisibleText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "BrowserIntegration")]
    public async Task LocalMockFormCanBeFilledAndVerifiedWithoutSubmitting()
    {
        using var server = new MockFormServer();
        await server.StartAsync();
        var profile = TempProfile();
        try
        {
            await using var backend = new PlaywrightBrowserBackend(headless: true, profilePath: profile);
            await backend.OpenAsync("mock-form", server.Url, CancellationToken.None);

            var fill = await backend.ExecuteAsync(new BrowserAgentActionRequest
            {
                TaskId = "mock-form",
                Action = BrowserAgentActionKind.Fill,
                Role = "label",
                Target = "Project name",
                Value = "keyboard.wtf",
            }, CancellationToken.None);
            var preview = await backend.ExecuteAsync(new BrowserAgentActionRequest
            {
                TaskId = "mock-form",
                Action = BrowserAgentActionKind.Click,
                Role = "button",
                Target = "Preview",
            }, CancellationToken.None);
            var verify = await backend.ExecuteAsync(new BrowserAgentActionRequest
            {
                TaskId = "mock-form",
                Action = BrowserAgentActionKind.Verify,
                ExpectedText = "Preview: keyboard.wtf",
            }, CancellationToken.None);

            Assert.True(fill.Ok);
            Assert.True(preview.Ok);
            Assert.True(verify.Ok);
            Assert.True(verify.Verified);
        }
        finally
        {
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "BrowserIntegration")]
    public async Task ClosingOnlyTaskKeepsPersistentContextReusable()
    {
        using var server = new MockFormServer();
        await server.StartAsync();
        var profile = TempProfile();
        try
        {
            await using var backend = new PlaywrightBrowserBackend(headless: true, profilePath: profile);
            await backend.OpenAsync("first-task", server.Url, CancellationToken.None);
            await backend.CloseTaskAsync("first-task");

            var page = await backend.OpenAsync("second-task", server.Url, CancellationToken.None);

            Assert.True(page.Loaded);
            Assert.Contains("Jarvis Mock Form", page.Title, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "BrowserIntegration")]
    public async Task ExtractRanksHighestPrizePoolFromVisibleCards()
    {
        const string html =
            """
            <!doctype html>
            <html>
              <head><title>Mock Hackathons</title></head>
              <body>
                <article class="hackathon-card">
                  <h2>Small Builder Jam</h2>
                  <p>Prize pool: $5,000</p>
                </article>
                <article class="hackathon-card">
                  <h2>Global AI Challenge</h2>
                  <p>Prize pool: $100,000</p>
                </article>
                <article class="hackathon-card">
                  <h2>Local Student Hack</h2>
                  <p>Prize pool: $10,000</p>
                </article>
              </body>
            </html>
            """;
        using var server = new StaticHtmlServer(html);
        await server.StartAsync();
        var profile = TempProfile();
        try
        {
            await using var backend = new PlaywrightBrowserBackend(headless: true, profilePath: profile);
            await backend.OpenAsync("hackathons", server.Url, CancellationToken.None);

            var extraction = await backend.ExecuteAsync(new BrowserAgentActionRequest
            {
                TaskId = "hackathons",
                Action = BrowserAgentActionKind.Extract,
                Target = "highest prize pool hackathon",
            }, CancellationToken.None);

            Assert.True(extraction.Ok);
            Assert.Contains("Global AI Challenge", extraction.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("$100,000", extraction.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Global AI Challenge", extraction.Candidates[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    private static string TempProfile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "keyboard-wtf-playwright",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MockFormServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private CancellationTokenSource _cts;
        private Task _loop;

        public string Url { get; private set; }

        public async Task StartAsync()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
            await Task.Delay(50);
        }

        private async Task LoopAsync(CancellationToken token)
        {
            const string html =
                """
                <!doctype html>
                <html>
                  <head><title>Jarvis Mock Form</title></head>
                  <body>
                    <label>Project name <input id="project" /></label>
                    <button type="button" onclick="document.getElementById('result').textContent='Preview: '+document.getElementById('project').value">Preview</button>
                    <div id="result"></div>
                  </body>
                </html>
                """;
            var bytes = Encoding.UTF8.GetBytes(html);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(token);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, token);
                    context.Response.Close();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts?.Dispose();
            _listener.Close();
        }
    }

    private sealed class StaticHtmlServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _html;
        private CancellationTokenSource _cts;
        private Task _loop;

        public StaticHtmlServer(string html)
        {
            _html = html;
        }

        public string Url { get; private set; }

        public async Task StartAsync()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
            await Task.Delay(50);
        }

        private async Task LoopAsync(CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(_html);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(token);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, token);
                    context.Response.Close();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts?.Dispose();
            _listener.Close();
        }
    }
}
