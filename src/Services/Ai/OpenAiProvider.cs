namespace KeyboardWtf.Services.Ai;

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KeyboardWtf.Models;

/// <summary>
/// OpenAI GPT provider via the chat completions endpoint.
/// Mirrors <see cref="ClaudeProvider"/> semantically so <see cref="AiProviderRegistry"/> can
/// swap between them transparently.
/// </summary>
internal sealed class OpenAiProvider : IAiProvider, IDisposable
{
    private const string ApiUrl = "https://api.openai.com/v1/chat/completions";
    private const string DefaultModel = "gpt-4o-mini";   // fast, cheap, plenty for destination formatting
    private const int MaxTokens = 1024;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public AiProvider Id => AiProvider.OpenAi;
    public string DisplayName => "OpenAI GPT";
    public bool IsImplemented => true;
    public bool IsAvailable => !string.IsNullOrEmpty(KeyboardWtfState.OpenAiApiKey);

    public async Task<string> ReformatAsync(
        string rawText,
        string destinationPrompt,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("OpenAI API key not configured");

        var systemPrompt = BuildSystemPrompt(destinationPrompt, language);

        var body = new
        {
            model = DefaultModel,
            max_tokens = MaxTokens,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = rawText },
            },
        };

        return await SendAsync(body, cancellationToken);
    }

    public async Task<(bool success, string message)> TestApiKeyAsync()
    {
        try
        {
            var result = await ReformatAsync("Hello", "Respond with just: OK", "en");
            return (true, $"API key works. Response: {result}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<string> SendAsync(object body, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", $"Bearer {KeyboardWtfState.OpenAiApiKey}");

        var response = await _http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Error($"OpenAI API error {(int)response.StatusCode}: {responseBody}");
            throw new HttpRequestException($"OpenAI API returned {(int)response.StatusCode}: {ExtractErrorMessage(responseBody)}");
        }

        return ExtractContent(responseBody);
    }

    private static string BuildSystemPrompt(string destinationPrompt, string language)
    {
        var langInstruction = language switch
        {
            "en" => "Output in English.",
            "de" => "Output in German (Deutsch).",
            _    => "Output in the same language as the input.",
        };
        return $"{destinationPrompt}\n\n{langInstruction}\n\nThe input is a speech-to-text transcript and may contain recognition errors. Fix obvious errors while preserving the intended meaning.";
    }

    private static string ExtractContent(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                return content.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string ExtractErrorMessage(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return "Unknown error";
    }

    public void Dispose() => _http.Dispose();
}
