namespace KeyboardWtf.Services;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using KeyboardWtf.Models;
using KeyboardWtf.Services.PaidStaging;

public sealed class GeminiScreenGuidanceService : IDisposable
{
    private static readonly string[] ModelCandidates =
    [
        "gemini-2.5-flash",
        "gemini-2.5-flash-lite",
        "gemini-2.0-flash",
    ];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };
    private readonly PaidAccountService _paidAccount;

    public GeminiScreenGuidanceService() : this(null) { }

    internal GeminiScreenGuidanceService(PaidAccountService paidAccount) =>
        _paidAccount = paidAccount;

    public async Task<string> InspectAsync(string request, CancellationToken token)
    {
        if (_paidAccount?.Enabled != true && string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey))
            throw new InvalidOperationException("Gemini is not configured.");

        KeyboardWtfState.SetUi(VoiceUiPhase.Executing, "Capturing screen", "One-time capture approved. Nothing is saved.");
        var image = CaptureScreenJpeg();
        token.ThrowIfCancellationRequested();
        KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Analyzing screen", "Gemini is examining this one approved image.");

        if (_paidAccount?.Enabled == true)
        {
            var response = await _paidAccount.GenerateAsync(
                BuildPrompt(request),
                null,
                900,
                null,
                new[] { new PaidInlineImage("image/jpeg", Convert.ToBase64String(image)) },
                "screen-guidance",
                token);
            KeyboardWtfState.SetUi(VoiceUiPhase.Done, "Guidance ready", Limit(response.Text, 120));
            return response.Text;
        }

        Exception lastError = null;
        foreach (var model in ModelCandidates)
        {
            try
            {
                return await AnalyzeAsync(model, image, request, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                AppLog.Warning(ex, $"Gemini screen analysis failed: {model}");
            }
        }

        throw lastError ?? new InvalidOperationException("Screen analysis failed.");
    }

    public static byte[] CaptureScreenJpeg()
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The screen could not be captured.");

        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

        const int maxWidth = 1600;
        var scale = Math.Min(1, maxWidth / (double)source.Width);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
        resized.Save(stream, encoder, parameters);
        return stream.ToArray();
    }

    private async Task<string> AnalyzeAsync(
        string model,
        byte[] jpeg,
        string request,
        CancellationToken token)
    {
        var prompt = BuildPrompt(request);
        var body = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inlineData = new
                            {
                                mimeType = "image/jpeg",
                                data = Convert.ToBase64String(jpeg),
                            },
                        },
                    },
                },
            },
            generationConfig = new
            {
                temperature = 0.15,
                maxOutputTokens = 900,
            },
        };

        using var requestMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        requestMessage.Headers.Add("x-goog-api-key", KeyboardWtfState.GeminiApiKey);
        requestMessage.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(requestMessage, token);
        var responseBody = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gemini vision returned {(int)response.StatusCode}: {ExtractError(responseBody)}");

        var text = ExtractText(responseBody);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Gemini vision returned no guidance.");
        KeyboardWtfState.SetUi(VoiceUiPhase.Done, "Guidance ready", Limit(text, 120));
        return text;
    }

    private static string BuildPrompt(string request)
    {
        var userRequest = string.IsNullOrWhiteSpace(request)
            ? "Explain what is visible and give the next safe step."
            : request.Trim();
        return
            """
            You are the screen-guidance component of keyboard.wtf on Windows.
            Analyze only this one user-approved screenshot.
            Give concise, step-by-step guidance about what is visibly present.
            Identify likely buttons or controls by their visible labels and relative position.
            Never claim you clicked, typed, submitted, or changed anything.
            If the requested control is not visible or uncertain, say so and explain what to look for.
            Avoid repeating private values, API keys, passwords, tokens, email bodies, financial details,
            or personal messages visible on screen unless the user explicitly asks to read that exact content.
            Do not infer hidden page state or browser DOM.

            User request:
            """ + userRequest;
    }

    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.GetArrayLength() == 0
            || !candidates[0].TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts))
            return "";
        return string.Join(
            Environment.NewLine,
            parts.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string ExtractError(string responseJson)
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

    private static string Limit(string value, int maxLength)
    {
        var text = (value ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    public void Dispose() => _http.Dispose();
}
