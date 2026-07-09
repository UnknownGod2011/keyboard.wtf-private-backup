namespace KeyboardWtf.Services;

using System.Text.RegularExpressions;

public sealed class BrowserAutomationTaskClassifier
{
    private static readonly Regex SimpleOpenOrSearch = new(
        @"^\s*(open|go to|visit|search|look up|find)\s+[^.?!]*(youtube|amazon|google|github|flipkart|wikipedia|imdb|maps|devpost)[^.?!]*[.?!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BrowserAutomationClassification Classify(string taskText)
    {
        var text = (taskText ?? "").Trim();
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new BrowserAutomationClassification
            {
                ShouldUseBrowserAutomation = false,
                Reason = "Empty task.",
            };
        }

        var site = DetectSite(normalized);
        var actionLevel = DetectActionLevel(normalized);
        var taskType = DetectTaskType(normalized);
        var isComplex = IsComplexBrowserTask(normalized, actionLevel, taskType);

        if (!isComplex && SimpleOpenOrSearch.IsMatch(text))
        {
            return new BrowserAutomationClassification
            {
                ShouldUseBrowserAutomation = false,
                TaskType = taskType,
                TargetSite = site,
                ActionLevel = actionLevel,
                Reason = "Simple open/search/browser task; use existing keyboard.wtf tools.",
            };
        }

        if (!isComplex)
        {
            return new BrowserAutomationClassification
            {
                ShouldUseBrowserAutomation = false,
                TaskType = taskType,
                TargetSite = site,
                ActionLevel = actionLevel,
                Reason = "No multi-step page-state browser automation needed.",
            };
        }

        return new BrowserAutomationClassification
        {
            ShouldUseBrowserAutomation = true,
            TaskType = taskType,
            TargetSite = site,
            ActionLevel = actionLevel,
            Profile = taskType is "knowledge_extract" or "repo_inspect" ? "fast" : "smart",
            Reason = "Multi-step page-state browser automation.",
            SuccessCriteria = BuildSuccessCriteria(taskType, normalized),
        };
    }

    public static bool IsCartMutation(BrowserAutomationActionLevel level) =>
        level == BrowserAutomationActionLevel.CartMutationAfterConfirmation;

    public static bool IsProtectedFinalAction(BrowserAutomationActionLevel level) =>
        level == BrowserAutomationActionLevel.ProtectedFinalActionBlocked;

    private static bool IsComplexBrowserTask(string normalized, BrowserAutomationActionLevel actionLevel, string taskType)
    {
        if (actionLevel is BrowserAutomationActionLevel.CartMutationAfterConfirmation or BrowserAutomationActionLevel.ProtectedFinalActionBlocked)
            return true;

        if (taskType is "youtube_oldest_video" or "youtube_recent_video" or "embedded_video_traversal"
            or "product_cta_inspection" or "product_page_inspection" or "maps_lookup"
            or "movie_info_lookup" or "popup_navigation")
            return true;

        return ContainsAny(normalized,
            "then open",
            "inside that page",
            "embedded video",
            "open the first result",
            "first relevant result",
            "close popup",
            "close the login popup",
            "report cta",
            "cta state",
            "tell me whether add to cart",
            "find the button",
            "fill this form",
            "stop before submit");
    }

    private static BrowserAutomationActionLevel DetectActionLevel(string normalized)
    {
        if (ContainsAny(normalized, "checkout", "payment", "place order", "pay now", "enter password", "otp", "captcha", "banking"))
            return BrowserAutomationActionLevel.ProtectedFinalActionBlocked;
        if (ContainsAny(normalized, "add to cart", "add them to cart", "cart page", "basket"))
            return BrowserAutomationActionLevel.CartMutationAfterConfirmation;
        if (ContainsAny(normalized, "fill form", "fill this form", "write in google docs", "type into"))
            return BrowserAutomationActionLevel.FormFillPreview;
        if (ContainsAny(normalized, "summarize", "report", "tell me whether", "inspect", "rating", "stars", "forks"))
            return BrowserAutomationActionLevel.ReadOnly;
        return BrowserAutomationActionLevel.Navigation;
    }

    private static string DetectTaskType(string normalized)
    {
        if (ContainsAny(normalized, "oldest") && normalized.Contains("youtube", StringComparison.Ordinal))
            return "youtube_oldest_video";
        if (ContainsAny(normalized, "most recent", "latest") && normalized.Contains("youtube", StringComparison.Ordinal))
            return "youtube_recent_video";
        if (ContainsAny(normalized, "add to cart", "cart page", "add them to cart"))
            return "ecommerce_add_to_cart";
        if (ContainsAny(normalized, "cta state", "add to cart / buy now", "notify me", "unavailable"))
            return "product_cta_inspection";
        if (normalized.Contains("amazon", StringComparison.Ordinal) && ContainsAny(normalized, "title/price", "title and price", "visible title", "price"))
            return "product_page_inspection";
        if (normalized.Contains("youtube video inside", StringComparison.Ordinal) || normalized.Contains("embedded video", StringComparison.Ordinal))
            return "embedded_video_traversal";
        if (normalized.Contains("google maps", StringComparison.Ordinal) || normalized.Contains("maps", StringComparison.Ordinal))
            return "maps_lookup";
        if (normalized.Contains("imdb", StringComparison.Ordinal))
            return "movie_info_lookup";
        if (normalized.Contains("github", StringComparison.Ordinal) && ContainsAny(normalized, "stars", "forks", "repo"))
            return "repo_inspect";
        if (normalized.Contains("wikipedia", StringComparison.Ordinal) && ContainsAny(normalized, "summarize", "first paragraph"))
            return "knowledge_extract";
        if (ContainsAny(normalized, "close popup", "dismiss popup", "cookie banner", "login popup"))
            return "popup_navigation";
        return "multi_step_browser_task";
    }

    private static string DetectSite(string normalized)
    {
        if (normalized.Contains("youtube", StringComparison.Ordinal)) return "youtube.com";
        if (normalized.Contains("amazon india", StringComparison.Ordinal) || normalized.Contains("amazon.in", StringComparison.Ordinal)) return "amazon.in";
        if (normalized.Contains("amazon", StringComparison.Ordinal)) return "amazon.com";
        if (normalized.Contains("flipkart", StringComparison.Ordinal)) return "flipkart.com";
        if (normalized.Contains("devpost", StringComparison.Ordinal)) return "devpost.com";
        if (normalized.Contains("github", StringComparison.Ordinal)) return "github.com";
        if (normalized.Contains("wikipedia", StringComparison.Ordinal)) return "wikipedia.org";
        if (normalized.Contains("imdb", StringComparison.Ordinal)) return "imdb.com";
        if (normalized.Contains("google maps", StringComparison.Ordinal) || normalized.Contains("maps", StringComparison.Ordinal)) return "google.com/maps";
        if (normalized.Contains("google", StringComparison.Ordinal)) return "google.com";
        return "";
    }

    private static string[] BuildSuccessCriteria(string taskType, string normalized)
    {
        return taskType switch
        {
            "youtube_oldest_video" or "youtube_recent_video" => new[]
            {
                "Final URL is a real YouTube watch page.",
                "The requested channel/entity is used exactly.",
                "A video player is visible or detected.",
                "Stop immediately after success.",
            },
            "ecommerce_add_to_cart" => new[]
            {
                "Requested products are searched/selected.",
                "Cart mutation is treated as one upfront confirmed action.",
                "Final page is cart or add-to-cart confirmation.",
                "Never checkout, pay, place order, or change address/payment settings.",
            },
            "product_cta_inspection" => new[]
            {
                "Requested product/search result page is opened.",
                "Visible CTA state is reported.",
                "No cart, checkout, payment, or account mutation occurs.",
            },
            _ => new[]
            {
                "Use the real requested website.",
                "Report exact blocker if the flow cannot continue.",
                "Stop after success or blocker.",
            },
        };
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string value) =>
        string.Join(" ", (value ?? "").Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
