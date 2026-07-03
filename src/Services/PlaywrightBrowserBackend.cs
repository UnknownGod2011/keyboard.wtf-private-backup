namespace KeyboardWtf.Services;

using System.Text.RegularExpressions;
using KeyboardWtf.Models;
using Microsoft.Playwright;

public interface IBrowserAutomationBackend : IAsyncDisposable
{
    BrowserBackendStatus Status { get; }
    Task<BrowserPageState> OpenAsync(string taskId, string url, CancellationToken token);
    Task<BrowserPageState> ObserveAsync(string taskId, CancellationToken token);
    Task<BrowserBackendActionResult> ExecuteAsync(BrowserAgentActionRequest request, CancellationToken token);
    Task CloseTaskAsync(string taskId);
}

public sealed class PlaywrightBrowserBackend : IBrowserAutomationBackend
{
    private readonly bool _headless;
    private readonly string _profilePath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly Dictionary<string, IPage> _pages = new(StringComparer.OrdinalIgnoreCase);
    private IPlaywright _playwright;
    private IBrowserContext _context;
    private string _channel = "";
    private string _lastError = "";

    public PlaywrightBrowserBackend(bool headless = false, string profilePath = null)
    {
        _headless = headless;
        _profilePath = string.IsNullOrWhiteSpace(profilePath)
            ? Path.Combine(SettingsService.AppDataDir, "browser-agent", "profile")
            : profilePath;
    }

    public BrowserBackendStatus Status => new()
    {
        Initialized = _context != null,
        Available = _context != null && string.IsNullOrWhiteSpace(_lastError),
        BrowserChannel = _channel,
        ProfilePath = _profilePath,
        LastError = _lastError,
    };

    public async Task<BrowserPageState> OpenAsync(
        string taskId,
        string url,
        CancellationToken token)
    {
        if (!BrowserAgentSafetyPolicy.IsSafeHttpUrl(url, out var uri))
            throw new InvalidOperationException("Browser tasks require an http or https start URL.");
        await EnsureInitializedAsync(token);
        await _actionLock.WaitAsync(token);
        try
        {
            var page = await PageForAsync(taskId, token);
            await page.GotoAsync(uri.AbsoluteUri, new()
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 25000,
            });
            return await SnapshotAsync(page);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public async Task<BrowserPageState> ObserveAsync(string taskId, CancellationToken token)
    {
        await EnsureInitializedAsync(token);
        await _actionLock.WaitAsync(token);
        try
        {
            var page = await PageForAsync(taskId, token);
            return await SnapshotAsync(page);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public async Task<BrowserBackendActionResult> ExecuteAsync(
        BrowserAgentActionRequest request,
        CancellationToken token)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        await EnsureInitializedAsync(token);
        await _actionLock.WaitAsync(token);
        try
        {
            var page = await PageForAsync(request.TaskId, token);
            switch (request.Action)
            {
                case BrowserAgentActionKind.Navigate:
                    if (!BrowserAgentSafetyPolicy.IsSafeHttpUrl(request.Value, out var uri))
                        return Failure("Navigation is limited to http and https URLs.");
                    await page.GotoAsync(uri.AbsoluteUri, new()
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 25000,
                    });
                    break;
                case BrowserAgentActionKind.Click:
                {
                    var match = await ResolveLocatorAsync(page, request);
                    if (!match.Ok)
                        return match.Result;
                    await match.Locator.ClickAsync(new() { Timeout = 12000 });
                    break;
                }
                case BrowserAgentActionKind.Fill:
                {
                    var match = await ResolveLocatorAsync(page, request);
                    if (!match.Ok)
                        return match.Result;
                    await match.Locator.FillAsync(request.Value ?? "", new() { Timeout = 12000 });
                    break;
                }
                case BrowserAgentActionKind.Select:
                {
                    var match = await ResolveLocatorAsync(page, request);
                    if (!match.Ok)
                        return match.Result;
                    await match.Locator.SelectOptionAsync(new[] { request.Value ?? "" }, new() { Timeout = 12000 });
                    break;
                }
                case BrowserAgentActionKind.Upload:
                {
                    var match = await ResolveLocatorAsync(page, request);
                    if (!match.Ok)
                        return match.Result;
                    await match.Locator.SetInputFilesAsync(request.FilePath, new() { Timeout = 12000 });
                    break;
                }
                case BrowserAgentActionKind.Press:
                    await page.Keyboard.PressAsync(NormalizeKey(request.Value));
                    break;
                case BrowserAgentActionKind.Wait:
                    await page.WaitForTimeoutAsync(Math.Clamp(
                        int.TryParse(request.Value, out var milliseconds) ? milliseconds : 750,
                        50,
                        5000));
                    break;
                case BrowserAgentActionKind.Verify:
                {
                    var state = await SnapshotAsync(page);
                    var expected = request.ExpectedText ?? request.Value ?? "";
                    var verified = !string.IsNullOrWhiteSpace(expected)
                        && state.VisibleText.Contains(expected, StringComparison.OrdinalIgnoreCase);
                    return new BrowserBackendActionResult
                    {
                        Ok = verified,
                        Verified = verified,
                        Message = verified
                            ? $"Verified visible text: {expected}."
                            : $"Could not verify visible text: {expected}.",
                        State = state,
                    };
                }
                case BrowserAgentActionKind.Extract:
                {
                    var state = await SnapshotAsync(page);
                    if (IsAutomationBlockedPage(state))
                    {
                        return new BrowserBackendActionResult
                        {
                            Ok = false,
                            MatchCount = 0,
                            Message = "This site is blocking or challenging the controlled browser session. Complete the site challenge manually or use a browser companion/extension flow, then ask Jarvis to continue.",
                            State = state,
                        };
                    }
                    var extraction = await ExtractVisibleCandidatesAsync(page, request);
                    return new BrowserBackendActionResult
                    {
                        Ok = !string.IsNullOrWhiteSpace(extraction.Message),
                        Verified = !string.IsNullOrWhiteSpace(extraction.Message),
                        Message = string.IsNullOrWhiteSpace(extraction.Message)
                            ? "No useful visible candidates were found on the current page."
                            : extraction.Message,
                        Extraction = extraction.Message,
                        Candidates = extraction.Candidates,
                        State = state,
                    };
                }
            }

            var pageState = await SnapshotAsync(page);
            return new BrowserBackendActionResult
            {
                Ok = true,
                Verified = pageState.Loaded,
                Message = $"{request.Action} completed on {pageState.Title}.",
                State = pageState,
            };
        }
        catch (TimeoutException ex)
        {
            return Failure($"The page did not become ready in time: {ex.Message}");
        }
        catch (PlaywrightException ex)
        {
            return Failure(ex.Message);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public async Task CloseTaskAsync(string taskId)
    {
        IPage page = null;
        await _actionLock.WaitAsync();
        try
        {
            if (_pages.Remove((taskId ?? "").Trim(), out page) && page != null && !page.IsClosed)
            {
                var otherOpenPageExists = _context?.Pages.Any(candidate =>
                    !ReferenceEquals(candidate, page) && !candidate.IsClosed) == true;
                if (otherOpenPageExists)
                {
                    await page.CloseAsync();
                }
                else
                {
                    // A Chromium persistent context can become unstable if its final tab is closed.
                    // Keep one blank reusable tab warm so the next task does not fail at Target.createTarget.
                    await page.GotoAsync("about:blank", new()
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 5000,
                    });
                }
            }
        }
        catch (PlaywrightException ex) when (IsBrokenTarget(ex))
        {
            await ResetBrowserContextAsync();
        }
        catch { }
        finally
        {
            _actionLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken token)
    {
        if (_context != null)
            return;
        await _initializeLock.WaitAsync(token);
        try
        {
            if (_context != null)
                return;
            Directory.CreateDirectory(_profilePath);
            _playwright = await Playwright.CreateAsync();
            Exception last = null;
            foreach (var channel in new[] { "msedge", "chrome" })
            {
                try
                {
                    _context = await _playwright.Chromium.LaunchPersistentContextAsync(
                        _profilePath,
                        new BrowserTypeLaunchPersistentContextOptions
                        {
                            Channel = channel,
                            Headless = _headless,
                            AcceptDownloads = false,
                            ViewportSize = ViewportSize.NoViewport,
                            Args = ["--disable-background-networking"],
                        });
                    _channel = channel;
                    _lastError = "";
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            _lastError = last?.Message ?? "No supported browser channel was available.";
            throw new InvalidOperationException(
                "Jarvis Browser Agent could not launch installed Microsoft Edge or Google Chrome.",
                last);
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private async Task<IPage> PageForAsync(string taskId, CancellationToken token)
    {
        var id = (taskId ?? "").Trim();
        if (id.Length == 0)
            throw new InvalidOperationException("A browser task id is required.");
        if (_pages.TryGetValue(id, out var existing) && !existing.IsClosed)
            return existing;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var page = _context.Pages.FirstOrDefault(candidate =>
                    !candidate.IsClosed && !_pages.Values.Any(open => ReferenceEquals(open, candidate)))
                    ?? await _context.NewPageAsync();
                _pages[id] = page;
                return page;
            }
            catch (PlaywrightException ex) when (attempt == 0 && IsBrokenTarget(ex))
            {
                await ResetBrowserContextAsync();
                await EnsureInitializedAsync(token);
            }
        }

        throw new InvalidOperationException("Jarvis Browser Agent could not open a browser tab.");
    }

    private static async Task<BrowserPageState> SnapshotAsync(IPage page)
    {
        var title = await page.TitleAsync();
        string visibleText;
        try
        {
            visibleText = await page.Locator("body").InnerTextAsync(new() { Timeout = 8000 });
        }
        catch
        {
            visibleText = "";
        }
        string aria;
        try
        {
            aria = await page.Locator("body").AriaSnapshotAsync();
        }
        catch
        {
            aria = "";
        }
        BrowserElementSummary[] interactive;
        try
        {
                interactive = await page.Locator("a,button,input,textarea,select,[role]")
                .EvaluateAllAsync<BrowserElementSummary[]>(
                    """
                    elements => elements.slice(0, 150).map(element => ({
                      role: element.getAttribute('role') || element.tagName.toLowerCase(),
                      name: (element.getAttribute('aria-label') || element.innerText || element.value || '').trim().slice(0, 180),
                      label: (element.labels && element.labels[0] ? element.labels[0].innerText : '').trim().slice(0, 180),
                      placeholder: (element.getAttribute('placeholder') || '').trim().slice(0, 180),
                      type: (element.getAttribute('type') || '').trim().slice(0, 80)
                    }))
                    """) ?? Array.Empty<BrowserElementSummary>();
        }
        catch
        {
            interactive = Array.Empty<BrowserElementSummary>();
        }
        return new BrowserPageState
        {
            Title = Limit(title, 300),
            Url = Limit(page.Url, 2000),
            VisibleText = Limit(visibleText, 10000),
            AriaSnapshot = Limit(aria, 14000),
            InteractiveElements = interactive,
            Loaded = !string.IsNullOrWhiteSpace(page.Url)
                && !string.Equals(page.Url, "about:blank", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static async Task<ExtractionSummary> ExtractVisibleCandidatesAsync(
        IPage page,
        BrowserAgentActionRequest request)
    {
        var query = string.Join(" ", new[] { request.Target, request.Value, request.ExpectedText }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        DomTextCandidate[] domCandidates;
        try
        {
            domCandidates = await page.Locator("body").EvaluateAsync<DomTextCandidate[]>(
                """
                (body, query) => {
                  const words = String(query || "")
                    .toLowerCase()
                    .split(/[^a-z0-9]+/)
                    .filter(word => word.length > 2 && !["the","and","for","with","from","find","search","highest","lowest","best"].includes(word));
                  const nodes = Array.from(document.querySelectorAll("article,section,li,tr,div"));
                  const seen = new Set();
                  const rows = [];
                  for (const element of nodes) {
                    const rect = element.getBoundingClientRect();
                    if (rect.width < 60 || rect.height < 12) continue;
                    const text = (element.innerText || element.textContent || "").replace(/\s+/g, " ").trim();
                    if (text.length < 20 || text.length > 900) continue;
                    const lower = text.toLowerCase();
                    const wordHits = words.filter(word => lower.includes(word)).length;
                    const hasUsefulSignal = /[$₹€£]|\b(rs\.?|inr|usd|prize|reward|cash|grant|award|price|rating|stars?|showtime|movie|hackathon|deadline)\b/i.test(text);
                    if (wordHits === 0 && !hasUsefulSignal) continue;
                    const key = text.slice(0, 240);
                    if (seen.has(key)) continue;
                    seen.add(key);
                    rows.push({
                      text: text.slice(0, 900),
                      tag: element.tagName.toLowerCase(),
                      top: Math.round(rect.top + window.scrollY)
                    });
                    if (rows.length >= 80) break;
                  }
                  return rows;
                }
                """,
                query) ?? Array.Empty<DomTextCandidate>();
        }
        catch
        {
            domCandidates = Array.Empty<DomTextCandidate>();
        }

        if (domCandidates.Length == 0)
        {
            var pageState = await SnapshotAsync(page);
            domCandidates = pageState.VisibleText
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Length is > 20 and < 900)
                .Take(80)
                .Select((line, index) => new DomTextCandidate { Text = line, Tag = "text", Top = index })
                .ToArray();
        }

        var normalizedQuery = FuzzyMatcher.Normalize(query);
        var queryWords = normalizedQuery
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ranked = domCandidates
            .Select(candidate => RankCandidate(candidate, queryWords, normalizedQuery))
            .Where(candidate => candidate.Score > 0)
            .GroupBy(candidate => candidate.NormalizedText)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .ToArray();

        if (ranked.Length == 0)
            return new ExtractionSummary();

        var wantsLowest = ContainsAny(normalizedQuery, "cheapest", "lowest", "low price", "least expensive");
        var wantsHighest = ContainsAny(normalizedQuery, "highest", "largest", "biggest", "top", "maximum", "max", "prize");
        var withMoney = ranked.Where(candidate => candidate.Amount.HasValue).ToArray();
        RankedDomCandidate[] ordered;
        if (withMoney.Length > 0)
        {
            ordered = (wantsLowest
                    ? withMoney.OrderBy(candidate => candidate.Amount.Value)
                    : withMoney.OrderByDescending(candidate => candidate.Amount.Value))
                .ThenByDescending(candidate => candidate.Score)
                .Take(8)
                .ToArray();
        }
        else
        {
            ordered = ranked
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Top)
                .Take(8)
                .ToArray();
        }

        ordered = CollapseNearDuplicates(ordered)
            .Take(8)
            .ToArray();

        var candidates = ordered
            .Take(6)
            .Select(candidate => candidate.Display)
            .ToArray();
        var lead = ordered[0];
        var message = lead.Amount.HasValue && wantsHighest
            ? $"Highest visible value found: {lead.Display}. Top visible candidates: {string.Join(" | ", candidates)}"
            : lead.Amount.HasValue && wantsLowest
                ? $"Lowest visible value found: {lead.Display}. Top visible candidates: {string.Join(" | ", candidates)}"
                : $"Visible candidates: {string.Join(" | ", candidates)}";
        return new ExtractionSummary
        {
            Message = Limit(message, 1600),
            Candidates = candidates,
        };
    }

    private static async Task<LocatorMatch> ResolveLocatorAsync(
        IPage page,
        BrowserAgentActionRequest request)
    {
        var locator = BuildLocator(page, request, exact: true);
        var count = await locator.CountAsync();
        if (count == 0)
        {
            locator = BuildLocator(page, request, exact: false);
            count = await locator.CountAsync();
        }
        if (count == 1)
            return new LocatorMatch { Ok = true, Locator = locator.First };

        var candidates = new List<string>();
        var hrefs = new List<string>();
        if (count > 1)
        {
            for (var index = 0; index < Math.Min(6, count); index++)
            {
                var candidate = locator.Nth(index);
                var text = "";
                try { text = await candidate.InnerTextAsync(new() { Timeout = 1000 }); } catch { }
                if (string.IsNullOrWhiteSpace(text))
                {
                    try { text = await candidate.GetAttributeAsync("aria-label") ?? ""; } catch { }
                }
                try
                {
                    var href = await candidate.EvaluateAsync<string>(
                        "element => element.href || element.closest('a')?.href || ''");
                    if (!string.IsNullOrWhiteSpace(href))
                        hrefs.Add(href.Trim());
                }
                catch { }
                candidates.Add(string.IsNullOrWhiteSpace(text) ? $"match {index + 1}" : Limit(text, 120));
            }
            if (hrefs.Count > 0 && hrefs.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                return new LocatorMatch { Ok = true, Locator = locator.First };
        }
        return new LocatorMatch
        {
            Result = new BrowserBackendActionResult
            {
                Ok = false,
                Ambiguous = count > 1,
                MatchCount = count,
                Candidates = candidates,
                Message = count == 0
                    ? $"No page element matched {request.Target}."
                    : $"{count} page elements matched {request.Target}; clarification is required.",
            },
        };
    }

    private static ILocator BuildLocator(
        IPage page,
        BrowserAgentActionRequest request,
        bool exact)
    {
        var target = request.Target ?? "";
        var roleText = FuzzyMatcher.Normalize(request.Role);
        if (Enum.TryParse<AriaRole>(roleText, true, out var role))
            return page.GetByRole(role, new() { Name = target, Exact = exact });
        if (roleText is "label" or "field" or "input")
            return page.GetByLabel(target, new() { Exact = exact });
        if (roleText == "placeholder")
            return page.GetByPlaceholder(target, new() { Exact = exact });
        if (request.Action == BrowserAgentActionKind.Upload)
            return page.GetByLabel(target, new() { Exact = exact });
        return page.GetByText(target, new() { Exact = exact });
    }

    private static string NormalizeKey(string value) => FuzzyMatcher.Normalize(value) switch
    {
        "shift tab" => "Shift+Tab",
        "escape" => "Escape",
        "arrowup" => "ArrowUp",
        "arrowdown" => "ArrowDown",
        "arrowleft" => "ArrowLeft",
        "arrowright" => "ArrowRight",
        "pageup" => "PageUp",
        "pagedown" => "PageDown",
        "home" => "Home",
        "end" => "End",
        _ => "Tab",
    };

    private static BrowserBackendActionResult Failure(string message) => new()
    {
        Ok = false,
        Message = message,
    };

    private static string Limit(string value, int maxLength)
    {
        value ??= "";
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string Shorten(string value, int maxLength)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..Math.Max(0, maxLength - 1)].Trim() + "…";
    }

    private static RankedDomCandidate RankCandidate(
        DomTextCandidate candidate,
        IReadOnlyList<string> queryWords,
        string normalizedQuery)
    {
        var text = candidate.Text ?? "";
        var normalizedText = FuzzyMatcher.Normalize(text);
        var wordHits = queryWords.Count(word => normalizedText.Contains(word, StringComparison.Ordinal));
        var amount = TryExtractAmount(text, out var amountText, out var numericAmount) ? numericAmount : (double?)null;
        var score = wordHits * 3;
        if (amount.HasValue)
            score += 8;
        if (normalizedText.Contains("prize", StringComparison.Ordinal)
            || normalizedText.Contains("award", StringComparison.Ordinal)
            || normalizedText.Contains("reward", StringComparison.Ordinal))
        {
            score += normalizedQuery.Contains("prize", StringComparison.Ordinal) ? 7 : 3;
        }
        if (normalizedText.Contains("hackathon", StringComparison.Ordinal))
            score += normalizedQuery.Contains("hackathon", StringComparison.Ordinal) ? 5 : 1;
        if (normalizedText.Contains("rating", StringComparison.Ordinal)
            || normalizedText.Contains("stars", StringComparison.Ordinal)
            || normalizedText.Contains("price", StringComparison.Ordinal))
        {
            score += 2;
        }

        var title = ExtractTitle(text, amountText);
        var display = !string.IsNullOrWhiteSpace(amountText)
            ? $"{title} — {amountText}"
            : $"{title} — {Shorten(text, 120)}";
        return new RankedDomCandidate
        {
            Text = text,
            Title = title,
            NormalizedText = Shorten(normalizedText, 260),
            Top = candidate.Top,
            Score = score,
            Amount = amount,
            Display = Shorten(display, 220),
        };
    }

    private static IEnumerable<RankedDomCandidate> CollapseNearDuplicates(
        IEnumerable<RankedDomCandidate> candidates)
    {
        var kept = new List<RankedDomCandidate>();
        foreach (var candidate in candidates)
        {
            var duplicate = kept.Any(existing =>
                candidate.Amount.HasValue
                && existing.Amount.HasValue
                && Math.Abs(candidate.Amount.Value - existing.Amount.Value) < 0.01
                && TitlesOverlap(candidate.Title, existing.Title));
            if (!duplicate)
                kept.Add(candidate);
        }
        return kept;
    }

    private static bool TitlesOverlap(string left, string right)
    {
        if ((left ?? "").TrimStart().StartsWith("$", StringComparison.Ordinal)
            || (right ?? "").TrimStart().StartsWith("$", StringComparison.Ordinal)
            || (left ?? "").TrimStart().StartsWith("₹", StringComparison.Ordinal)
            || (right ?? "").TrimStart().StartsWith("₹", StringComparison.Ordinal))
        {
            return true;
        }
        var a = FuzzyMatcher.Normalize(left);
        var b = FuzzyMatcher.Normalize(right);
        if (a.Length == 0 || b.Length == 0)
            return false;
        return a.Contains(b, StringComparison.Ordinal)
            || b.Contains(a, StringComparison.Ordinal)
            || FuzzyMatcher.Score(a, b) >= 0.88;
    }

    private static bool TryExtractAmount(string text, out string amountText, out double amount)
    {
        amountText = "";
        amount = 0;
        var match = MoneyPattern.Match(text ?? "");
        if (!match.Success)
            return false;
        amountText = match.Value.Trim();
        var normalized = amountText.ToLowerInvariant();
        var multiplier = 1d;
        if (normalized.Contains("crore", StringComparison.Ordinal)
            || normalized.Contains(" cr", StringComparison.Ordinal))
            multiplier = 10_000_000d;
        else if (normalized.Contains("lakh", StringComparison.Ordinal)
            || normalized.Contains(" lac", StringComparison.Ordinal))
            multiplier = 100_000d;
        else if (Regex.IsMatch(normalized, @"\d\s*k\b"))
            multiplier = 1_000d;
        else if (Regex.IsMatch(normalized, @"\d\s*m\b"))
            multiplier = 1_000_000d;
        else if (Regex.IsMatch(normalized, @"\d\s*b\b"))
            multiplier = 1_000_000_000d;

        var numeric = Regex.Match(normalized, @"[\d,.]+");
        if (!numeric.Success)
            return false;
        if (!double.TryParse(numeric.Value.Replace(",", ""), out var parsed))
            return false;
        amount = parsed * multiplier;
        return amount > 0;
    }

    private static string ExtractTitle(string text, string amountText)
    {
        var clean = string.Join(
            " ",
            (text ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!string.IsNullOrWhiteSpace(amountText))
        {
            var index = clean.IndexOf(amountText, StringComparison.OrdinalIgnoreCase);
            if (index > 8)
                clean = clean[..index].Trim(' ', '-', '•', '|', ':');
        }
        clean = Regex.Replace(clean, @"\b(prize pool|prizes?|awards?)\b.*$", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Visible option" : Shorten(clean, 90);
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(FuzzyMatcher.Normalize(term), StringComparison.Ordinal));

    private static bool IsBrokenTarget(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("Target.createTarget", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Browser has been closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("context has been closed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAutomationBlockedPage(BrowserPageState state)
    {
        var text = FuzzyMatcher.Normalize($"{state?.Title} {state?.VisibleText}");
        return text.Contains("cloudflare", StringComparison.Ordinal)
            || text.Contains("you have been blocked", StringComparison.Ordinal)
            || text.Contains("attention required", StringComparison.Ordinal)
            || text.Contains("checking your browser", StringComparison.Ordinal)
            || text.Contains("captcha", StringComparison.Ordinal)
            || text.Contains("verify you are human", StringComparison.Ordinal);
    }

    private async Task ResetBrowserContextAsync()
    {
        _pages.Clear();
        try
        {
            if (_context != null)
                await _context.CloseAsync();
        }
        catch { }
        _context = null;
        _channel = "";
        try { _playwright?.Dispose(); } catch { }
        _playwright = null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var page in _pages.Values.ToArray())
        {
            try { await page.CloseAsync(); } catch { }
        }
        _pages.Clear();
        try
        {
            if (_context != null)
                await _context.CloseAsync();
        }
        catch { }
        _context = null;
        _playwright?.Dispose();
        _playwright = null;
        _initializeLock.Dispose();
        _actionLock.Dispose();
    }

    private sealed class LocatorMatch
    {
        public bool Ok { get; init; }
        public ILocator Locator { get; init; }
        public BrowserBackendActionResult Result { get; init; }
    }

    private sealed class DomTextCandidate
    {
        public string Text { get; init; } = "";
        public string Tag { get; init; } = "";
        public int Top { get; init; }
    }

    private sealed class RankedDomCandidate
    {
        public string Text { get; init; } = "";
        public string Title { get; init; } = "";
        public string NormalizedText { get; init; } = "";
        public int Top { get; init; }
        public double Score { get; init; }
        public double? Amount { get; init; }
        public string Display { get; init; } = "";
    }

    private sealed class ExtractionSummary
    {
        public string Message { get; init; } = "";
        public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();
    }

    private static readonly Regex MoneyPattern = new(
        @"(?:(?:[$₹€£]|rs\.?|inr|usd)\s*[\d,.]+(?:\s*(?:k|m|b|lakh|lakhs|lac|lacs|crore|cr))?)|(?:[\d,.]+\s*(?:k|m|b|lakh|lakhs|lac|lacs|crore|cr)?\s*(?:usd|inr|dollars?|rupees?))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
