namespace KeyboardWtf.Services;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntentMemoryType
{
    Preference,
    Correction,
    AppAlias,
    FileAlias,
    FolderAlias,
    BrowserPreference,
    Workflow,
    Routine,
    TaskPattern,
    FailedAttemptFix,
}

public enum IntentMemoryResolutionStatus
{
    Found,
    Ambiguous,
    NotFound,
}

public sealed class IntentMemoryActionData
{
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class IntentMemoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public IntentMemoryType Type { get; set; } = IntentMemoryType.Preference;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string SourcePhrase { get; set; } = "";
    public string TriggerPhrase { get; set; } = "";
    public string Summary { get; set; } = "";
    public IntentMemoryActionData ActionData { get; set; } = new();
    public double Confidence { get; set; } = 1;
    public int UsageCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IntentMemoryDraft
{
    public IntentMemoryType Type { get; set; } = IntentMemoryType.Preference;
    public string SourcePhrase { get; set; } = "";
    public string TriggerPhrase { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double Confidence { get; set; } = 1;
}

public sealed class IntentMemoryUpdate
{
    public IntentMemoryType? Type { get; set; }
    public string SourcePhrase { get; set; }
    public string TriggerPhrase { get; set; }
    public string Summary { get; set; }
    public string Action { get; set; }
    public string Target { get; set; }
    public Dictionary<string, string> Parameters { get; set; }
    public double? Confidence { get; set; }
}

public sealed class IntentMemoryMatch
{
    public IntentMemoryEntry Entry { get; init; }
    public double Score { get; init; }
}

public sealed class IntentMemoryResolution
{
    public IntentMemoryResolutionStatus Status { get; init; }
    public IntentMemoryEntry Best { get; init; }
    public IReadOnlyList<IntentMemoryMatch> Candidates { get; init; } = Array.Empty<IntentMemoryMatch>();
}

public sealed class IntentMemorySnapshot
{
    public IReadOnlyList<IntentMemoryEntry> Entries { get; init; } = Array.Empty<IntentMemoryEntry>();
    public int Count { get; init; }
    public int MaxEntries { get; init; }
    public int MaxValueLength { get; init; }
}

public sealed class IntentMemoryService
{
    public const int MaxEntries = 200;
    public const int MaxKeyLength = 120;
    public const int MaxValueLength = 1000;
    public const int MaxParameterCount = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly List<IntentMemoryEntry> _entries = new();

    public IntentMemoryService(string path = null)
    {
        MemoryPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(SettingsService.AppDataDir, "intent-memory.json")
            : path;
    }

    public string MemoryPath { get; }

    public void Load()
    {
        lock (_lock)
        {
            _entries.Clear();
            if (!File.Exists(MemoryPath))
                return;

            try
            {
                var entries = JsonSerializer.Deserialize<List<IntentMemoryEntry>>(
                    File.ReadAllText(MemoryPath),
                    JsonOptions) ?? new();
                _entries.AddRange(entries
                    .Where(entry => entry != null)
                    .Select(NormalizeEntry)
                    .Where(IsUseful)
                    .OrderByDescending(entry => entry.UpdatedAt)
                    .Take(MaxEntries));
                SaveLocked();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Intent memory load failed; starting empty");
            }
        }
    }

    public IntentMemorySnapshot Snapshot(IntentMemoryType? type = null)
    {
        lock (_lock)
        {
            var entries = _entries
                .Where(entry => type == null || entry.Type == type)
                .OrderByDescending(entry => entry.UpdatedAt)
                .Select(Clone)
                .ToArray();
            return new IntentMemorySnapshot
            {
                Entries = entries,
                Count = entries.Length,
                MaxEntries = MaxEntries,
                MaxValueLength = MaxValueLength,
            };
        }
    }

    public IntentMemoryEntry Remember(string key, string value)
    {
        return RememberStructured(new IntentMemoryDraft
        {
            Type = IntentMemoryType.Preference,
            SourcePhrase = key,
            TriggerPhrase = key,
            Summary = value,
            Action = "remember",
            Target = value,
        });
    }

    public IntentMemoryEntry RememberStructured(IntentMemoryDraft draft)
    {
        if (draft == null)
            throw new ArgumentNullException(nameof(draft));

        var normalized = NormalizeDraft(draft);
        if (string.IsNullOrWhiteSpace(normalized.Summary)
            && string.IsNullOrWhiteSpace(normalized.ActionData.Target)
            && normalized.ActionData.Parameters.Count == 0)
        {
            throw new InvalidOperationException("Nothing useful was provided to remember.");
        }

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var trigger = FuzzyMatcher.Normalize(normalized.TriggerPhrase);
            var existing = trigger.Length == 0
                ? null
                : _entries.FirstOrDefault(entry =>
                    entry.Type == normalized.Type
                    && FuzzyMatcher.Normalize(entry.TriggerPhrase) == trigger);
            if (existing == null)
            {
                existing = normalized;
                existing.Id = Guid.NewGuid().ToString("N");
                existing.CreatedAt = now;
                _entries.Add(existing);
            }
            else
            {
                existing.SourcePhrase = normalized.SourcePhrase;
                existing.TriggerPhrase = normalized.TriggerPhrase;
                existing.Summary = normalized.Summary;
                existing.ActionData = normalized.ActionData;
                existing.Confidence = normalized.Confidence;
            }

            existing.UpdatedAt = now;
            SyncLegacyFields(existing);
            TrimLocked();
            SaveLocked();
            return Clone(existing);
        }
    }

    public IntentMemoryEntry Update(string idOrTrigger, IntentMemoryUpdate update)
    {
        var needle = CleanInline(idOrTrigger, MaxKeyLength);
        if (needle.Length == 0)
            throw new InvalidOperationException("A memory id or trigger is required.");
        if (update == null)
            throw new ArgumentNullException(nameof(update));

        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.TriggerPhrase, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Key, needle, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return null;

            if (update.Type != null)
                entry.Type = update.Type.Value;
            if (update.SourcePhrase != null)
                entry.SourcePhrase = CleanInline(update.SourcePhrase, MaxValueLength);
            if (update.TriggerPhrase != null)
                entry.TriggerPhrase = CleanInline(update.TriggerPhrase, MaxKeyLength);
            if (update.Summary != null)
                entry.Summary = CleanInline(update.Summary, MaxValueLength);
            if (update.Action != null)
                entry.ActionData.Action = CleanInline(update.Action, 80);
            if (update.Target != null)
                entry.ActionData.Target = CleanInline(update.Target, MaxValueLength);
            if (update.Parameters != null)
                entry.ActionData.Parameters = CleanParameters(update.Parameters);
            if (update.Confidence != null)
                entry.Confidence = Math.Clamp(update.Confidence.Value, 0, 1);

            entry = NormalizeEntry(entry);
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            SyncLegacyFields(entry);
            SaveLocked();
            return Clone(entry);
        }
    }

    public bool Forget(string idOrKey)
    {
        var needle = CleanInline(idOrKey, MaxKeyLength);
        if (needle.Length == 0)
            return false;

        lock (_lock)
        {
            var removed = _entries.RemoveAll(entry =>
                string.Equals(entry.Id, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Key, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.TriggerPhrase, needle, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                SaveLocked();
            return removed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            SaveLocked();
        }
    }

    public IReadOnlyList<IntentMemoryEntry> Search(
        string query,
        int maxResults = 5,
        IntentMemoryType? type = null)
    {
        return SearchRanked(query, maxResults, type)
            .Select(match => match.Entry)
            .ToArray();
    }

    public IReadOnlyList<IntentMemoryMatch> SearchRanked(
        string query,
        int maxResults = 5,
        IntentMemoryType? type = null)
    {
        var needle = CleanInline(query, MaxValueLength);
        lock (_lock)
        {
            return _entries
                .Where(entry => type == null || entry.Type == type)
                .Select(entry => new IntentMemoryMatch
                {
                    Entry = Clone(entry),
                    Score = Score(needle, entry),
                })
                .Where(match => needle.Length == 0 || match.Score >= 0.42)
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.Entry.UsageCount)
                .ThenByDescending(match => match.Entry.UpdatedAt)
                .Take(Math.Clamp(maxResults, 1, 20))
                .ToArray();
        }
    }

    public IntentMemoryResolution ResolveTrigger(
        string query,
        IReadOnlyCollection<IntentMemoryType> allowedTypes = null,
        int maxResults = 4)
    {
        var normalized = FuzzyMatcher.Normalize(query);
        lock (_lock)
        {
            var eligible = _entries
                .Where(entry => allowedTypes == null || allowedTypes.Contains(entry.Type))
                .ToArray();
            var exact = eligible
                .Where(entry => FuzzyMatcher.Normalize(entry.TriggerPhrase) == normalized)
                .OrderByDescending(entry => entry.Confidence)
                .ThenByDescending(entry => entry.UpdatedAt)
                .ToArray();
            if (exact.Length == 1)
            {
                return new IntentMemoryResolution
                {
                    Status = IntentMemoryResolutionStatus.Found,
                    Best = Clone(exact[0]),
                    Candidates = [new IntentMemoryMatch { Entry = Clone(exact[0]), Score = 1 }],
                };
            }
            if (exact.Length > 1)
            {
                return new IntentMemoryResolution
                {
                    Status = IntentMemoryResolutionStatus.Ambiguous,
                    Candidates = exact.Take(maxResults)
                        .Select(entry => new IntentMemoryMatch { Entry = Clone(entry), Score = 1 })
                        .ToArray(),
                };
            }

            var ranked = eligible
                .Select(entry => new IntentMemoryMatch
                {
                    Entry = Clone(entry),
                    Score = Math.Max(
                        FuzzyMatcher.Score(query, entry.TriggerPhrase),
                        FuzzyMatcher.Score(query, entry.SourcePhrase)),
                })
                .Where(match => match.Score >= 0.62)
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.Entry.UsageCount)
                .ThenByDescending(match => match.Entry.UpdatedAt)
                .Take(Math.Clamp(maxResults, 1, 10))
                .ToArray();
            if (ranked.Length == 0)
                return new IntentMemoryResolution { Status = IntentMemoryResolutionStatus.NotFound };

            var top = ranked[0];
            var secondScore = ranked.Length > 1 ? ranked[1].Score : 0;
            var highConfidence = top.Score >= 0.86
                && (ranked.Length == 1 || top.Score - secondScore >= 0.08);
            return new IntentMemoryResolution
            {
                Status = highConfidence
                    ? IntentMemoryResolutionStatus.Found
                    : IntentMemoryResolutionStatus.Ambiguous,
                Best = highConfidence ? top.Entry : null,
                Candidates = ranked,
            };
        }
    }

    public IntentMemoryEntry MarkUsed(string id)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return null;
            entry.UsageCount++;
            entry.LastUsedAt = DateTimeOffset.UtcNow;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            SaveLocked();
            return Clone(entry);
        }
    }

    public string BuildPromptDigest(int maxEntries = 12, int maxChars = 1800)
    {
        var lines = SearchRanked("", maxEntries)
            .Select(match =>
            {
                var entry = match.Entry;
                var trigger = string.IsNullOrWhiteSpace(entry.TriggerPhrase)
                    ? entry.SourcePhrase
                    : entry.TriggerPhrase;
                var action = string.IsNullOrWhiteSpace(entry.ActionData.Action)
                    ? entry.Summary
                    : $"{entry.ActionData.Action} {entry.ActionData.Target}".Trim();
                return $"- [{entry.Type}] {trigger}: {action}";
            })
            .ToList();
        if (lines.Count == 0)
            return "No saved intent memory.";

        var text = string.Join(Environment.NewLine, lines);
        return text.Length <= maxChars ? text : text[..maxChars] + "...";
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(MemoryPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var ordered = _entries.OrderByDescending(entry => entry.UpdatedAt).ToArray();
        var temporaryPath = MemoryPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(ordered, JsonOptions));
        File.Move(temporaryPath, MemoryPath, overwrite: true);
    }

    private void TrimLocked()
    {
        while (_entries.Count > MaxEntries)
        {
            var removable = _entries
                .OrderBy(entry => entry.UsageCount)
                .ThenBy(entry => entry.LastUsedAt ?? DateTimeOffset.MinValue)
                .ThenBy(entry => entry.UpdatedAt)
                .First();
            _entries.Remove(removable);
        }
    }

    private static IntentMemoryEntry NormalizeDraft(IntentMemoryDraft draft)
    {
        var source = CleanInline(draft.SourcePhrase, MaxValueLength);
        var trigger = CleanInline(draft.TriggerPhrase, MaxKeyLength);
        if (trigger.Length == 0)
            trigger = CleanInline(source, MaxKeyLength);
        return NormalizeEntry(new IntentMemoryEntry
        {
            Type = draft.Type,
            SourcePhrase = source,
            TriggerPhrase = trigger,
            Summary = CleanInline(draft.Summary, MaxValueLength),
            ActionData = new IntentMemoryActionData
            {
                Action = CleanInline(draft.Action, 80),
                Target = CleanInline(draft.Target, MaxValueLength),
                Parameters = CleanParameters(draft.Parameters),
            },
            Confidence = Math.Clamp(draft.Confidence, 0, 1),
        });
    }

    private static IntentMemoryEntry NormalizeEntry(IntentMemoryEntry entry)
    {
        entry.Id = string.IsNullOrWhiteSpace(entry.Id)
            ? Guid.NewGuid().ToString("N")
            : entry.Id.Trim();
        entry.ActionData ??= new IntentMemoryActionData();
        entry.ActionData.Parameters = CleanParameters(entry.ActionData.Parameters);

        if (string.IsNullOrWhiteSpace(entry.SourcePhrase))
            entry.SourcePhrase = entry.Key;
        if (string.IsNullOrWhiteSpace(entry.TriggerPhrase))
            entry.TriggerPhrase = entry.Key;
        if (string.IsNullOrWhiteSpace(entry.Summary))
            entry.Summary = entry.Value;
        if (string.IsNullOrWhiteSpace(entry.ActionData.Target))
            entry.ActionData.Target = entry.Value;

        entry.SourcePhrase = CleanInline(entry.SourcePhrase, MaxValueLength);
        entry.TriggerPhrase = CleanInline(entry.TriggerPhrase, MaxKeyLength);
        entry.Summary = CleanInline(entry.Summary, MaxValueLength);
        entry.ActionData.Action = CleanInline(entry.ActionData.Action, 80);
        entry.ActionData.Target = CleanInline(entry.ActionData.Target, MaxValueLength);
        entry.Confidence = Math.Clamp(entry.Confidence, 0, 1);
        entry.UsageCount = Math.Max(0, entry.UsageCount);
        if (entry.CreatedAt == default)
            entry.CreatedAt = DateTimeOffset.UtcNow;
        if (entry.UpdatedAt == default)
            entry.UpdatedAt = entry.CreatedAt;
        SyncLegacyFields(entry);
        return entry;
    }

    private static void SyncLegacyFields(IntentMemoryEntry entry)
    {
        entry.Key = CleanInline(
            string.IsNullOrWhiteSpace(entry.TriggerPhrase) ? entry.SourcePhrase : entry.TriggerPhrase,
            MaxKeyLength);
        entry.Value = CleanInline(
            string.IsNullOrWhiteSpace(entry.Summary) ? entry.ActionData.Target : entry.Summary,
            MaxValueLength);
    }

    private static bool IsUseful(IntentMemoryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.TriggerPhrase)
        || !string.IsNullOrWhiteSpace(entry.Summary)
        || !string.IsNullOrWhiteSpace(entry.ActionData.Target)
        || entry.ActionData.Parameters.Count > 0;

    private static double Score(string query, IntentMemoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 1;
        var scores = new List<double>
        {
            FuzzyMatcher.Score(query, entry.TriggerPhrase),
            FuzzyMatcher.Score(query, entry.SourcePhrase),
            FuzzyMatcher.Score(query, entry.Summary),
            FuzzyMatcher.Score(query, entry.ActionData.Target),
        };
        scores.AddRange(entry.ActionData.Parameters.Select(pair =>
            Math.Max(FuzzyMatcher.Score(query, pair.Key), FuzzyMatcher.Score(query, pair.Value))));
        var best = scores.Max();
        return Math.Min(1, best + Math.Min(0.04, entry.UsageCount * 0.005));
    }

    private static Dictionary<string, string> CleanParameters(Dictionary<string, string> parameters)
    {
        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parameters ?? new Dictionary<string, string>())
        {
            var key = CleanInline(pair.Key, 80);
            var value = CleanInline(pair.Value, MaxValueLength);
            if (key.Length == 0 || value.Length == 0)
                continue;
            cleaned[key] = value;
            if (cleaned.Count >= MaxParameterCount)
                break;
        }
        return cleaned;
    }

    private static string CleanInline(string value, int maxLength)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    private static IntentMemoryEntry Clone(IntentMemoryEntry entry) => new()
    {
        Id = entry.Id,
        Type = entry.Type,
        Key = entry.Key,
        Value = entry.Value,
        SourcePhrase = entry.SourcePhrase,
        TriggerPhrase = entry.TriggerPhrase,
        Summary = entry.Summary,
        ActionData = new IntentMemoryActionData
        {
            Action = entry.ActionData.Action,
            Target = entry.ActionData.Target,
            Parameters = new Dictionary<string, string>(
                entry.ActionData.Parameters,
                StringComparer.OrdinalIgnoreCase),
        },
        Confidence = entry.Confidence,
        UsageCount = entry.UsageCount,
        LastUsedAt = entry.LastUsedAt,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
    };
}
