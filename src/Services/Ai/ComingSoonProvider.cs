namespace KeyboardWtf.Services.Ai;

using System;
using System.Threading.Tasks;
using KeyboardWtf.Models;

/// <summary>
/// Placeholder for providers that aren't built yet. Declares itself
/// <see cref="IsImplemented"/> = <c>false</c> so the UI can badge it "Coming soon"
/// and block the test button. Any attempt to actually reformat throws.
/// </summary>
internal sealed class ComingSoonProvider : IAiProvider
{
    public ComingSoonProvider(AiProvider id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public AiProvider Id { get; }
    public string DisplayName { get; }
    public bool IsImplemented => false;
    public bool IsAvailable => false;

    public Task<string> ReformatAsync(
        string rawText,
        string destinationPrompt,
        string language,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{DisplayName} provider is not yet implemented");

    public Task<(bool success, string message)> TestApiKeyAsync()
        => Task.FromResult((false, $"{DisplayName}: coming soon"));
}
