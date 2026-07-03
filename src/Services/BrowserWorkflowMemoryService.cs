namespace KeyboardWtf.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using KeyboardWtf.Models;

public sealed class BrowserWorkflowMemoryService
{
    private const int MaxTotalWorkflows = 80;
    private const int MaxWorkflowsPerDomain = 8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();
    private List<BrowserWorkflowMemory> _workflows = new();

    public BrowserWorkflowMemoryService(string path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(SettingsService.AppDataDir, "browser-workflows.json")
            : path;
    }

    public IReadOnlyList<BrowserWorkflowMemory> Snapshot()
    {
        lock (_lock)
            return _workflows.Select(Clone).ToArray();
    }

    public void Load()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path))
            {
                _workflows = new List<BrowserWorkflowMemory>();
                return;
            }

            try
            {
                _workflows = JsonSerializer.Deserialize<List<BrowserWorkflowMemory>>(
                    File.ReadAllText(_path),
                    JsonOptions) ?? new List<BrowserWorkflowMemory>();
                var migrated = MigrateLocked();
                PruneLocked(save: migrated);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Browser workflow memory load failed; starting empty.");
                _workflows = new List<BrowserWorkflowMemory>();
            }
        }
    }

    public BrowserWorkflowMemory SaveSuccessfulWorkflow(
        string domain,
        string taskType,
        string taskPattern,
        string userIntent,
        string backend,
        IEnumerable<string> learnedSteps,
        IEnumerable<string> deterministicActions,
        IEnumerable<string> locatorHints,
        IEnumerable<string> safetyBoundaries,
        bool confirmationRequired,
        int runtimeMs)
    {
        var now = DateTimeOffset.UtcNow;
        var cleanDomain = CleanDomain(domain);
        var cleanType = Clean(taskType, 80);
        var cleanPattern = Clean(taskPattern, 240);
        var cleanIntent = Clean(userIntent, 240);
        var sanitizedSteps = SanitizeList(learnedSteps, 20, 180);
        var sanitizedActions = SanitizeList(deterministicActions, 20, 200);
        var sanitizedHints = SanitizeList(locatorHints, 16, 140);
        if (cleanType.Equals("youtube_play_oldest_video", StringComparison.OrdinalIgnoreCase))
        {
            sanitizedSteps = sanitizedSteps
                .Where(step => !System.Text.RegularExpressions.Regex.IsMatch(step, @"@\w+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .ToArray();
            sanitizedActions = StripUnsafeTemplateFinalUrls(sanitizedActions).ToArray();
            sanitizedHints = StripUnsafeTemplateFinalUrls(sanitizedHints).ToArray();
        }
        if (cleanType.Equals("ecommerce_add_to_cart", StringComparison.OrdinalIgnoreCase))
        {
            sanitizedActions = StripUnsafeTemplateFinalUrls(sanitizedActions).ToArray();
            sanitizedHints = StripUnsafeTemplateFinalUrls(sanitizedHints).ToArray();
        }
        var sanitizedBoundaries = SanitizeList(safetyBoundaries, 12, 140);

        lock (_lock)
        {
            var existing = _workflows
                .Where(workflow => string.Equals(workflow.Domain, cleanDomain, StringComparison.OrdinalIgnoreCase))
                .Where(workflow => !workflow.Quarantined)
                .Where(workflow => workflow.MemoryKind == BrowserWorkflowMemoryKind.ParameterizedWorkflowTemplate)
                .Where(workflow => string.IsNullOrWhiteSpace(cleanType)
                    || string.IsNullOrWhiteSpace(workflow.TaskType)
                    || string.Equals(workflow.TaskType, cleanType, StringComparison.OrdinalIgnoreCase))
                .Select(workflow => new
                {
                    Workflow = workflow,
                    Score = FuzzyMatcher.Score(
                        FuzzyMatcher.Normalize(workflow.TaskPattern + " " + string.Join(" ", workflow.UserIntentExamples)),
                        FuzzyMatcher.Normalize(cleanPattern + " " + cleanIntent)),
                })
                .OrderByDescending(item => item.Score)
                .FirstOrDefault(item => item.Score >= 0.74)?.Workflow;

            var workflow = existing ?? new BrowserWorkflowMemory
            {
                WorkflowId = Guid.NewGuid().ToString("N"),
                MemoryKind = BrowserWorkflowMemoryKind.ParameterizedWorkflowTemplate,
                CreatedAt = now,
                Domain = cleanDomain,
                TaskType = cleanType,
                TaskPattern = cleanPattern,
                HardStopConditions =
                [
                    "payment",
                    "checkout",
                    "place order",
                    "OTP/CAPTCHA bypass",
                    "final submit without review",
                    "delete/send/publish without confirmation",
                ],
            };
            workflow.UpdatedAt = now;
            workflow.MemoryKind = workflow.MemoryKind is BrowserWorkflowMemoryKind.LegacyQuarantined or BrowserWorkflowMemoryKind.FailureHint
                ? BrowserWorkflowMemoryKind.ParameterizedWorkflowTemplate
                : workflow.MemoryKind;
            workflow.Quarantined = false;
            workflow.QuarantineReason = "";
            workflow.StaleAfter = now.AddDays(45);
            workflow.TaskType = cleanType.Length > 0 ? cleanType : workflow.TaskType;
            workflow.TaskPattern = cleanPattern.Length > 0 ? cleanPattern : workflow.TaskPattern;
            workflow.BackendThatDiscoveredIt = Clean(backend, 120);
            AddUnique(workflow.UserIntentExamples, cleanIntent, 8);
            workflow.LearnedSteps = MergeLists(workflow.LearnedSteps, sanitizedSteps, 20);
            workflow.DeterministicActions = MergeLists(workflow.DeterministicActions, sanitizedActions, 20);
            workflow.SelectorsOrLocatorHints = MergeLists(workflow.SelectorsOrLocatorHints, sanitizedHints, 16);
            workflow.SafetyBoundaries = MergeLists(workflow.SafetyBoundaries, sanitizedBoundaries, 12);
            workflow.ConfirmationRequired = workflow.ConfirmationRequired || confirmationRequired;
            workflow.LastRuntimeMs = Math.Max(0, runtimeMs);
            workflow.AverageRuntimeMs = workflow.SuccessCount <= 0
                ? workflow.LastRuntimeMs
                : (int)Math.Round(((workflow.AverageRuntimeMs * workflow.SuccessCount) + workflow.LastRuntimeMs) / (double)(workflow.SuccessCount + 1));
            workflow.SuccessCount++;
            workflow.LastSuccessAt = now;
            workflow.Confidence = Math.Clamp(workflow.Confidence + 0.18, 0.72, 0.96);
            if (!_workflows.Any(item => string.Equals(item.WorkflowId, workflow.WorkflowId, StringComparison.OrdinalIgnoreCase)))
                _workflows.Add(workflow);

            PruneLocked(save: true);
            return Clone(workflow);
        }
    }

    public BrowserWorkflowMemory SaveExactSuccessfulInstance(
        string domain,
        string taskType,
        string taskPattern,
        string userIntent,
        string entityKey,
        string finalUrl,
        string backend,
        IEnumerable<string> learnedSteps,
        IEnumerable<string> deterministicActions,
        IEnumerable<string> locatorHints,
        IEnumerable<string> safetyBoundaries,
        bool confirmationRequired,
        int runtimeMs)
    {
        var now = DateTimeOffset.UtcNow;
        var cleanDomain = CleanDomain(domain);
        var cleanType = Clean(taskType, 80);
        var cleanPattern = Clean(taskPattern, 240);
        var cleanIntent = Clean(userIntent, 240);
        var cleanEntity = NormalizeEntityKey(entityKey);
        var cleanUrl = BrowserAgentSafetyPolicy.IsSafeHttpUrl(finalUrl, out var uri)
            ? uri.ToString()
            : "";
        if (string.IsNullOrWhiteSpace(cleanDomain)
            || string.IsNullOrWhiteSpace(cleanType)
            || string.IsNullOrWhiteSpace(cleanEntity)
            || string.IsNullOrWhiteSpace(cleanUrl))
        {
            throw new InvalidOperationException("Exact browser workflow instances require domain, task type, entity/query, and a safe final URL.");
        }

        lock (_lock)
        {
            var workflow = _workflows.FirstOrDefault(item =>
                !item.Quarantined
                && item.MemoryKind == BrowserWorkflowMemoryKind.ExactSuccessfulInstance
                && string.Equals(item.Domain, cleanDomain, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.TaskType, cleanType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.EntityKey, cleanEntity, StringComparison.OrdinalIgnoreCase));
            if (workflow == null)
            {
                workflow = new BrowserWorkflowMemory
                {
                    WorkflowId = Guid.NewGuid().ToString("N"),
                    MemoryKind = BrowserWorkflowMemoryKind.ExactSuccessfulInstance,
                    CreatedAt = now,
                    Domain = cleanDomain,
                    TaskType = cleanType,
                    TaskPattern = cleanPattern,
                    EntityKey = cleanEntity,
                    HardStopConditions =
                    [
                        "payment",
                        "checkout",
                        "place order",
                        "OTP/CAPTCHA bypass",
                        "final submit without review",
                        "delete/send/publish without confirmation",
                    ],
                };
                _workflows.Add(workflow);
            }

            workflow.UpdatedAt = now;
            workflow.Quarantined = false;
            workflow.QuarantineReason = "";
            workflow.FinalUrl = cleanUrl;
            workflow.TaskPattern = cleanPattern.Length > 0 ? cleanPattern : workflow.TaskPattern;
            workflow.BackendThatDiscoveredIt = Clean(backend, 120);
            AddUnique(workflow.UserIntentExamples, cleanIntent, 8);
            AddUnique(workflow.ParameterKeys, cleanEntity, 8);
            workflow.LearnedSteps = MergeLists(workflow.LearnedSteps, SanitizeList(learnedSteps, 20, 180), 20);
            workflow.DeterministicActions = MergeLists(
                workflow.DeterministicActions,
                SanitizeList(deterministicActions, 20, 200).Concat([$"exact_entity:{cleanEntity}", $"navigate:{cleanUrl}"]),
                24);
            workflow.SelectorsOrLocatorHints = MergeLists(workflow.SelectorsOrLocatorHints, SanitizeList(locatorHints, 16, 140), 16);
            workflow.SafetyBoundaries = MergeLists(workflow.SafetyBoundaries, SanitizeList(safetyBoundaries, 12, 140), 12);
            workflow.ConfirmationRequired = workflow.ConfirmationRequired || confirmationRequired;
            workflow.LastRuntimeMs = Math.Max(0, runtimeMs);
            workflow.AverageRuntimeMs = workflow.SuccessCount <= 0
                ? workflow.LastRuntimeMs
                : (int)Math.Round(((workflow.AverageRuntimeMs * workflow.SuccessCount) + workflow.LastRuntimeMs) / (double)(workflow.SuccessCount + 1));
            workflow.SuccessCount++;
            workflow.LastSuccessAt = now;
            workflow.StaleAfter = now.AddDays(30);
            workflow.Confidence = Math.Clamp(workflow.Confidence + 0.2, 0.78, 0.98);
            PruneLocked(save: true);
            return Clone(workflow);
        }
    }

    public BrowserWorkflowMatch FindExactSuccessfulInstance(string domain, string taskType, string entityKey)
    {
        var cleanDomain = CleanDomain(domain);
        var cleanType = Clean(taskType, 80);
        var cleanEntity = NormalizeEntityKey(entityKey);
        if (string.IsNullOrWhiteSpace(cleanDomain)
            || string.IsNullOrWhiteSpace(cleanType)
            || string.IsNullOrWhiteSpace(cleanEntity))
        {
            return new BrowserWorkflowMatch();
        }

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var workflow = _workflows
                .Where(item => !item.Quarantined)
                .Where(item => item.MemoryKind == BrowserWorkflowMemoryKind.ExactSuccessfulInstance)
                .Where(item => item.StaleAfter > now)
                .Where(item => string.Equals(item.Domain, cleanDomain, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.Equals(item.TaskType, cleanType, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.Equals(item.EntityKey, cleanEntity, StringComparison.OrdinalIgnoreCase)
                    || item.ParameterKeys.Any(key => string.Equals(NormalizeEntityKey(key), cleanEntity, StringComparison.OrdinalIgnoreCase)))
                .Where(item => BrowserAgentSafetyPolicy.IsSafeHttpUrl(item.FinalUrl, out _))
                .OrderByDescending(item => item.Confidence)
                .ThenByDescending(item => item.LastSuccessAt ?? item.UpdatedAt)
                .FirstOrDefault();
            return workflow == null
                ? new BrowserWorkflowMatch()
                : new BrowserWorkflowMatch { Workflow = Clone(workflow), Score = 1.0 };
        }
    }

    public BrowserWorkflowMemory RecordFailureHint(
        string domain,
        string taskType,
        string entityKey,
        string command,
        string url,
        string reason,
        IEnumerable<string> pageSignals = null)
    {
        var now = DateTimeOffset.UtcNow;
        var cleanDomain = CleanDomain(domain);
        var cleanType = Clean(taskType, 80);
        var cleanEntity = NormalizeEntityKey(entityKey);
        if (string.IsNullOrWhiteSpace(cleanDomain) || string.IsNullOrWhiteSpace(cleanType))
            return null;

        lock (_lock)
        {
            var workflow = _workflows.FirstOrDefault(item =>
                item.MemoryKind == BrowserWorkflowMemoryKind.FailureHint
                && string.Equals(item.Domain, cleanDomain, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.TaskType, cleanType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.EntityKey, cleanEntity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.FailureUrl, Clean(url, 300), StringComparison.OrdinalIgnoreCase));
            if (workflow == null)
            {
                workflow = new BrowserWorkflowMemory
                {
                    WorkflowId = Guid.NewGuid().ToString("N"),
                    MemoryKind = BrowserWorkflowMemoryKind.FailureHint,
                    CreatedAt = now,
                    Domain = cleanDomain,
                    TaskType = cleanType,
                    EntityKey = cleanEntity,
                    Confidence = 0.28,
                };
                _workflows.Add(workflow);
            }

            workflow.UpdatedAt = now;
            workflow.TaskPattern = Clean(command, 240);
            workflow.FailureReason = Clean(reason, 240);
            workflow.FailureUrl = Clean(url, 300);
            workflow.FailurePageSignals = SanitizeList(pageSignals, 10, 120).ToList();
            workflow.FailureCount++;
            workflow.LastFailureAt = now;
            workflow.FailureExpiresAt = now.AddHours(6);
            workflow.StaleAfter = now.AddHours(6);
            PruneLocked(save: true);
            return Clone(workflow);
        }
    }

    public BrowserWorkflowMatch FindBestMatch(string domain, string task, string taskType = "")
    {
        var cleanDomain = CleanDomain(domain);
        var cleanType = Clean(taskType, 80);
        var normalizedTask = FuzzyMatcher.Normalize(task);
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var best = _workflows
                .Where(workflow => string.Equals(workflow.Domain, cleanDomain, StringComparison.OrdinalIgnoreCase))
                .Where(workflow => !workflow.Quarantined)
                .Where(workflow => workflow.MemoryKind == BrowserWorkflowMemoryKind.ParameterizedWorkflowTemplate)
                .Where(workflow => string.IsNullOrWhiteSpace(cleanType)
                    || string.Equals(workflow.TaskType, cleanType, StringComparison.OrdinalIgnoreCase))
                .Where(workflow => workflow.Confidence >= 0.35)
                .Where(workflow => workflow.StaleAfter > now)
                .Select(workflow =>
                {
                    var text = FuzzyMatcher.Normalize(workflow.TaskPattern + " " + string.Join(" ", workflow.UserIntentExamples));
                    var fuzzy = FuzzyMatcher.Score(text, normalizedTask);
                    var overlap = TokenOverlapScore(normalizedTask, text);
                    var score = Math.Max(fuzzy, overlap);
                    if (IsReusableParameterizedTaskType(cleanType)
                        && string.Equals(workflow.TaskType, cleanType, StringComparison.OrdinalIgnoreCase))
                    {
                        score = Math.Max(score, 0.82);
                    }
                    return new BrowserWorkflowMatch
                    {
                        Workflow = Clone(workflow),
                        Score = score,
                    };
                })
                .OrderByDescending(match => match.Score * Math.Max(0.05, match.Workflow.Confidence))
                .FirstOrDefault();
            return best?.Score >= 0.52 ? best : new BrowserWorkflowMatch();
        }
    }

    private static bool IsReusableParameterizedTaskType(string taskType) =>
        taskType.Equals("youtube_play_oldest_video", StringComparison.OrdinalIgnoreCase)
        || taskType.Equals("ecommerce_add_to_cart", StringComparison.OrdinalIgnoreCase);

    public void RecordFailure(string workflowId)
    {
        lock (_lock)
        {
            var workflow = _workflows.FirstOrDefault(item =>
                string.Equals(item.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase));
            if (workflow == null)
                return;
            workflow.FailureCount++;
            workflow.LastFailureAt = DateTimeOffset.UtcNow;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            workflow.Confidence = Math.Clamp(workflow.Confidence - 0.22, 0, 1);
            PruneLocked(save: true);
        }
    }

    public void RecordRepairSuccess(
        string workflowId,
        IEnumerable<string> repairedSteps,
        int runtimeMs,
        IEnumerable<string> deterministicActions = null,
        IEnumerable<string> locatorHints = null)
    {
        lock (_lock)
        {
            var workflow = _workflows.FirstOrDefault(item =>
                string.Equals(item.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase));
            if (workflow == null)
                return;
            workflow.LearnedSteps = MergeLists(workflow.LearnedSteps, SanitizeList(repairedSteps, 20, 180), 20);
            workflow.DeterministicActions = MergeLists(
                workflow.DeterministicActions,
                SanitizeList(deterministicActions, 20, 200),
                20);
            workflow.SelectorsOrLocatorHints = MergeLists(
                workflow.SelectorsOrLocatorHints,
                SanitizeList(locatorHints, 16, 140),
                16);
            workflow.LastRuntimeMs = Math.Max(0, runtimeMs);
            workflow.AverageRuntimeMs = workflow.SuccessCount <= 0
                ? workflow.LastRuntimeMs
                : (int)Math.Round(((workflow.AverageRuntimeMs * workflow.SuccessCount) + workflow.LastRuntimeMs) / (double)(workflow.SuccessCount + 1));
            workflow.SuccessCount++;
            workflow.LastSuccessAt = DateTimeOffset.UtcNow;
            workflow.UpdatedAt = DateTimeOffset.UtcNow;
            workflow.StaleAfter = DateTimeOffset.UtcNow.AddDays(45);
            workflow.Confidence = Math.Clamp(workflow.Confidence + 0.24, 0.72, 0.96);
            SaveLocked();
        }
    }

    public void Prune()
    {
        lock (_lock)
            PruneLocked(save: true);
    }

    private bool MigrateLocked()
    {
        var changed = false;
        foreach (var workflow in _workflows)
        {
            workflow.MemoryKind = workflow.MemoryKind switch
            {
                BrowserWorkflowMemoryKind.LegacyQuarantined => BrowserWorkflowMemoryKind.LegacyQuarantined,
                BrowserWorkflowMemoryKind.FailureHint => BrowserWorkflowMemoryKind.FailureHint,
                BrowserWorkflowMemoryKind.ExactSuccessfulInstance => BrowserWorkflowMemoryKind.ExactSuccessfulInstance,
                _ => BrowserWorkflowMemoryKind.ParameterizedWorkflowTemplate,
            };

            var reason = UnsafeLegacyReason(workflow);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                if (!workflow.Quarantined || workflow.QuarantineReason != reason)
                {
                    workflow.Quarantined = true;
                    workflow.QuarantineReason = reason;
                    workflow.MemoryKind = BrowserWorkflowMemoryKind.LegacyQuarantined;
                    workflow.Confidence = Math.Min(workflow.Confidence, 0.05);
                    workflow.UpdatedAt = DateTimeOffset.UtcNow;
                    changed = true;
                }
                continue;
            }

            if (workflow.MemoryKind == BrowserWorkflowMemoryKind.ParameterizedWorkflowTemplate)
            {
                var beforeSteps = workflow.LearnedSteps.Count;
                var beforeActions = workflow.DeterministicActions.Count;
                var beforeHints = workflow.SelectorsOrLocatorHints.Count;
                if (workflow.TaskType.Equals("youtube_play_oldest_video", StringComparison.OrdinalIgnoreCase))
                {
                    workflow.LearnedSteps = workflow.LearnedSteps
                        .Where(step => !System.Text.RegularExpressions.Regex.IsMatch(step, @"@\w+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        .ToList();
                }
                workflow.DeterministicActions = StripUnsafeTemplateFinalUrls(workflow.DeterministicActions).ToList();
                workflow.SelectorsOrLocatorHints = StripUnsafeTemplateFinalUrls(workflow.SelectorsOrLocatorHints).ToList();
                changed = changed
                    || beforeSteps != workflow.LearnedSteps.Count
                    || beforeActions != workflow.DeterministicActions.Count
                    || beforeHints != workflow.SelectorsOrLocatorHints.Count;
            }
        }
        return changed;
    }

    private static string UnsafeLegacyReason(BrowserWorkflowMemory workflow)
    {
        if (workflow == null)
            return "missing workflow";
        var domain = workflow.Domain ?? "";
        var taskType = workflow.TaskType ?? "";
        var joined = string.Join(
            " ",
            workflow.UserIntentExamples
                .Concat(workflow.LearnedSteps)
                .Concat(workflow.DeterministicActions)
                .Concat(workflow.SelectorsOrLocatorHints));

        if ((domain.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || domain.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            && workflow.SuccessCount > 0)
        {
            return "local/mock workflow memory is not allowed to drive real browser automation";
        }
        if (taskType.Equals("youtube_play_oldest_video", StringComparison.OrdinalIgnoreCase)
            && joined.Contains("ecommerce", StringComparison.OrdinalIgnoreCase))
        {
            return "workflow task type conflicts with ecommerce actions";
        }
        if (taskType.Equals("ecommerce_add_to_cart", StringComparison.OrdinalIgnoreCase)
            && joined.Contains("youtube_", StringComparison.OrdinalIgnoreCase))
        {
            return "workflow task type conflicts with YouTube actions";
        }
        if (workflow.MemoryKind == BrowserWorkflowMemoryKind.ExactSuccessfulInstance
            && string.IsNullOrWhiteSpace(workflow.EntityKey))
        {
            return "exact replay memory is missing entity/query key";
        }
        return "";
    }

    private static IEnumerable<string> StripUnsafeTemplateFinalUrls(IEnumerable<string> values)
    {
        foreach (var value in values ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (value.StartsWith("watch_url:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.StartsWith("navigate:", StringComparison.OrdinalIgnoreCase)
                && value.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.StartsWith("youtube_watch_url:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.StartsWith("youtube_channel:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.Equals("youtube_replay_saved_watch_url", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.StartsWith("ecommerce_product_url:", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return value;
        }
    }

    private void PruneLocked(bool save)
    {
        var now = DateTimeOffset.UtcNow;
        _workflows = _workflows
            .Where(workflow => !workflow.Quarantined || workflow.MemoryKind == BrowserWorkflowMemoryKind.LegacyQuarantined)
            .Where(workflow => workflow.MemoryKind != BrowserWorkflowMemoryKind.FailureHint
                || workflow.FailureExpiresAt == null
                || workflow.FailureExpiresAt > now)
            .Where(workflow => workflow.MemoryKind == BrowserWorkflowMemoryKind.LegacyQuarantined
                || workflow.MemoryKind == BrowserWorkflowMemoryKind.FailureHint
                || workflow.Confidence >= 0.12
                || workflow.SuccessCount > workflow.FailureCount)
            .Where(workflow => workflow.MemoryKind == BrowserWorkflowMemoryKind.LegacyQuarantined
                || workflow.MemoryKind == BrowserWorkflowMemoryKind.FailureHint
                || workflow.StaleAfter > now
                || workflow.SuccessCount >= 3)
            .GroupBy(workflow => workflow.Domain, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group
                .OrderByDescending(workflow => workflow.Confidence)
                .ThenByDescending(workflow => workflow.LastSuccessAt ?? workflow.UpdatedAt)
                .Take(MaxWorkflowsPerDomain))
            .OrderByDescending(workflow => workflow.Confidence)
            .ThenByDescending(workflow => workflow.LastSuccessAt ?? workflow.UpdatedAt)
            .Take(MaxTotalWorkflows)
            .ToList();
        if (save)
            SaveLocked();
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_workflows, JsonOptions));
    }

    private static string CleanDomain(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            value = uri.Host;
        return Clean(value, 100).ToLowerInvariant();
    }

    private static string NormalizeEntityKey(string value) =>
        FuzzyMatcher.Normalize(value)
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);

    private static IReadOnlyList<string> SanitizeList(IEnumerable<string> values, int maxItems, int maxLength) =>
        (values ?? Array.Empty<string>())
            .Select(value => Clean(value, maxLength))
            .Where(value => value.Length > 0)
            .Where(value => !LooksSensitive(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToArray();

    private static List<string> MergeLists(IEnumerable<string> existing, IEnumerable<string> incoming, int maxItems) =>
        (existing ?? Array.Empty<string>())
            .Concat(incoming ?? Array.Empty<string>())
            .Select(value => Clean(value, 220))
            .Where(value => value.Length > 0)
            .Where(value => !LooksSensitive(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToList();

    private static void AddUnique(List<string> list, string value, int maxItems)
    {
        if (string.IsNullOrWhiteSpace(value) || LooksSensitive(value))
            return;
        if (!list.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, value);
        while (list.Count > maxItems)
            list.RemoveAt(list.Count - 1);
    }

    private static bool LooksSensitive(string value)
    {
        var normalized = FuzzyMatcher.Normalize(value);
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("otp", StringComparison.Ordinal)
            || normalized.Contains("captcha", StringComparison.Ordinal)
            || normalized.Contains("cvv", StringComparison.Ordinal)
            || normalized.Contains("card number", StringComparison.Ordinal)
            || normalized.Contains("api key", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(value, @"AIza[0-9A-Za-z_\-]{20,}")
            || System.Text.RegularExpressions.Regex.IsMatch(value, @"\b\d{12,19}\b");
    }

    private static double TokenOverlapScore(string query, string candidate)
    {
        var queryTokens = Tokenize(query).ToArray();
        if (queryTokens.Length == 0)
            return 0;
        var candidateTokens = Tokenize(candidate).ToHashSet(StringComparer.Ordinal);
        if (candidateTokens.Count == 0)
            return 0;
        return queryTokens.Count(candidateTokens.Contains) / (double)queryTokens.Length;
    }

    private static IEnumerable<string> Tokenize(string value) =>
        FuzzyMatcher.Normalize((value ?? "")
                .Replace("_", " ", StringComparison.Ordinal)
                .Replace("-", " ", StringComparison.Ordinal)
                .Replace(".", " ", StringComparison.Ordinal))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .Where(token => token is not "the" and not "with" and not "for" and not "and" and not "from");

    private static string Clean(string value, int maxLength)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    private static BrowserWorkflowMemory Clone(BrowserWorkflowMemory workflow) => workflow == null
        ? null
        : new BrowserWorkflowMemory
        {
            WorkflowId = workflow.WorkflowId,
            MemoryKind = workflow.MemoryKind,
            Domain = workflow.Domain,
            TaskType = workflow.TaskType,
            TaskPattern = workflow.TaskPattern,
            EntityKey = workflow.EntityKey,
            ParameterKeys = workflow.ParameterKeys.ToList(),
            FinalUrl = workflow.FinalUrl,
            UserIntentExamples = workflow.UserIntentExamples.ToList(),
            BackendThatDiscoveredIt = workflow.BackendThatDiscoveredIt,
            LearnedSteps = workflow.LearnedSteps.ToList(),
            DeterministicActions = workflow.DeterministicActions.ToList(),
            SelectorsOrLocatorHints = workflow.SelectorsOrLocatorHints.ToList(),
            SemanticFallbackDescriptions = workflow.SemanticFallbackDescriptions.ToList(),
            RequiredContext = workflow.RequiredContext.ToList(),
            SafetyBoundaries = workflow.SafetyBoundaries.ToList(),
            ConfirmationRequired = workflow.ConfirmationRequired,
            HardStopConditions = workflow.HardStopConditions.ToList(),
            PageSignature = workflow.PageSignature,
            AverageRuntimeMs = workflow.AverageRuntimeMs,
            LastRuntimeMs = workflow.LastRuntimeMs,
            SuccessCount = workflow.SuccessCount,
            FailureCount = workflow.FailureCount,
            LastSuccessAt = workflow.LastSuccessAt,
            LastFailureAt = workflow.LastFailureAt,
            FailureReason = workflow.FailureReason,
            FailureUrl = workflow.FailureUrl,
            FailurePageSignals = workflow.FailurePageSignals.ToList(),
            FailureExpiresAt = workflow.FailureExpiresAt,
            Quarantined = workflow.Quarantined,
            QuarantineReason = workflow.QuarantineReason,
            Confidence = workflow.Confidence,
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt,
            StaleAfter = workflow.StaleAfter,
        };
}
