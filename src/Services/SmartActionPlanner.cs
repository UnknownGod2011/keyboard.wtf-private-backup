namespace KeyboardWtf.Services;

using KeyboardWtf.Models;
using KeyboardWtf.Services.Ai;
using System.Text.RegularExpressions;

public sealed record SmartActionPlan(
    string Action,
    string Recipient,
    string Subject,
    string Content,
    string SpokenResponse);

public sealed class SmartActionPlanner
{
    private static readonly object ResponseSchema = new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                @enum = new[]
                {
                    "type_text",
                    "copy",
                    "open_notepad",
                    "open_settings",
                    "open_url",
                    "gmail_draft",
                    "ask",
                },
                description = "The single safest action that fulfills the request.",
            },
            recipient = new { type = "string", description = "Email recipient or empty string." },
            subject = new { type = "string", description = "Short email subject or empty string." },
            content = new { type = "string", description = "Final polished user-ready text or URL." },
            spokenResponse = new { type = "string", description = "A concise response to speak to the user." },
        },
        required = new[] { "action", "recipient", "subject", "content", "spokenResponse" },
        additionalProperties = false,
    };

    public async Task<SmartActionPlan> PlanAsync(
        string transcript,
        CancellationToken cancellationToken = default)
    {
        AppLog.Info("Smart action planning started");
        var gemini = AiProviderRegistry.Get(AiProvider.Gemini);
        var knownPlan = await TryPlanKnownActionAsync(transcript, gemini, cancellationToken);
        if (knownPlan != null)
        {
            AppLog.Info($"Smart action planned locally: {knownPlan.Action}");
            return knownPlan;
        }

        if (gemini is not IStructuredAiProvider structured || !structured.IsAvailable)
        {
            AppLog.Warning("Smart action planning using offline fallback");
            return FallbackPlan(transcript);
        }

        var prompt =
            """
            You are the safe action planner for keyboard.wtf, a Windows voice assistant.
            Interpret the user's spoken request and return exactly one action.

            Rules:
            - If the user asks to email, message, send, draft, apologize, or write to an email address,
              choose gmail_draft. Extract the recipient. Rewrite the body so it is polished and
              professional when requested or when the original is rough speech.
            - gmail_draft only prepares Gmail compose. It never sends automatically.
            - If required information is missing, choose ask and put one short clarifying question
              in spokenResponse.
            - Safe desktop actions are open_notepad, open_settings, open_url, copy, and type_text.
            - Only allow http or https URLs.
            - For ordinary text with no requested computer action, choose type_text and clean obvious
              speech disfluencies while preserving meaning.
            - spokenResponse must be short and honest. For gmail_draft say the draft is ready and that
              the user must review and click Send.

            User transcript:
            """ + transcript;

        try
        {
            var plan = await structured.GenerateStructuredAsync<SmartActionPlan>(
                prompt,
                ResponseSchema,
                cancellationToken);
            AppLog.Info("Smart action planning completed");
            return Validate(plan, transcript);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Gemini action planning failed; using safe local fallback");
            return FallbackPlan(transcript);
        }
    }

    private static async Task<SmartActionPlan> TryPlanKnownActionAsync(
        string transcript,
        IAiProvider gemini,
        CancellationToken cancellationToken)
    {
        var text = transcript?.Trim() ?? "";
        var lower = text.ToLowerInvariant();

        if (lower.Contains("notepad", StringComparison.Ordinal)
            && ContainsAny(lower, "open", "launch", "start"))
            return new("open_notepad", "", "", "", "Opening Notepad.");

        if (lower.Contains("settings", StringComparison.Ordinal)
            && ContainsAny(lower, "open", "show"))
            return new("open_settings", "", "", "", "Opening settings.");

        var email = Regex.Match(text, @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase);
        var asksForEmail = email.Success
            && ContainsAny(lower, "email", "gmail", "message", "send", "write", "draft");
        if (!asksForEmail)
            return null;

        var body = ExtractEmailBody(text, email.Value);
        if (string.IsNullOrWhiteSpace(body))
            return new("ask", email.Value, "", "", "What should the email say?");

        var wantsProfessional = ContainsAny(lower, "professional", "polished", "formal");
        if (gemini?.IsAvailable == true)
        {
            try
            {
                var prompt = wantsProfessional
                    ? "Rewrite this as a concise, professional email body. Preserve every important fact. Return only the body."
                    : "Clean this spoken email into a clear, natural email body. Preserve the tone and meaning. Return only the body.";
                body = await gemini
                    .ReformatAsync(body, prompt, KeyboardWtfState.SelectedLanguage)
                    .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
            catch (TimeoutException)
            {
                AppLog.Warning("Gemini email rewrite timed out; using cleaned transcript");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warning(ex, "Gemini email rewrite failed; using cleaned transcript");
            }
        }

        var subject = ContainsAny(body.ToLowerInvariant(), "sorry", "apolog", "late")
            ? "Apology for Being Late"
            : "Message";
        return new(
            "gmail_draft",
            email.Value,
            subject,
            body,
            "The Gmail draft is ready. Please review it, then click Send.");
    }

    private static string ExtractEmailBody(string text, string email)
    {
        var withoutRecipient = Regex.Replace(
            text,
            $@"\s+(?:to|for)\s+{Regex.Escape(email)}[\s.]*$",
            "",
            RegexOptions.IgnoreCase);
        var marker = Regex.Match(
            withoutRecipient,
            @"\b(?:saying|that says|with the message|message(?:\s+(?:like|saying))?|email(?:\s+(?:like|saying))?|body)\b[\s:,-]*",
            RegexOptions.IgnoreCase);
        var body = marker.Success
            ? withoutRecipient[(marker.Index + marker.Length)..]
            : withoutRecipient;
        body = Regex.Replace(
            body,
            @"^(?:send|write|draft|create|open gmail and create)\b.*?\b(?:to|for)\b\s*",
            "",
            RegexOptions.IgnoreCase);
        return body.Trim(' ', '.', ',', ':', '-', '"', '\'');
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static SmartActionPlan Validate(SmartActionPlan plan, string transcript)
    {
        var action = (plan.Action ?? "").Trim().ToLowerInvariant();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "type_text",
            "copy",
            "open_notepad",
            "open_settings",
            "open_url",
            "gmail_draft",
            "ask",
        };
        if (!allowed.Contains(action))
            return FallbackPlan(transcript);

        if (action == "gmail_draft" && string.IsNullOrWhiteSpace(plan.Recipient))
            return new SmartActionPlan("ask", "", "", "", "Who should I address the email to?");

        if (action == "open_url"
            && (!Uri.TryCreate(plan.Content, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            return new SmartActionPlan("ask", "", "", "", "Which safe web address should I open?");

        return plan with
        {
            Action = action,
            Recipient = plan.Recipient?.Trim() ?? "",
            Subject = plan.Subject?.Trim() ?? "",
            Content = string.IsNullOrWhiteSpace(plan.Content) ? transcript : plan.Content.Trim(),
            SpokenResponse = plan.SpokenResponse?.Trim() ?? "",
        };
    }

    private static SmartActionPlan FallbackPlan(string transcript)
    {
        var parsed = VoiceCommandParser.Parse(transcript);
        return parsed.Kind switch
        {
            ParsedCommandKind.OpenNotepad => new("open_notepad", "", "", "", "Opening Notepad."),
            ParsedCommandKind.OpenSettings => new("open_settings", "", "", "", "Opening settings."),
            ParsedCommandKind.OpenUrl => new("open_url", "", "", parsed.Argument ?? "", "Opening the link."),
            ParsedCommandKind.GmailDraft => new(
                "gmail_draft",
                parsed.Argument ?? "",
                "",
                parsed.Content ?? transcript,
                "The Gmail draft is ready. Review it and click Send when you are ready."),
            ParsedCommandKind.Copy => new("copy", "", "", parsed.Content ?? transcript, "Copied."),
            _ => new("type_text", "", "", transcript, ""),
        };
    }
}
