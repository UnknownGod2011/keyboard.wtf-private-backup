namespace KeyboardWtf.Services.Ai;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Models;

internal sealed class GeminiProvider : IStructuredAiProvider, IDisposable
{
    private static readonly string[] ModelCandidates =
    [
        "gemini-2.5-flash-lite",
        "gemini-2.5-flash",
        "gemini-2.0-flash",
    ];
    private static readonly string[] StructuredModelCandidates =
    [
        "gemini-2.5-flash-lite",
        "gemini-2.5-flash",
        "gemini-2.0-flash",
    ];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public AiProvider Id => AiProvider.Gemini;
    public string DisplayName => "Google Gemini";
    public bool IsImplemented => true;
    public bool IsAvailable => !string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey);

    public async Task<string> ReformatAsync(
        string rawText,
        string destinationPrompt,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Gemini API key not configured");

        var prompt = BuildPrompt(rawText, destinationPrompt, language);
        Exception lastError = null;
        foreach (var model in ModelCandidates)
        {
            try
            {
                AppLog.Info($"Gemini request starting: {model}");
                return await SendAsync(model, prompt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                AppLog.Warning(ex, $"Gemini model failed: {model}");
            }
        }

        throw lastError ?? new InvalidOperationException("Gemini request failed");
    }

    public async Task<T> GenerateStructuredAsync<T>(
        string prompt,
        object responseSchema,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Gemini API key not configured");

        Exception lastError = null;
        foreach (var model in StructuredModelCandidates)
        {
            try
            {
                AppLog.Info($"Gemini structured request starting: {model}");
                var body = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[] { new { text = prompt } },
                        },
                    },
                    generationConfig = new
                    {
                        temperature = 0.1,
                        maxOutputTokens = 1024,
                        thinkingConfig = new { thinkingBudget = 0 },
                        responseMimeType = "application/json",
                        responseJsonSchema = responseSchema,
                    },
                };

                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                var requestTask = SendRequestAsync(model, body, requestTimeout.Token);
                var completed = await Task.WhenAny(
                    requestTask,
                    Task.Delay(TimeSpan.FromSeconds(8), cancellationToken));
                if (completed != requestTask)
                    throw new TimeoutException($"Gemini structured request timed out: {model}");
                var responseBody = await requestTask;
                AppLog.Info($"Gemini structured request received: {model}");
                var json = ExtractContent(responseBody);
                var result = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                return result ?? throw new InvalidDataException("Gemini returned an empty structured response");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                AppLog.Warning(ex, $"Gemini structured request failed: {model}");
            }
        }

        throw lastError ?? new InvalidOperationException("Gemini structured request failed");
    }

    public async Task<(bool success, string message)> TestApiKeyAsync()
    {
        try
        {
            var result = await ReformatAsync("Hello", "Respond with exactly: OK", "en");
            return (true, $"API key works. Response: {result}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<string> SendAsync(
        string model,
        string prompt,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = prompt } },
                },
            },
            generationConfig = new
            {
                temperature = 0.2,
                maxOutputTokens = 1024,
            },
        };

        var responseBody = await SendRequestAsync(model, body, cancellationToken);
        return ExtractContent(responseBody);
    }

    private async Task<string> SendRequestAsync(
        string model,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        request.Headers.Add("x-goog-api-key", KeyboardWtfState.GeminiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = ExtractErrorMessage(responseBody);
            throw new HttpRequestException($"Gemini API returned {(int)response.StatusCode}: {error}");
        }

        return responseBody;
    }

    private static string BuildPrompt(string rawText, string destinationPrompt, string language)
    {
        var langInstruction = language switch
        {
            "en" => "Output in English.",
            "de" => "Output in German (Deutsch).",
            _ => "Output in the same language as the input.",
        };

        return
            $"{destinationPrompt}\n\n" +
            $"{langInstruction}\n\n" +
            "The input is a speech-to-text transcript and may contain recognition errors. " +
            "Fix obvious recognition errors while preserving the intended meaning. " +
            "Return only the final user-ready text.\n\n" +
            $"Transcript:\n{rawText}";
    }

    private static string ExtractContent(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return string.Empty;

        var first = candidates[0];
        if (!first.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        }

        return sb.ToString().Trim();
    }

    private static string ExtractErrorMessage(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
                return message.GetString() ?? "Unknown error";
        }
        catch { }

        return "Unknown error";
    }

    public void Dispose() => _http.Dispose();
}
