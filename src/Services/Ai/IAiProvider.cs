namespace KeyboardWtf.Services.Ai;

using System.Threading.Tasks;
using KeyboardWtf.Models;

/// <summary>
/// Pluggable LLM provider for destination-specific reformatting.
///
/// <para>Two state axes on each provider:</para>
/// <list type="bullet">
///   <item><see cref="IsImplemented"/>: whether this provider's code is actually built.
///         Placeholders set this to <c>false</c> so the UI can show a "Coming soon" badge
///         instead of a functional test button.</item>
///   <item><see cref="IsAvailable"/>: whether an API key is configured for this provider.
///         True only when both implemented AND the key is present.</item>
/// </list>
/// </summary>
internal interface IAiProvider
{
    AiProvider Id { get; }
    string DisplayName { get; }
    bool IsImplemented { get; }
    bool IsAvailable { get; }

    /// <summary>
    /// Reformat raw transcript for a specific destination. The destination's system prompt and
    /// the user's current language are passed through; providers may add their own language
    /// hints on top.
    /// </summary>
    Task<string> ReformatAsync(
        string rawText,
        string destinationPrompt,
        string language,
        CancellationToken cancellationToken = default);

    /// <summary>Low-cost smoke test for the currently-configured API key.</summary>
    Task<(bool success, string message)> TestApiKeyAsync();
}

internal interface IStructuredAiProvider : IAiProvider
{
    Task<T> GenerateStructuredAsync<T>(
        string prompt,
        object responseSchema,
        CancellationToken cancellationToken = default);
}
