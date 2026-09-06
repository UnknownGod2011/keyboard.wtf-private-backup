namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyboardWtf.Models;
using KeyboardWtf.Services.PaidStaging;

public sealed class BrowserPlannerAction
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "";

    [JsonPropertyName("targetRole")]
    public string TargetRole { get; set; } = "";

    [JsonPropertyName("targetName")]
    public string TargetName { get; set; } = "";

    [JsonPropertyName("targetFieldId")]
    public string TargetFieldId { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("option")]
    public string Option { get; set; } = "";

    [JsonPropertyName("replace")]
    public bool Replace { get; set; }

    [JsonPropertyName("submitAfter")]
    public bool SubmitAfter { get; set; }

    [JsonPropertyName("captureBeforeAction")]
    public bool CaptureBeforeAction { get; set; }
}

public sealed class BrowserPlannerDecision
{
    [JsonPropertyName("evaluation")]
    public string Evaluation { get; set; } = "";

    [JsonPropertyName("nextGoal")]
    public string NextGoal { get; set; } = "";

    [JsonPropertyName("action")]
    public BrowserPlannerAction Action { get; set; } = new();

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("resultSummary")]
    public string ResultSummary { get; set; } = "";

    [JsonPropertyName("completionKind")]
    public string CompletionKind { get; set; } = "";

    [JsonPropertyName("completionValue")]
    public string CompletionValue { get; set; } = "";

    [JsonPropertyName("needsUserAction")]
    public bool NeedsUserAction { get; set; }

    [JsonPropertyName("userAction")]
    public string UserAction { get; set; } = "";

    [JsonPropertyName("userActionReason")]
    public string UserActionReason { get; set; } = "";

    [JsonPropertyName("userActionRequired")]
    public bool UserActionRequired { get; set; }

    [JsonPropertyName("userActionSkippable")]
    public bool UserActionSkippable { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}

public sealed class BrowserAutomationPlanner : IDisposable
{
    private static readonly HashSet<string> AllowedActions = new(
        StringComparer.Ordinal)
    {
        "navigate",
        "open_tab",
        "activate_tab",
        "click",
        "type",
        "paste",
        "press_key",
        "select_option",
        "check",
        "uncheck",
        "scroll",
        "copy_clipboard",
        // Internal cursor operations are issued only by the coordinator after
        // the planner chose paste. They are not planner-facing actions.
        "vision",
        "done",
        "needs_user_action",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Schema = new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["evaluation"] = StringSchema(),
            ["nextGoal"] = StringSchema(),
            ["action"] = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["type"] = StringSchema(),
                    ["targetId"] = StringSchema(),
                    ["targetRole"] = StringSchema(),
                    ["targetName"] = StringSchema(),
                    ["targetFieldId"] = StringSchema(),
                    ["text"] = StringSchema(),
                    ["url"] = StringSchema(),
                    ["key"] = StringSchema(),
                    ["option"] = StringSchema(),
                    ["replace"] = new { type = "boolean" },
                    ["submitAfter"] = new { type = "boolean" },
                    ["captureBeforeAction"] = new { type = "boolean" },
                },
                required = new[] { "type", "replace" },
            },
            ["done"] = new { type = "boolean" },
            ["resultSummary"] = StringSchema(),
            ["completionKind"] = StringSchema(),
            ["completionValue"] = StringSchema(),
            ["needsUserAction"] = new { type = "boolean" },
            ["userAction"] = StringSchema(),
            ["userActionReason"] = StringSchema(),
            ["userActionRequired"] = new { type = "boolean" },
            ["userActionSkippable"] = new { type = "boolean" },
            ["confidence"] = new { type = "number" },
        },
        required = new[]
        {
            "evaluation", "nextGoal", "action", "done", "resultSummary",
            "completionKind", "completionValue", "needsUserAction",
            "userAction", "userActionReason", "userActionRequired",
            "userActionSkippable", "confidence",
        },
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(28) };
    private readonly BrowserSemanticControlRanker _ranker = new();
    private readonly object _healthLock = new();
    private readonly Dictionary<string, DateTime> _unhealthyUntil = new(
        StringComparer.OrdinalIgnoreCase);
    private string _healthTaskId = "";
    private string _lastModel = "";
    private readonly PaidAccountService _paidAccount;

    public BrowserAutomationPlanner() : this(null) { }

    internal BrowserAutomationPlanner(PaidAccountService paidAccount) =>
        _paidAccount = paidAccount;

    public string LastModel => _lastModel;

    /// <summary>
    /// Provider health is deliberately scoped to one browser task. A malformed
    /// response or temporary quota error can make a fallback model preferable
    /// for the rest of that task, but it must never poison an unrelated request.
    /// </summary>
    internal void BeginTask(string taskId)
    {
        lock (_healthLock)
        {
            if (string.Equals(_healthTaskId, taskId, StringComparison.Ordinal))
                return;
            _healthTaskId = taskId ?? "";
            _unhealthyUntil.Clear();
            _lastModel = "";
        }
    }

    internal int UnhealthyModelCount
    {
        get
        {
            lock (_healthLock)
                return _unhealthyUntil.Count;
        }
    }

    internal void RecordModelFailure(
        string model,
        Exception error,
        DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(model))
            return;
        var cooldown = error?.Message.Contains(
                "429",
                StringComparison.OrdinalIgnoreCase) == true
            ? TimeSpan.FromMinutes(10)
            : TimeSpan.FromMinutes(2);
        lock (_healthLock)
            _unhealthyUntil[model] = (nowUtc ?? DateTime.UtcNow).Add(cooldown);
    }

    private bool IsCoolingDown(string model)
    {
        lock (_healthLock)
            return _unhealthyUntil.TryGetValue(model, out var until)
                && until > DateTime.UtcNow;
    }

    public async Task<BrowserPlannerDecision> PlanAsync(
        string request,
        BrowserPageSnapshot page,
        IReadOnlyList<string> history,
        string configuredModel,
        CancellationToken token)
    {
        if (_paidAccount?.Enabled == true)
        {
            var rankedPaid = _ranker.Rank(request, page.Controls, 110);
            var paidPrompt = BuildPrompt(request, page, history, rankedPaid);
            _lastModel = "vertex-cloud";
            return await ExecutePaidPlanWithMalformedRetryAsync(
                attemptToken => SendPaidStructuredAsync(
                    paidPrompt,
                    null,
                    attemptToken),
                token,
                ex =>
                    AppLog.Warning(
                        ex,
                        "Paid browser planner returned malformed structured output; retrying once within the same task"));
        }
        if (string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey))
            throw new InvalidOperationException("A Gemini API key is required for smart browser automation.");

        // Failed-action history is intentionally excluded from semantic ranking.
        // The planner still receives that history below, but an exhausted control
        // name must not become more prominent merely because it was just rejected.
        var ranked = _ranker.Rank(request, page.Controls, 110);
        var prompt = BuildPrompt(request, page, history, ranked);
        Exception lastError = null;
        foreach (var model in ModelCandidates(configuredModel))
        {
            if (IsCoolingDown(model))
                continue;

            try
            {
                _lastModel = model;
                var decision = await SendStructuredAsync(model, prompt, token);
                ValidateDecision(decision);
                return decision;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                RecordModelFailure(model, ex);
                AppLog.Warning(ex, $"Browser planner model failed: {model}");
            }
        }

        throw lastError ?? new InvalidOperationException("No healthy Gemini browser planner is available.");
    }

    internal static bool ShouldRetryMalformedPaidPlan(
        Exception error,
        int attempt) =>
        attempt < 2
        && error is JsonException or InvalidDataException;

    internal static async Task<BrowserPlannerDecision> ExecutePaidPlanWithMalformedRetryAsync(
        Func<CancellationToken, Task<BrowserPlannerDecision>> requestPlan,
        CancellationToken token,
        Action<Exception> onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(requestPlan);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var decision = await requestPlan(token);
                ValidateDecision(decision);
                return decision;
            }
            catch (Exception ex)
                when (ShouldRetryMalformedPaidPlan(ex, attempt))
            {
                onRetry?.Invoke(ex);
            }
        }

        throw new InvalidDataException(
            "Cloud AI returned invalid browser planner output.");
    }

    public async Task<BrowserPlannerDecision> PlanWithVisionAsync(
        string request,
        BrowserPageSnapshot page,
        IReadOnlyList<string> history,
        string screenshotDataUrl,
        string configuredModel,
        CancellationToken token)
    {
        if (!TryParseDataUrl(screenshotDataUrl, out var mimeType, out var imageBase64))
            throw new InvalidDataException("The Chrome Helper returned an invalid screenshot.");

        var ranked = _ranker.Rank(request, page.Controls, 150);
        var prompt = BuildPrompt(request, page, history, ranked)
            + "\nVISUAL RECOVERY:\n"
            + "The attached screenshot is the current visible viewport. Identify the requested visible control, icon, diagram, or image question. "
            + "Correlate it to one current control ID when an interactive DOM candidate exists. "
            + "Do not return an approximate coordinate. If no safe live control maps to the visual target, use needs_user_action.\n";

        if (_paidAccount?.Enabled == true)
        {
            _lastModel = "vertex-cloud";
            var paidDecision = await SendPaidStructuredAsync(
                prompt,
                new PaidInlineImage(mimeType, imageBase64),
                token);
            ValidateDecision(paidDecision);
            return paidDecision;
        }
        if (string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey))
            throw new InvalidOperationException("A Gemini API key is required for visual browser recovery.");

        Exception lastError = null;
        foreach (var model in ModelCandidates(configuredModel))
        {
            if (IsCoolingDown(model))
                continue;
            try
            {
                _lastModel = model;
                var decision = await SendStructuredAsync(
                    model,
                    prompt,
                    token,
                    new { inlineData = new { mimeType, data = imageBase64 } });
                ValidateDecision(decision);
                return decision;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                RecordModelFailure(model, ex);
                AppLog.Warning(ex, $"Browser vision model failed: {model}");
            }
        }
        throw lastError ?? new InvalidOperationException("No healthy Gemini vision model is available.");
    }

    private async Task<BrowserPlannerDecision> SendStructuredAsync(
        string model,
        string prompt,
        CancellationToken token,
        object additionalPart = null)
    {
        var parts = additionalPart == null
            ? new object[] { new { text = prompt } }
            : new object[] { new { text = prompt }, additionalPart };
        var body = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts,
                },
            },
            generationConfig = new
            {
                temperature = 0.15,
                maxOutputTokens = 1500,
                thinkingConfig = new { thinkingBudget = 512 },
                responseMimeType = "application/json",
                responseJsonSchema = Schema,
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        request.Headers.Add("x-goog-api-key", KeyboardWtfState.GeminiApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http.SendAsync(request, token);
        var responseBody = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Gemini browser planner returned {(int)response.StatusCode}: {SafeError(responseBody)}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var text = document.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        return ParseDecision(text ?? "{}");
    }

    private async Task<BrowserPlannerDecision> SendPaidStructuredAsync(
        string prompt,
        PaidInlineImage image,
        CancellationToken token)
    {
        var response = await _paidAccount.GenerateAsync(
            prompt,
            "You are the grounded browser planner for keyboard.wtf. Return only valid JSON matching the response schema.",
            1500,
            Schema,
            image is null ? null : new[] { image },
            image is null ? "browser-plan" : "browser-vision-recovery",
            token);
        return ParseDecision(response.Text);
    }

    internal static BrowserPlannerDecision ParseDecision(string text)
    {
        using var document = JsonDocument.Parse(text);
        var decision = document.RootElement.Deserialize<BrowserPlannerDecision>(JsonOptions)
            ?? throw new InvalidDataException("Cloud AI returned an empty browser plan.");
        if (decision.Action?.Type?.Trim().ToLowerInvariant() is "type" or "paste")
        {
            // Missing is not append: a model may describe a replacement in its
            // reasoning yet omit the flag. Reject that ambiguous wire action
            // through the existing bounded malformed-plan recovery.
            var action = document.RootElement.EnumerateObject().First(
                property => property.Name.Equals("action", StringComparison.OrdinalIgnoreCase)).Value;
            if (!action.EnumerateObject().Any(property =>
                    property.Name.Equals("replace", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("A browser text write must explicitly specify replace=true or replace=false.");
        }
        return decision;
    }

    internal static string BuildPrompt(
        string request,
        BrowserPageSnapshot page,
        IReadOnlyList<string> history,
        IReadOnlyList<BrowserControl> controls)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the browser-use-quality planner inside keyboard.wtf.");
        builder.AppendLine("Keep the complete user outcome in context. Evaluate the last action, remember concise progress, and choose exactly one grounded next action.");
        builder.AppendLine("Use live DOM/accessibility controls before scrolling or visual recovery. Prefer the smallest exact control.");
        builder.AppendLine("If the original request explicitly names a destination website, service, or domain and the current page is unrelated, establish that requested destination first with navigate/open_tab. Never use a convenient search box or relevant-sounding control on the unrelated current page as a substitute for the named destination.");
        builder.AppendLine("Before acting, verify that the control belongs to the correct entity, collection, section, and local context for the requested outcome. A globally visible control with a relevant-sounding label is not sufficient evidence.");
        builder.AppendLine("After grounding an action, read the target's complete accessible name and nearby label. Never activate a live control whose semantics contradict an explicit user constraint, such as one-way versus round-trip or latest versus oldest.");
        builder.AppendLine("For form controls, the field label identifies the question and the control name identifies its answer. Copy the chosen question's fieldId into action.targetFieldId alongside the exact control targetId. Identical answer labels in different questions are not interchangeable. Do not mark a form done with unanswered required questions: fill from supplied context or ask for the missing required value.");
        builder.AppendLine("For an ordered item from a named creator, account, profile, category, or collection, first reach and verify that entity's collection page, then its item/content section, then use the exact requested ordering control. A global search Filter/Sort control is not collection ordering.");
        builder.AppendLine("Before using a generic Filter, Sort, More, or menu control, identify the expected state transition and confirm its nearby context serves the immediate goal. Navigate deeper when it does not.");
        builder.AppendLine("If history says an action was unverified or made no progress on the unchanged page, that exact control and mechanism is exhausted. Do not repeat it. Choose a genuinely different control, navigation path, input mechanism, or one bounded visual recovery.");
        builder.AppendLine("Never invent a control ID. Use navigate/open_tab only with an absolute http/https URL.");
        builder.AppendLine("Allowed actions: navigate, open_tab, activate_tab, click, type, paste, press_key, select_option, check, uncheck, scroll, copy_clipboard, vision, done, needs_user_action.");
        builder.AppendLine("Task-owned tab history entries use stable target IDs such as tab:123. To switch among those tabs, use activate_tab and copy that exact tab:<id> value into action.targetId; never guess a tab ID.");
        builder.AppendLine("For an obvious search field, type the query with replace=true and submitAfter=true.");
        builder.AppendLine("Use paste for long text or a rich/contenteditable document editor. Use type for ordinary form inputs. Only target a control that is explicitly editable; never use a broad document, tabs, toolbar, side-panel, or container merely because its label sounds relevant.");
        builder.AppendLine("Every type/paste action must explicitly set action.replace. With replace=true, action.text is the complete desired field value. With replace=false, action.text is only the new text to insert/append, never the existing value plus the new text. Put the flag in action, not merely in evaluation. For non-writing actions set replace=false.");
        builder.AppendLine("If the user has already placed the cursor in a verified editor, paste may omit a target. The executor will use that active cursor only when the live helper confirms it is an editable surface; otherwise ask for the smallest necessary user action.");
        builder.AppendLine("Treat every independently verified field value, selected option, completed navigation, and date-range phase as a preserved checkpoint. Do not overwrite it, repeat it after a harmless rerender, or navigate back to an ancestor/home page unless fresh evidence proves the checkpoint is wrong.");
        builder.AppendLine("When the current URL and page state already contain the requested entities or parameters, continue from that progressed state and inspect the result; never restart the workflow merely because the page layout is unfamiliar.");
        builder.AppendLine("After a verified search result opens the requested target page, the search prerequisite is complete. A site-wide search field still visible on that target page is not a reason to submit the same query again; inspect the current page and finish the remaining goal.");
        builder.AppendLine("A retry must change strategy: use a different grounded control or interaction mechanism. Never repeat the same semantic action just because the graph fingerprint or DOM epoch changed.");
        builder.AppendLine("Never invent a final Enter, Submit, Send, Post, Delete, Join, Register, or Apply action. If the requested goals are already satisfied and the user did not request a final action, return done instead of proposing or asking permission for that extra action.");
        builder.AppendLine("Never clear or reset a form, its answers, or existing progress unless the original request explicitly asks for that reset. Correct one field through its editor rather than activating a page-wide clear/reset control.");
        builder.AppendLine("When a modal, dialog, listbox, date picker, or other overlay is open, finish or dismiss that visible layer with its exact Apply/Done/Save/Close control before using page-level controls behind it. A no-op behind an overlay means inspect the overlay; it does not justify restarting the workflow.");
        builder.AppendLine("For an explicit date, a context-free numeric day button is not grounded evidence of the requested month/year. Prefer a full accessible date label; otherwise navigate the calendar, type the date, or use one screenshot to correlate the visible month before clicking.");
        builder.AppendLine("Never invent a required date, personal detail, recipient, or other missing user input. When the page requires information that the original request and accepted task guidance do not provide, use needs_user_action and ask only for that missing value.");
        builder.AppendLine("For needs_user_action, put the exact missing information or manual step in userAction and explain why it blocks progress in userActionReason. Set userActionRequired=true only when the task cannot be completed correctly without it. Set userActionSkippable=true only when the page makes it safely optional; never mark login, security, irreversible confirmation, payment, or a required value as skippable. If uncertain, set both flags false.");
        builder.AppendLine("Task guidance and skip-policy entries in ACTION HISTORY are authoritative. If the user allowed unavailable optional information to be skipped, continue past fields that are demonstrably optional instead of asking for them. Never skip a required or safety-sensitive dependency, and never ask again for an optional dependency already recorded as skipped.");
        builder.AppendLine("A field write is provisional until the next fresh page observation still contains the requested value. If a date or other controlled input clears or reformats away after the page reacts, do not alternate between fields or repeat the same format; choose a different supported input mechanism or ask for the smallest necessary user action.");
        builder.AppendLine("For a multi-step source-to-document task, once a grounded summary/result is ready, put the task-scoped draft text in completionValue before navigating away. It is ephemeral for this one task only. A later paste action may leave action.text empty to use that draft. Do not mark done until the destination editor verifies insertion.");
        builder.AppendLine("copy_clipboard is terminal only when the user's requested destination is the clipboard. In a source-to-editor workflow it may prepare the draft, but you must then activate the recorded destination tab and paste into its verified editor.");
        builder.AppendLine("For a native select control whose actions include select_option, use select_option on that control and put the exact desired visible option in action.option.");
        builder.AppendLine("For a custom dropdown/listbox, click its closed control once, then on the fresh observation click the exact desired visible option. Never keep clicking the placeholder or repeat an unchanged option-menu action.");
        builder.AppendLine("For multi-question forms, complete questions grounded from text/controls first. Defer image-dependent questions and genuinely missing personal details instead of blocking other safe progress.");
        builder.AppendLine("When AUTHORITATIVE USER-PROVIDED context or CURRENT SELECTED TEXT is present, consume every supported value from it before requesting user input. Ask only for a genuinely missing required value; optional unknown values may be skipped only when the user or page policy allows it.");
        builder.AppendLine("Use vision only when a relevant visible control/image cannot be grounded from the controls. For an image question, set action.targetId to one live option/control belonging to that question so the helper can bring the exact question region into view before capture.");
        builder.AppendLine("Never handle passwords, OTP, CAPTCHA, payment, checkout, banking, or security challenges; use needs_user_action.");
        builder.AppendLine("Do not submit forms, send messages/posts, delete, register, apply, or place orders unless the original request explicitly asks for that final action.");
        builder.AppendLine("Add-to-cart is allowed, but stop before checkout.");
        builder.AppendLine("Set done only when the current live state is sufficient for deterministic verification.");
        builder.AppendLine("For read/summarize requests, put the user-ready result in resultSummary.");
        builder.AppendLine("For copy requests, choose copy_clipboard and put exactly the intended clipboard text in action.text.");
        builder.AppendLine();
        builder.AppendLine($"ORIGINAL REQUEST: {BrowserAutomationText.Truncate(request, 10000)}");
        builder.AppendLine($"CURRENT URL: {BrowserAutomationText.RedactUrlForProvider(page.Url)}");
        builder.AppendLine($"CURRENT TITLE: {BrowserAutomationText.Truncate(page.Title, 300)}");
        builder.AppendLine($"GRAPH: {page.GraphVersion} DOM EPOCH: {page.DomEpoch}");
        builder.AppendLine($"ACTIVE EDITOR: editable={page.ActiveEditable} role={Quote(page.ActiveEditorRole)} name={Quote(page.ActiveEditorName)} id={Quote(page.ActiveEditorId)}");
        builder.AppendLine("ACTION HISTORY:");
        foreach (var item in history.TakeLast(8))
            builder.AppendLine($"- {BrowserAutomationText.Truncate(item, 500)}");
        builder.AppendLine("RELEVANT PAGE TEXT:");
        builder.AppendLine(BrowserAutomationText.Truncate(page.PageText, 8500));
        if (!string.IsNullOrWhiteSpace(page.SelectedText))
        {
            builder.AppendLine("CURRENT SELECTED TEXT (fresh from the controlled page):");
            builder.AppendLine(BrowserAutomationText.Truncate(page.SelectedText, 6000));
        }
        builder.AppendLine("GROUNDED CONTROLS:");
        foreach (var control in controls)
        {
            builder.AppendLine(
                $"- id={control.Id} role={control.Role} name={Quote(control.AccessibleName)} "
                + $"text={Quote(control.Text)} label={Quote(control.NearbyLabel)} "
                + $"placeholder={Quote(control.Placeholder)} value={Quote(control.Value)} selected={control.Selected} "
                + $"checked={control.Checked} editable={control.Editable} actions=[{string.Join(",", control.SafeActions)}]");
            if (!string.IsNullOrWhiteSpace(control.FieldId))
                builder.AppendLine($"  fieldId={Quote(control.FieldId)} field={Quote(control.FieldLabel)} required={control.FieldRequired?.ToString() ?? "unknown"} answered={control.FieldAnswered}");
        }
        return builder.ToString();
    }

    internal static void ValidateDecision(BrowserPlannerDecision decision)
    {
        decision.Action ??= new BrowserPlannerAction();
        decision.Action.Type = (decision.Action.Type ?? "").Trim().ToLowerInvariant();
        if (decision.Action.Type is "none" or "noop" or "no_action")
        {
            if (string.IsNullOrWhiteSpace(decision.ResultSummary))
            {
                throw new InvalidDataException(
                    "The browser planner returned a no-op without a usable result.");
            }
            decision.Action.Type = "done";
        }
        if (!AllowedActions.Contains(decision.Action.Type))
        {
            throw new InvalidDataException(
                $"The browser planner returned an unsupported action: {decision.Action.Type}.");
        }
        if (decision.NeedsUserAction
            || decision.Action.Type == "needs_user_action")
        {
            decision.NeedsUserAction = true;
            decision.Action.Type = "needs_user_action";
        }
        else if (decision.Action.Type == "done")
        {
            decision.Done = true;
        }
        else
        {
            // Some models describe an action as the final required action and set
            // done=true in the same response. The action still has to execute and
            // be verified before the following observation may become terminal.
            decision.Done = false;
        }
        if (decision.Action.Type.Length == 0)
            throw new InvalidDataException("The browser planner returned no action.");
        if (decision.Confidence is < 0 or > 1)
            decision.Confidence = Math.Clamp(decision.Confidence, 0, 1);
    }

    private static IEnumerable<string> ModelCandidates(string configured)
    {
        return new[]
        {
            configured,
            "gemini-3.1-flash-lite-preview",
            "gemini-3-flash-preview",
            "gemini-2.5-flash",
        }.Where(model => !string.IsNullOrWhiteSpace(model))
         .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static object StringSchema() => new { type = "string" };

    private static string Quote(string value) =>
        JsonSerializer.Serialize(BrowserAutomationText.Truncate(value, 180));

    private static string SafeError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return BrowserAutomationText.Truncate(
                document.RootElement.GetProperty("error").GetProperty("message").GetString(),
                300);
        }
        catch
        {
            return "Provider request failed.";
        }
    }

    private static bool TryParseDataUrl(
        string value,
        out string mimeType,
        out string base64)
    {
        mimeType = "";
        base64 = "";
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        var separator = value.IndexOf(',');
        if (separator < 6)
            return false;
        var header = value[5..separator];
        var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts.Contains("base64", StringComparer.OrdinalIgnoreCase))
            return false;
        mimeType = parts[0];
        base64 = value[(separator + 1)..];
        return mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && base64.Length > 0;
    }

    public void Dispose() => _http.Dispose();
}
