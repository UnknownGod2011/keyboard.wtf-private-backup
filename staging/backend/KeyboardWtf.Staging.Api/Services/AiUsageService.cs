using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text.Json;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class AiUsageService
{
    private readonly IAccountStore _store;
    private readonly ICatalogService _catalog;
    private readonly IModelRoutingService _routing;
    private readonly IVertexAiClient _vertex;
    private readonly PaidSystemOptions _options;
    private readonly ILogger<AiUsageService> _logger;

    public AiUsageService(
        IAccountStore store,
        ICatalogService catalog,
        IModelRoutingService routing,
        IVertexAiClient vertex,
        IOptions<PaidSystemOptions> options,
        ILogger<AiUsageService>? logger = null)
    {
        _store = store;
        _catalog = catalog;
        _routing = routing;
        _vertex = vertex;
        _options = options.Value;
        _logger = logger ?? NullLogger<AiUsageService>.Instance;
    }

    public async Task<GenerateResponse> GenerateAsync(
        CallerIdentity caller,
        GenerateRequest request,
        CancellationToken cancellationToken)
        => await GenerateAsync(caller, request, "1.3.0", cancellationToken);

    public async Task<GenerateResponse> GenerateAsync(
        CallerIdentity caller,
        GenerateRequest request,
        string? desktopVersion,
        CancellationToken cancellationToken)
    {
        var requestStarted = Stopwatch.GetTimestamp();
        if (!_options.Enabled)
            throw new InvalidOperationException("Paid AI is temporarily disabled.");
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 200)
            throw new ArgumentException("A stable request ID is required.");
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 200_000)
            throw new ArgumentException("The prompt is empty or too large.");
        if (request.SystemInstruction?.Length > 20_000)
            throw new ArgumentException("The system instruction is too large.");
        if (request.MaxOutputTokens is < 1 or > 8192)
            throw new ArgumentException("MaxOutputTokens must be between 1 and 8192.");
        if (request.TaskKind?.Length > 100)
            throw new ArgumentException("The task kind is too large.");
        if (request.ResponseJsonSchema is JsonElement schema
            && JsonSerializer.SerializeToUtf8Bytes(schema).Length > 50_000)
            throw new ArgumentException("The response schema is too large.");
        ValidateImages(request.Images);

        var configurationStarted = Stopwatch.GetTimestamp();
        var catalogTask = _catalog.GetAsync(cancellationToken);
        var routeTask = _routing.ResolveAsync(
            request.TaskKind,
            $"{caller.UserId}:{request.RequestId}",
            cancellationToken);
        await Task.WhenAll(catalogTask, routeTask);
        var catalog = await catalogTask;
        if (catalog.KillSwitch && !CatalogService.IsAiCanary(_options, caller.Email))
            throw new InvalidOperationException(catalog.KillSwitchMessage ?? "Paid AI is temporarily unavailable.");
        var route = await routeTask;
        var configurationMs = Stopwatch.GetElapsedTime(configurationStarted).TotalMilliseconds;
        ClientCompatibility.Require(desktopVersion, route.MinimumDesktopVersion);
        if (request.Prompt.Length > route.MaxInputCharacters)
            throw new ArgumentException($"The prompt exceeds the server limit for route '{route.Name}'.");
        if (request.MaxOutputTokens > route.MaxOutputTokens)
            throw new ArgumentException($"The output limit exceeds the server limit for route '{route.Name}'.");
        // The desktop cannot lower or enlarge the authoritative reservation.
        // A modified client-controlled budget would either weaken accounting
        // or turn normal requests into confusing artificial failures.
        var taskCap = _options.DefaultTaskCreditMicrosCap;
        // Reserve conservatively for visual inputs without persisting or logging image bytes.
        // 16K synthetic characters per image approximates a 4K-token visual observation.
        var reservationPrompt = request.Images is { Count: > 0 }
            ? request.Prompt + new string('x', request.Images.Count * 16_384)
            : request.Prompt;
        var reserve = _catalog.EstimateReservationCreditMicros(
            route.Candidates[0].Pricing,
            reservationPrompt,
            request.MaxOutputTokens,
            taskCap);
        var reservationStarted = Stopwatch.GetTimestamp();
        var reservation = await _store.ReserveAsync(
            caller,
            request.RequestId,
            route.Candidates[0].Model,
            route.Candidates[0].Pricing.Version,
            reserve,
            taskCap,
            _options.MaxRequestsPerMinutePerUser,
            _options.MaxConcurrentRequestsPerUser,
            _options.GlobalDailyCreditMicrosCap,
            cancellationToken,
            route.Name);
        var reservationMs = Stopwatch.GetElapsedTime(reservationStarted).TotalMilliseconds;
        if (!reservation.Created)
            throw new InvalidOperationException("This request ID has already been used.");

        VertexGenerateResult generated;
        ModelCandidateSnapshot selectedCandidate;
        var providerStarted = Stopwatch.GetTimestamp();
        double providerMs;
        try
        {
            await _store.MarkReservationProviderStartedAsync(
                caller.UserId,
                request.RequestId,
                cancellationToken);
            generated = null!;
            selectedCandidate = null!;
            VertexRequestRejectedException? rejected = null;
            for (var index = 0; index < route.Candidates.Count; index++)
            {
                var candidate = route.Candidates[index];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(
                    Math.Min(_options.MaxRequestSeconds, route.TimeoutSeconds)));
                try
                {
                    generated = await _vertex.GenerateAsync(
                        candidate.Model,
                        candidate.Location,
                        request.Prompt,
                        request.SystemInstruction,
                        request.MaxOutputTokens,
                        request.ResponseJsonSchema,
                        request.Images,
                        timeout.Token);
                    selectedCandidate = candidate;
                    rejected = null;
                    break;
                }
                catch (VertexRequestRejectedException exception)
                    when (index + 1 < route.Candidates.Count && IsSafeProviderFallback(exception.StatusCode))
                {
                    // Google currently documents failed HTTP 400 and 500
                    // inference responses as non-billable. Only those
                    // definitive rejections may use a second server-configured
                    // candidate; every other post-dispatch failure remains
                    // financially ambiguous and must not fan out.
                    rejected = exception;
                }
            }
            if (rejected is not null || selectedCandidate is null)
                throw rejected ?? new InvalidOperationException("No server model candidate completed the request.");
        }
        catch (VertexRequestRejectedException exception)
            when (IsDefinitivelyNonBillable(exception.StatusCode))
        {
            await _store.ReleaseReservationAsync(
                caller.UserId,
                request.RequestId,
                "provider_rejected",
                CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            // Once a request may have reached Vertex, a timeout, disconnect, or
            // cancellation is financially ambiguous. Preserve the reservation
            // for reconciliation instead of issuing an unsafe automatic refund.
            await _store.MarkReservationForReconciliationAsync(
                caller.UserId,
                request.RequestId,
                cancellationToken.IsCancellationRequested
                    ? "cancelled_after_provider_dispatch"
                    : "provider_result_ambiguous",
                CancellationToken.None);
            throw;
        }

        providerMs = Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds;

        // Provider work has completed. From this point onward accounting must
        // finish even if the caller disconnects or cancels the local request.
        var (providerCost, charge) = _catalog.CalculateActualCost(selectedCandidate.Pricing, generated.Usage);
        var outcome = string.IsNullOrWhiteSpace(generated.Text)
            ? "provider_completed_no_usable_output"
            : "completed";
        var evidenceStarted = Stopwatch.GetTimestamp();
        try
        {
            // Persist authoritative provider evidence before changing balances.
            // A maintenance worker can now finish settlement after a crash or
            // client disconnect without guessing whether provider work occurred.
            await _store.RecordProviderCompletionAsync(
                caller.UserId,
                request.RequestId,
                generated.Usage,
                charge,
                providerCost,
                outcome,
                CancellationToken.None,
                selectedCandidate.Model,
                selectedCandidate.Pricing.Version);
        }
        catch (Exception exception)
        {
            await _store.MarkReservationForReconciliationAsync(
                caller.UserId,
                request.RequestId,
                "provider_completed_evidence_write_failed",
                CancellationToken.None);
            throw new InvalidOperationException(
                "Usage was generated but its durable accounting evidence is pending reconciliation.",
                exception);
        }
        var evidenceMs = Stopwatch.GetElapsedTime(evidenceStarted).TotalMilliseconds;
        var settlementStarted = Stopwatch.GetTimestamp();
        SettlementResult? settled = null;
        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                settled = await _store.SettleAsync(
                    caller.UserId,
                    request.RequestId,
                    generated.Usage,
                    charge,
                    providerCost,
                    outcome,
                    CancellationToken.None);
                break;
            }
            catch when (attempt < _options.MaxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), CancellationToken.None);
            }
        }

        if (settled is null)
            throw new InvalidOperationException("Usage was generated but its accounting settlement is pending reconciliation.");
        var settlementMs = Stopwatch.GetElapsedTime(settlementStarted).TotalMilliseconds;
        _logger.LogInformation(
            "Paid AI timing route={Route} configMs={ConfigurationMs:F1} reservationMs={ReservationMs:F1} providerMs={ProviderMs:F1} evidenceMs={EvidenceMs:F1} settlementMs={SettlementMs:F1} totalMs={TotalMs:F1}",
            route.Name,
            configurationMs,
            reservationMs,
            providerMs,
            evidenceMs,
            settlementMs,
            Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds);
        if (string.IsNullOrWhiteSpace(generated.Text))
            throw new InvalidOperationException("Vertex AI returned no usable response; any reported provider usage was still accounted for.");
        return new GenerateResponse(
            request.RequestId,
            generated.Text,
            selectedCandidate.Model,
            settled.ChargedCreditMicros,
            settled.Account.AvailableCreditMicros,
            new
            {
                generated.Usage.InputTokens,
                generated.Usage.OutputTokens,
                generated.Usage.CachedInputTokens,
                generated.Usage.ReasoningTokens,
                generated.Usage.ImageTokens,
                generated.Usage.AudioTokens,
                generated.Usage.TotalTokens,
                generated.Usage.ProviderModelVersion,
                Route = route.Name,
                RouteVersion = route.Version,
                PricingVersion = selectedCandidate.Pricing.Version,
            },
            true);
    }

    internal static bool IsSafeProviderFallback(int statusCode) =>
        // Retry only failures for which a different server-owned model can
        // plausibly help. Authentication/authorization rejections are also
        // non-billable, but retrying them against another model is pointless.
        statusCode is 400 or 404 or 408 or 409 or 429 or 500 or 502 or 503 or 504;

    internal static bool IsDefinitivelyNonBillable(int statusCode) =>
        // Vertex pricing explicitly states that only successful HTTP 200
        // inference responses are billed. VertexRequestRejectedException is
        // created only after a complete provider HTTP response, so all of its
        // non-success HTTP statuses are definitive and safe to release. Network
        // failures and timeouts never enter this branch and remain quarantined.
        statusCode is >= 300 and <= 599;

    private static void ValidateImages(IReadOnlyList<InlineImageRequest>? images)
    {
        if (images is null || images.Count == 0)
            return;
        if (images.Count > 2)
            throw new ArgumentException("At most two task images are allowed.");
        long totalBytes = 0;
        foreach (var image in images)
        {
            if (image.MimeType is not ("image/jpeg" or "image/png" or "image/webp"))
                throw new ArgumentException("Only JPEG, PNG, or WebP task images are allowed.");
            if (string.IsNullOrWhiteSpace(image.Data) || image.Data.Length > 12_000_000)
                throw new ArgumentException("A task image is empty or too large.");
            try
            {
                totalBytes += Convert.FromBase64String(image.Data).LongLength;
            }
            catch (FormatException)
            {
                throw new ArgumentException("A task image is not valid base64.");
            }
        }
        if (totalBytes > 8 * 1024 * 1024)
            throw new ArgumentException("Task images exceed the 8 MB request limit.");
    }
}
