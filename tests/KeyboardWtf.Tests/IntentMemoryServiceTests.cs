namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class IntentMemoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-memory-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public IntentMemoryServiceTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "intent-memory.json");
    }

    [Fact]
    public void StructuredMemoryCanBeCreatedListedEditedAndDeleted()
    {
        var service = new IntentMemoryService(_path);
        service.Load();
        var created = service.RememberStructured(new IntentMemoryDraft
        {
            Type = IntentMemoryType.Correction,
            SourcePhrase = "No, use Excel next time",
            TriggerPhrase = "monthly spreadsheet",
            Summary = "Open Excel instead of Microsoft To Do.",
            Action = "open_app",
            Target = "Excel",
            Parameters = new() { ["failed_target"] = "Microsoft To Do" },
            Confidence = 0.95,
        });

        var listed = service.Search("spreadsheet", 5, IntentMemoryType.Correction);
        var updated = service.Update(created.Id, new IntentMemoryUpdate
        {
            Type = IntentMemoryType.FailedAttemptFix,
            Summary = "Open Excel for the monthly spreadsheet.",
            Confidence = 1,
        });

        Assert.Single(listed);
        Assert.Equal(IntentMemoryType.FailedAttemptFix, updated.Type);
        Assert.Equal("Excel", updated.ActionData.Target);
        Assert.True(service.Forget(created.Id));
        Assert.Empty(service.Snapshot().Entries);
    }

    [Fact]
    public void SimilarRoutineTriggersAreReportedAsAmbiguous()
    {
        var service = new IntentMemoryService(_path);
        service.Load();
        RememberRoutine(service, "initialize my setup", "personal setup");
        RememberRoutine(service, "initialize our setup", "shared setup");

        var result = service.ResolveTrigger(
            "initialize setup",
            [IntentMemoryType.Routine]);

        Assert.Equal(IntentMemoryResolutionStatus.Ambiguous, result.Status);
        Assert.True(result.Candidates.Count >= 2);
    }

    [Fact]
    public void ExactRoutineTriggerWinsAndUsageIsTracked()
    {
        var service = new IntentMemoryService(_path);
        service.Load();
        var created = RememberRoutine(service, "initialize my work setup", "work setup");

        var result = service.ResolveTrigger(
            "initialize my work setup",
            [IntentMemoryType.Routine]);
        var used = service.MarkUsed(result.Best.Id);

        Assert.Equal(IntentMemoryResolutionStatus.Found, result.Status);
        Assert.Equal(created.Id, result.Best.Id);
        Assert.Equal(1, used.UsageCount);
        Assert.NotNull(used.LastUsedAt);
    }

    [Fact]
    public void LegacyKeyValueMemoryMigratesToStructuredPreference()
    {
        File.WriteAllText(
            _path,
            """[{"Id":"legacy","Key":"movie cinema","Value":"Prefer PVR Juhu","CreatedAt":"2026-01-01T00:00:00Z","UpdatedAt":"2026-01-01T00:00:00Z"}]""");
        var service = new IntentMemoryService(_path);

        service.Load();

        var entry = Assert.Single(service.Snapshot().Entries);
        Assert.Equal(IntentMemoryType.Preference, entry.Type);
        Assert.Equal("movie cinema", entry.TriggerPhrase);
        Assert.Equal("Prefer PVR Juhu", entry.Summary);
    }

    private static IntentMemoryEntry RememberRoutine(
        IntentMemoryService service,
        string trigger,
        string target)
    {
        return service.RememberStructured(new IntentMemoryDraft
        {
            Type = IntentMemoryType.Routine,
            SourcePhrase = $"Whenever I say {trigger}",
            TriggerPhrase = trigger,
            Summary = $"Run {target}.",
            Action = "run_workflow",
            Target = target,
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch { }
    }
}
