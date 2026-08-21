namespace KeyboardWtf.Services.Ai;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services.PaidStaging;

internal sealed class PaidCloudAiProvider : IStructuredAiProvider
{
    private readonly PaidAccountService _account;

    public PaidCloudAiProvider(PaidAccountService account) =>
        _account = account ?? throw new ArgumentNullException(nameof(account));

    public AiProvider Id => AiProvider.Gemini;
    public string DisplayName => "keyboard.wtf Cloud AI";
    public bool IsImplemented => true;
    public bool IsAvailable => _account.Enabled && _account.IsSignedIn;
    internal string UnavailableMessage => !_account.IsSignedIn
        ? "Sign in with Google in Account & Billing before using Smart Writing."
        : "Smart Writing is unavailable for this account right now.";

    public async Task<string> ReformatAsync(
        string rawText,
        string destinationPrompt,
        string language,
        CancellationToken cancellationToken = default)
    {
        var response = await _account.GenerateAsync(
            rawText,
            $"{destinationPrompt}\nOutput in {(language == "auto" ? "the input language" : language)}. Return only the final user-ready text.",
            1024,
            null,
            null,
            "desktop-reformat",
            cancellationToken);
        return response.Text;
    }

    internal async Task<string> ReformatAudioAsync(
        byte[] wavAudio,
        string destinationPrompt,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (wavAudio is null || wavAudio.Length < 44)
            throw new ArgumentException("Smart Writing audio is empty.", nameof(wavAudio));
        var response = await _account.GenerateAudioAsync(
            "Convert the attached speech into the exact user-ready writing requested by the system instruction.",
            $"{destinationPrompt}\nOutput in {(language == "auto" ? "the spoken language" : language)}.",
            2048,
            new PaidInlineAudio("audio/wav", Convert.ToBase64String(wavAudio)),
            "smart-writing-audio",
            cancellationToken);
        return response.Text;
    }

    public async Task<T> GenerateStructuredAsync<T>(
        string prompt,
        object responseSchema,
        CancellationToken cancellationToken = default)
    {
        var response = await _account.GenerateAsync(
            prompt,
            "Return only valid JSON matching the supplied response schema.",
            1500,
            responseSchema,
            null,
            "desktop-structured",
            cancellationToken);
        return JsonSerializer.Deserialize<T>(response.Text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("Cloud AI returned an empty structured response.");
    }

    public async Task<(bool success, string message)> TestApiKeyAsync()
    {
        if (!_account.Enabled)
            return (false, "Paid account is disabled.");
        if (!_account.IsSignedIn)
            return (false, "Sign in with Google first.");
        try
        {
            var response = await _account.GenerateAsync(
                "Reply with exactly OK.",
                null,
                16,
                null,
                null,
                "desktop-health",
                CancellationToken.None);
            return (string.Equals(response.Text.Trim(), "OK", StringComparison.OrdinalIgnoreCase),
                "Cloud AI account connection works.");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }
}
