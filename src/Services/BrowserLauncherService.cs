namespace KeyboardWtf.Services;

using System.Diagnostics;
using Microsoft.Win32;

public sealed class BrowserLauncherService
{
    private static readonly Dictionary<string, string[]> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = ["Google Chrome", "Chrome"],
        ["edge"] = ["Microsoft Edge", "Edge"],
        ["brave"] = ["Brave"],
        ["firefox"] = ["Mozilla Firefox", "Firefox"],
        ["opera"] = ["Opera"],
        ["vivaldi"] = ["Vivaldi"],
        ["arc"] = ["Arc"],
    };

    private readonly AppResolverService _apps;
    private readonly SettingsService _settings;

    public BrowserLauncherService(AppResolverService apps, SettingsService settings)
    {
        _apps = apps;
        _settings = settings;
    }

    public bool TryOpen(string url, string requestedBrowser, out string message, out string browserUsed)
    {
        message = "";
        browserUsed = "";
        if (!TryNormalizeUrl(url, out var uri))
        {
            message = "Only valid http/https links or web hostnames can be opened.";
            return false;
        }

        var browser = NormalizeBrowser(requestedBrowser);
        if (string.IsNullOrWhiteSpace(browser))
            browser = NormalizeBrowser(_settings.Current.PreferredBrowser);
        if (string.IsNullOrWhiteSpace(browser))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
                browserUsed = "default browser";
                message = $"Opened {uri.Host} in the default browser.";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        var candidate = FindBrowserExecutable(browser);
        if (candidate == null)
        {
            message = $"{DisplayBrowser(browser)} is not installed or does not expose a safe executable. Ask whether to use the default browser.";
            return false;
        }

        try
        {
            var path = candidate.LaunchTarget.StartsWith("path:", StringComparison.OrdinalIgnoreCase)
                ? candidate.LaunchTarget[5..]
                : candidate.LaunchTarget;
            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
            };
            info.ArgumentList.Add(uri.ToString());
            Process.Start(info);
            browserUsed = DisplayBrowser(browser);
            message = $"Opened {uri.Host} in {browserUsed}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"{DisplayBrowser(browser)} could not open the link: {ex.Message}";
            return false;
        }
    }

    public AppCandidate FindBrowserExecutable(string browser)
    {
        var normalized = NormalizeBrowser(browser);
        if (string.IsNullOrWhiteSpace(normalized) || !BrowserNames.TryGetValue(normalized, out var names))
            return null;

        var candidates = _apps.Discover();
        return candidates
            .Where(candidate => candidate.LaunchTarget.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            .Where(candidate => Path.GetExtension(candidate.LaunchTarget[5..])
                .Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(candidate => AppResolverService.IsTrustedLaunchPath(candidate.LaunchTarget[5..]))
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = names.Max(name => FuzzyMatcher.Score(name, candidate.Name)),
            })
            .Where(item => item.Score >= 0.78)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Candidate)
            .FirstOrDefault();
    }

    public static string BuildSearchUrl(string query, string service)
    {
        var encoded = Uri.EscapeDataString((query ?? "").Trim());
        return FuzzyMatcher.Normalize(service) switch
        {
            "youtube" or "youtube music" => $"https://www.youtube.com/results?search_query={encoded}",
            "github" => $"https://github.com/search?q={encoded}",
            "bing" => $"https://www.bing.com/search?q={encoded}",
            _ => $"https://www.google.com/search?q={encoded}",
        };
    }

    public static string NormalizeBrowser(string browser)
    {
        var normalized = FuzzyMatcher.Normalize(browser);
        return normalized switch
        {
            "" or "default" or "default browser" => "",
            "google chrome" or "chrome" => "chrome",
            "microsoft edge" or "edge" => "edge",
            "brave browser" or "brave" => "brave",
            "mozilla firefox" or "firefox" => "firefox",
            "opera browser" or "opera" => "opera",
            "vivaldi browser" or "vivaldi" => "vivaldi",
            "arc browser" or "arc" => "arc",
            _ => normalized,
        };
    }

    public static bool TryNormalizeUrl(string url, out Uri uri)
    {
        uri = null;
        var candidate = (url ?? "").Trim().Trim('"', '\'');
        if (candidate.Length == 0)
            return false;

        var hasExplicitScheme = candidate.Contains("://", StringComparison.Ordinal);
        if (!hasExplicitScheme)
        {
            // A selected or spoken web destination may reasonably be a bare
            // hostname. Infer HTTPS only for an unambiguous DNS/IP host; never
            // reinterpret free text, paths, credentials, or another scheme.
            if (candidate.Any(char.IsWhiteSpace)
                || candidate.Contains('\\')
                || candidate.Contains('@')
                || candidate.StartsWith("//", StringComparison.Ordinal)
                || !Uri.TryCreate("https://" + candidate, UriKind.Absolute, out var inferred)
                || string.IsNullOrWhiteSpace(inferred.Host)
                || (Uri.CheckHostName(inferred.Host) == UriHostNameType.Unknown)
                || (!inferred.Host.Contains('.')
                    && Uri.CheckHostName(inferred.Host) != UriHostNameType.IPv4
                    && Uri.CheckHostName(inferred.Host) != UriHostNameType.IPv6))
            {
                return false;
            }

            candidate = inferred.AbsoluteUri;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out uri)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    internal bool UsesChrome(string requestedBrowser)
    {
        var chosen = NormalizeBrowser(requestedBrowser);
        if (chosen.Length == 0) chosen = NormalizeBrowser(_settings.Current.PreferredBrowser);
        if (chosen.Length != 0) return chosen == "chrome";
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return IsChromeAssociation(choice?.GetValue("ProgId") as string);
        }
        catch { return false; } // Unknown default is never permission to substitute Chrome.
    }

    internal static bool IsChromeAssociation(string progId) =>
        string.Equals(progId, "ChromeHTML", StringComparison.OrdinalIgnoreCase)
        || (progId ?? "").StartsWith("ChromeHTML.", StringComparison.OrdinalIgnoreCase);

    private static string DisplayBrowser(string browser) => NormalizeBrowser(browser) switch
    {
        "chrome" => "Chrome",
        "edge" => "Edge",
        "brave" => "Brave",
        "firefox" => "Firefox",
        "opera" => "Opera",
        "vivaldi" => "Vivaldi",
        "arc" => "Arc",
        _ => browser,
    };
}
