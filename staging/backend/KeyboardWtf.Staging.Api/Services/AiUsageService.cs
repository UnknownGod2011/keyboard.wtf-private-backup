using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class AiUsageService
{
    private readonly IAccountStore _store;
    private readonly ICatalogService _catalog;
    private readonly IVertexAiClient _vertex;
    private readonly PaidSystemOptions _options;

    public AiUsageService(
        IAccountStore store,
        ICatalogService catalog,
        IVertexAiClient vertex,
        IOptions<PaidSystemOptions> options)
    {
        _store = store;
        _catalog = catalog;
        _vertex = vertex;
        _options = options.Value;
    }

    public async Task<GenerateResponse> GenerateAsync(
        CallerIdentity caller,
        GenerateRequest request,
        CancellationToken cancellationToken)
    {
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

        var catalog = await _catalog.GetAsync(cancellationToken);
        if (catalog.KillSwitch)
            throw new InvalidOperationException(catalog.KillSwitchMessage ?? "Paid AI is temporarily unavailable.");
        var taskCap = Math.Clamp(
            request.MaxCreditMicros ?? _options.DefaultTaskCreditMicrosCap,
            1,
            _options.DefaultTaskCreditMicrosCap);
        // Reserve conservatively for visual inputs without persisting or logging image bytes.
        // 16K synthetic characters per image approximates a 4K-token visual observation.
        var reservationPrompt = request.Images is { Count: > 0 }
            ? request.Prompt + new string('x', request.Images.Count * 16_384)
            : request.Prompt;
        var reserve = _catalog.EstimateReservationCreditMicros(
            catalog.Pricing,
            reservationPrompt,
            request.MaxOutputTokens,
            taskCap);
        var reservation = await _store.ReserveAsync(
            caller,
            request.RequestId,
            catalog.Pricing.Model,
            catalog.Pricing.Version,
            reserve,
            taskCap,
            _options.MaxRequestsPerMinutePerUser,
            _options.MaxConcurrentRequestsPerUser,
            _options.GlobalDailyCreditMicrosCap,
            cancellationToken);
        if (!reservation.Created)
            throw new InvalidOperationException("This request ID has already been used.");

        VertexGenerateResult generated;
        try
        {
            await _store.MarkReservationProviderStartedAsync(
                caller.UserId,
                request.RequestId,
                cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.MaxRequestSeconds));
            generated = await _vertex.GenerateAsync(
                catalog.Pricing.Model,
                request.Prompt,
                request.SystemInstruction,
                request.MaxOutputTokens,
                request.ResponseJsonSchema,
                request.Images,
                timeout.Token);
        }
        catch (VertexRequestRejectedException)
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

        // Provider work has completed. From this point onward accounting must
        // finish even if the caller disconnects or cancels the local request.
        var (providerCost, charge) = _catalog.CalculateActualCost(catalog.Pricing, generated.Usage);
        var outcome = string.IsNullOrWhiteSpace(generated.Text)
            ? "provider_completed_no_usable_output"
            : "completed";
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
        if (string.IsNullOrWhiteSpace(generated.Text))
            throw new InvalidOperationException("Vertex AI returned no usable response; any reported provider usage was still accounted for.");
        return new GenerateResponse(
            request.RequestId,
            generated.Text,
            catalog.Pricing.Model,
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
                PricingVersion = catalog.Pricing.Version,
            },
            true);
    }

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
