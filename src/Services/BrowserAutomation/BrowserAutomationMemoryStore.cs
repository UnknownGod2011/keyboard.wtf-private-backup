namespace KeyboardWtf.Services;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class BrowserAutomationMemoryStore
{
    private const int MaxPatterns = 50;
    private readonly string _path;
    private readonly object _lock = new();
    private List<BrowserAutomationPattern> _patterns;

    public BrowserAutomationMemoryStore(string path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(SettingsService.AppDataDir, "browser-automation-patterns.json")
            : path;
    }

    public string MemoryPath => _path;

    public string BuildPromptContext(BrowserAutomationClassification classification)
    {
        var matches = Load()
            .Where(pattern =>
                string.Equals(pattern.Domain, classification.TargetSite, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pattern.TaskType, classification.TaskType, StringComparison.OrdinalIgnoreCase)
                && pattern.SuccessCount > 0)
            .OrderByDescending(pattern => pattern.UpdatedAt)
            .Take(2)
            .ToArray();

        if (matches.Length == 0)
            return "";

        return string.Join("\n", matches.Select(pattern =>
            $"- {pattern.Domain} / {pattern.TaskType}: {pattern.StepSummary} Safety: {pattern.SafetyLevel}. Notes: {pattern.Notes}"));
    }

    public void RecordSuccess(BrowserAutomationRequest request, BrowserAutomationResult result)
    {
        if (!result.Success)
            return;

        var domain = Clean(request.TargetSite, 80);
        var taskType = Clean(request.TaskType, 80);
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(taskType))
            return;

        lock (_lock)
        {
            var patterns = LoadMutable();
            var existing = patterns.FirstOrDefault(pattern =>
                string.Equals(pattern.Domain, domain, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pattern.TaskType, taskType, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                existing = new BrowserAutomationPattern
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Domain = domain,
                    TaskType = taskType,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                patterns.Add(existing);
            }

            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.SuccessCount++;
            existing.LastRuntimeMs = result.RuntimeMs;
            existing.AverageRuntimeMs = existing.AverageRuntimeMs <= 0
                ? result.RuntimeMs
                : (int)Math.Round((existing.AverageRuntimeMs * 0.75) + (result.RuntimeMs * 0.25));
            existing.SafetyLevel = request.ActionLevel.ToString();
            existing.StepSummary = BuildStepSummary(request.TaskType);
            existing.Notes = Clean(result.ReceiptSummary.Length > 0 ? result.ReceiptSummary : result.Message, 400);

            _patterns = patterns
                .OrderByDescending(pattern => pattern.UpdatedAt)
                .Take(MaxPatterns)
                .ToList();
            Save();
        }
    }

    public BrowserAutomationPattern[] Snapshot() => Load().ToArray();

    private List<BrowserAutomationPattern> Load()
    {
        lock (_lock)
            return LoadMutable().Select(pattern => pattern.Clone()).ToList();
    }

    private List<BrowserAutomationPattern> LoadMutable()
    {
        if (_patterns != null)
            return _patterns;

        try
        {
            if (!File.Exists(_path))
                return _patterns = new List<BrowserAutomationPattern>();
            _patterns = JsonSerializer.Deserialize<List<BrowserAutomationPattern>>(
                File.ReadAllText(_path),
                JsonOptions()) ?? new List<BrowserAutomationPattern>();
        }
        catch
        {
            _patterns = new List<BrowserAutomationPattern>();
        }

        return _patterns;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? SettingsService.AppDataDir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_patterns, JsonOptions()));
    }

    private static string BuildStepSummary(string taskType) => taskType switch
    {
        "youtube_oldest_video" => "Search/open channel; open Videos; find oldest/sort path; open correct video; verify watch page.",
        "youtube_recent_video" => "Search/open channel; choose latest visible video; verify watch page.",
        "ecommerce_add_to_cart" => "Search product; choose relevant result; add to cart after upfront approval; repeat; stop at cart.",
        "product_cta_inspection" => "Open product page; inspect visible and icon-only CTAs; verify with receipt.",
        "embedded_video_traversal" => "Open first relevant page; find embedded or linked YouTube video; open video; verify watch page.",
        _ => "Use visible controls; verify final page; stop after success.",
    };

    private static string Clean(string value, int max)
    {
        var text = string.Join(" ", (value ?? "")
            .ReplaceLineEndings(" ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= max ? text : text[..max].Trim();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class BrowserAutomationPattern
{
    public string Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string StepSummary { get; set; } = "";
    public string SafetyLevel { get; set; } = "";
    public string Notes { get; set; } = "";
    public int SuccessCount { get; set; }
    public int LastRuntimeMs { get; set; }
    public int AverageRuntimeMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public BrowserAutomationPattern Clone() => (BrowserAutomationPattern)MemberwiseClone();
}
