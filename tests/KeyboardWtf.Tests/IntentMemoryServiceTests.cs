namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class IntentMemoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-intent-memory-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplicitMemoryCanBeUpdatedReadBackAndDeletedDurably()
    {
        var path = Path.Combine(_root, "intent-memory.json");
        var memory = new IntentMemoryService(path);
        memory.Load();

        var created = memory.Remember("preferred editor", "Notepad");
        Assert.Equal("Notepad", memory.ReadPersisted(created.Id)?.Value);
        var updated = memory.Remember("preferred editor", "Visual Studio Code");
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(
            "Visual Studio Code",
            memory.ReadPersisted("preferred editor")?.Value);

        var freshConversation = new IntentMemoryService(path);
        freshConversation.Load();
        Assert.Equal(
            "Visual Studio Code",
            freshConversation.Search("preferred editor").Single().Value);
        Assert.True(freshConversation.Forget("preferred editor"));
        Assert.Null(freshConversation.ReadPersisted("preferred editor"));
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
