using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using Microsoft.Extensions.Options;
using KeyboardWtf.Staging.Api.Contracts;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class VertexAiClient : IVertexAiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly GoogleCredential _credential;
    private readonly PaidSystemOptions _options;

    public VertexAiClient(HttpClient httpClient, IOptions<PaidSystemOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _credential = GoogleCredential.GetApplicationDefault()
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
    }

    public async Task<VertexGenerateResult> GenerateAsync(
        string model,
        string prompt,
        string? systemInstruction,
        int maxOutputTokens,
        JsonElement? responseJsonSchema,
        IReadOnlyList<InlineImageRequest>? images,
        CancellationToken cancellationToken)
    {
        var endpoint =
            $"https://{_options.Region}-aiplatform.googleapis.com/v1/projects/{Uri.EscapeDataString(_options.ProjectId)}/locations/{Uri.EscapeDataString(_options.Region)}/publishers/google/models/{Uri.EscapeDataString(model)}:generateContent";
        var parts = new List<object> { new { text = prompt } };
        if (images is not null)
        {
            parts.AddRange(images.Select(image => (object)new
            {
                inlineData = new { mimeType = image.MimeType, data = image.Data },
            }));
        }
        var generationConfig = new Dictionary<string, object>
        {
            ["maxOutputTokens"] = Math.Clamp(maxOutputTokens, 1, 8192),
            ["temperature"] = 0.3,
        };
        if (responseJsonSchema is JsonElement schema)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseSchema"] = schema;
        }
        var body = new Dictionary<string, object?>
        {
            ["contents"] = new[]
            {
                new { role = "user", parts },
            },
            ["generationConfig"] = generationConfig,
        };
        if (!string.IsNullOrWhiteSpace(systemInstruction))
            body["systemInstruction"] = new { parts = new[] { new { text = systemInstruction } } };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var accessToken = await _credential.UnderlyingCredential
            .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new VertexRequestRejectedException((int)response.StatusCode);

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var text = ExtractText(root);
        var usage = ExtractUsage(root, response.Headers);
        return new VertexGenerateResult(text, usage);
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
            return "";
        var output = new StringBuilder();
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts))
                continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                    output.Append(text.GetString());
            }
        }
        return output.ToString().Trim();
    }

    private static ProviderUsage ExtractUsage(JsonElement root, HttpResponseHeaders headers)
    {
        var usage = root.TryGetProperty("usageMetadata", out var value) ? value : default;
        static long Read(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var item)
            && item.TryGetInt64(out var count)
                ? count
                : 0;
        static long ReadModality(JsonElement element, string property, string modality)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(property, out var details)
                || details.ValueKind != JsonValueKind.Array)
                return 0;
            long total = 0;
            foreach (var detail in details.EnumerateArray())
            {
                if (detail.TryGetProperty("modality", out var kind)
                    && string.Equals(kind.GetString(), modality, StringComparison.OrdinalIgnoreCase)
                    && detail.TryGetProperty("tokenCount", out var count)
                    && count.TryGetInt64(out var value))
                    total += value;
            }
            return total;
        }
        var requestId = headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;
        requestId ??= root.TryGetProperty("responseId", out var responseId)
            ? responseId.GetString()
            : null;
        var inputImage = ReadModality(usage, "promptTokensDetails", "IMAGE");
        var outputImage = ReadModality(usage, "candidatesTokensDetails", "IMAGE");
        var inputAudio = ReadModality(usage, "promptTokensDetails", "AUDIO");
        var outputAudio = ReadModality(usage, "candidatesTokensDetails", "AUDIO");
        return new ProviderUsage(
            Read(usage, "promptTokenCount"),
            Read(usage, "candidatesTokenCount"),
            Read(usage, "cachedContentTokenCount"),
            Read(usage, "thoughtsTokenCount"),
            inputImage + outputImage,
            inputAudio + outputAudio,
            Read(usage, "totalTokenCount"),
            requestId,
            root.TryGetProperty("modelVersion", out var modelVersion)
                ? modelVersion.GetString()
                : null,
            inputAudio,
            outputAudio,
            inputImage,
            outputImage);
    }
}
