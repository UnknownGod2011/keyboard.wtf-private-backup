namespace KeyboardWtf.Services.Ai;

using System.Threading.Tasks;
using KeyboardWtf.Models;

/// <summary>
/// Claude provider. Thin wrapper around the existing <see cref="ClaudeApiService"/> so the
/// service class can stay untouched while we introduce the multi-provider abstraction.
/// </summary>
internal sealed class ClaudeProvider : IAiProvider
{
    private readonly ClaudeApiService _service;

    public ClaudeProvider(ClaudeApiService service)
    {
        _service = service;
    }

    public AiProvider Id => AiProvider.Claude;
    public string DisplayName => "Claude";
    public bool IsImplemented => true;
    public bool IsAvailable => !string.IsNullOrEmpty(KeyboardWtfState.ClaudeApiKey);

    public Task<string> ReformatAsync(
        string rawText,
        string destinationPrompt,
        string language,
        CancellationToken cancellationToken = default)
        => _service
            .ReformatAsync(rawText, destinationPrompt, language)
            .WaitAsync(cancellationToken);

    public Task<(bool success, string message)> TestApiKeyAsync()
        => _service.TestApiKeyAsync();
}
