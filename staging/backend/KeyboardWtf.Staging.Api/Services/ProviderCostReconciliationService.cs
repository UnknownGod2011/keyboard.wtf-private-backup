using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public sealed record ProviderBillingCostSnapshot(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    long ProviderCostUsdMicros);

public interface IProviderBillingCostSource
{
    Task<ProviderBillingCostSnapshot> ReadAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);
}

internal sealed class BigQueryProviderBillingCostSource : IProviderBillingCostSource
{
    private static readonly Regex TablePattern = new(
        "^[a-z][a-z0-9-]{4,61}[a-z0-9]\\.[A-Za-z_][A-Za-z0-9_]*\\.[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HttpClient _http;
    private readonly IVertexAccessTokenProvider _tokens;
    private readonly PaidSystemOptions _options;

    public BigQueryProviderBillingCostSource(
        HttpClient http,
        IVertexAccessTokenProvider tokens,
        IOptions<PaidSystemOptions> options)
    {
        _http = http;
        _tokens = tokens;
        _options = options.Value;
        _http.Timeout = TimeSpan.FromSeconds(45);
    }

    internal static bool IsValidTable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && TablePattern.IsMatch(value);

    internal static string BuildQuery(string table) =>
        $"""
        SELECT CAST(ROUND(COALESCE(SUM(
          (cost + COALESCE((SELECT SUM(credit.amount) FROM UNNEST(credits) credit), 0))
          / NULLIF(currency_conversion_rate, 0)
        ), 0) * 1000000) AS INT64) AS provider_cost_usd_micros
        FROM `{table}`
        WHERE usage_start_time >= @window_start
          AND usage_start_time < @window_end
          AND project.id = @project_id
          AND service.description = 'Vertex AI'
        """;

    public async Task<ProviderBillingCostSnapshot> ReadAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        if (!IsValidTable(_options.BillingExportTable))
            throw new InvalidOperationException("The billing export table is not configured safely.");
        var payload = new
        {
            query = BuildQuery(_options.BillingExportTable),
            useLegacySql = false,
            timeoutMs = 30_000,
            maximumBytesBilled = _options.BillingReconciliationMaximumBytesBilled.ToString(CultureInfo.InvariantCulture),
            parameterMode = "NAMED",
            queryParameters = new object[]
            {
                TimestampParameter("window_start", windowStart),
                TimestampParameter("window_end", windowEnd),
                StringParameter("project_id", _options.ProjectId),
            },
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://bigquery.googleapis.com/bigquery/v2/projects/{Uri.EscapeDataString(_options.ProjectId)}/queries");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await _tokens.GetAsync(cancellationToken));
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"BigQuery billing reconciliation failed ({(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("jobComplete", out var complete) || !complete.GetBoolean())
            throw new InvalidOperationException("BigQuery billing reconciliation did not complete in the bounded query window.");
        var value = root.TryGetProperty("rows", out var rows)
            && rows.ValueKind == JsonValueKind.Array
            && rows.GetArrayLength() > 0
            && rows[0].TryGetProperty("f", out var fields)
            && fields.GetArrayLength() > 0
            && fields[0].TryGetProperty("v", out var scalar)
            ? scalar.GetString()
            : "0";
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cost)
            || cost < 0)
            throw new InvalidOperationException("BigQuery returned an invalid provider cost aggregate.");
        return new ProviderBillingCostSnapshot(windowStart, windowEnd, cost);
    }

    private static object TimestampParameter(string name, DateTimeOffset value) => new
    {
        name,
        parameterType = new { type = "TIMESTAMP" },
        parameterValue = new { value = value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) },
    };

    private static object StringParameter(string name, string value) => new
    {
        name,
        parameterType = new { type = "STRING" },
        parameterValue = new { value },
    };
}

public sealed record ProviderCostReconciliationResult(
    string RunId,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    long InternalProviderCostUsdMicros,
    long BilledProviderCostUsdMicros,
    long DifferenceUsdMicros,
    int UsageRecords,
    string Status);

public sealed class ProviderCostReconciliationService
{
    private readonly FirestoreDb _db;
    private readonly IProviderBillingCostSource _billing;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;
    private readonly FirestoreMaintenanceLease _leases;
    private readonly ILogger<ProviderCostReconciliationService> _logger;

    public ProviderCostReconciliationService(
        FirestoreDb db,
        IProviderBillingCostSource billing,
        IClock clock,
        IOptions<PaidSystemOptions> options,
        ILogger<ProviderCostReconciliationService> logger)
    {
        _db = db;
        _billing = billing;
        _clock = clock;
        _options = options.Value;
        _leases = new FirestoreMaintenanceLease(db, clock);
        _logger = logger;
    }

    public async Task<ProviderCostReconciliationResult?> RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.ProviderCostReconciliationEnabled)
        {
            _logger.LogInformation("Provider cost reconciliation is disabled.");
            return null;
        }
        const string job = "provider-cost-reconciliation";
        var now = _clock.UtcNow;
        var windowStart = new DateTimeOffset(
            now.UtcDateTime.Date.AddDays(-_options.BillingReconciliationLagDays),
            TimeSpan.Zero);
        var windowEnd = windowStart.AddDays(1);
        var runId = $"provider_cost_{windowStart:yyyyMMdd}_{now:yyyyMMddHHmmss}_{Crypto.RandomToken(4)}";
        await using var lease = await _leases.TryAcquireAsync(
            job,
            TimeSpan.FromSeconds(_options.ReconciliationLeaseSeconds),
            cancellationToken);
        if (lease is null)
            return null;

        var (internalCost, usageRecords, complete) = await ReadInternalCostAsync(
            windowStart,
            windowEnd,
            cancellationToken);
        if (!complete)
        {
            var incomplete = new ProviderCostReconciliationResult(
                runId, windowStart, windowEnd, internalCost, 0, 0, usageRecords, "scan_incomplete");
            await WriteRunAsync(incomplete, cancellationToken);
            _logger.LogWarning(
                "Provider cost reconciliation scan incomplete records={Records}",
                usageRecords);
            return incomplete;
        }

        var billed = await _billing.ReadAsync(windowStart, windowEnd, cancellationToken);
        var difference = Math.Abs(internalCost - billed.ProviderCostUsdMicros);
        var material = IsMaterialVariance(
            internalCost,
            billed.ProviderCostUsdMicros,
            _options.ProviderCostVarianceAbsoluteUsdMicros,
            _options.ProviderCostVarianceBasisPoints);
        var result = new ProviderCostReconciliationResult(
            runId,
            windowStart,
            windowEnd,
            internalCost,
            billed.ProviderCostUsdMicros,
            difference,
            usageRecords,
            material ? "attention_required" : "completed");
        await WriteRunAsync(result, cancellationToken);
        await UpdateIssueAsync(result, material, cancellationToken);
        if (material)
            _logger.LogWarning(
                "Provider cost drift detected window={Window} internalUsdMicros={Internal} billedUsdMicros={Billed} differenceUsdMicros={Difference}",
                windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                internalCost,
                billed.ProviderCostUsdMicros,
                difference);
        else
            _logger.LogInformation(
                "Provider cost reconciliation completed window={Window} records={Records}",
                windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                usageRecords);
        return result;
    }

    internal static bool IsMaterialVariance(
        long internalCost,
        long billedCost,
        long absoluteThreshold,
        int basisPointThreshold)
    {
        var difference = Math.Abs((decimal)internalCost - billedCost);
        if (difference < absoluteThreshold)
            return false;
        var baseline = Math.Max(1m, Math.Max(internalCost, billedCost));
        return difference * 10_000m / baseline >= basisPointThreshold;
    }

    private async Task<(long Cost, int Records, bool Complete)> ReadInternalCostAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        if (windowEnd - windowStart != TimeSpan.FromDays(1))
            throw new InvalidOperationException("Provider cost reconciliation requires one closed UTC day.");
        var date = windowStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var shardReads = Enumerable.Range(0, _options.GlobalUsageShardCount)
            .Select(index => _db.Collection("globalUsageShards")
                .Document($"{date}-{index:D3}")
                .GetSnapshotAsync(cancellationToken));
        var shards = await Task.WhenAll(shardReads);
        var legacy = await _db.Collection("globalUsage").Document(date)
            .GetSnapshotAsync(cancellationToken);
        long cost = 0;
        var records = 0;
        foreach (var snapshot in shards.Append(legacy))
        {
            if (!snapshot.Exists)
                continue;
            var data = snapshot.ToDictionary();
            var providerCost = ReadLong(data, "providerCostUsdMicros");
            var settlementCount = ReadLong(data, "providerCostSettlementCount");
            if (providerCost < 0 || settlementCount < 0 || settlementCount > int.MaxValue)
                throw new InvalidOperationException("Internal provider cost aggregate is invalid.");
            cost = checked(cost + providerCost);
            records = checked(records + (int)settlementCount);
        }
        return (cost, records, true);
    }

    private Task WriteRunAsync(
        ProviderCostReconciliationResult result,
        CancellationToken cancellationToken) =>
        _db.Collection("providerCostReconciliationRuns").Document(result.RunId).CreateAsync(
            new Dictionary<string, object>
            {
                ["job"] = "provider-cost-reconciliation",
                ["windowStart"] = Timestamp.FromDateTime(result.WindowStart.UtcDateTime),
                ["windowEnd"] = Timestamp.FromDateTime(result.WindowEnd.UtcDateTime),
                ["internalProviderCostUsdMicros"] = result.InternalProviderCostUsdMicros,
                ["billedProviderCostUsdMicros"] = result.BilledProviderCostUsdMicros,
                ["differenceUsdMicros"] = result.DifferenceUsdMicros,
                ["usageRecords"] = result.UsageRecords,
                ["status"] = result.Status,
                ["createdAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
            },
            cancellationToken);

    private async Task UpdateIssueAsync(
        ProviderCostReconciliationResult result,
        bool material,
        CancellationToken cancellationToken)
    {
        var issue = _db.Collection("providerCostIssues")
            .Document(Crypto.Sha256($"provider-cost:{result.WindowStart:yyyyMMdd}"));
        if (material)
        {
            await AuditIssueStore.UpsertOpenAsync(
                _db,
                "providerCostIssues",
                issue.Id,
                new Dictionary<string, object>
                {
                    ["category"] = "provider_cost_drift",
                    ["windowStart"] = Timestamp.FromDateTime(result.WindowStart.UtcDateTime),
                    ["lastRunId"] = result.RunId,
                    ["differenceUsdMicros"] = result.DifferenceUsdMicros,
                },
                _clock.UtcNow,
                cancellationToken);
            return;
        }
        var existing = await issue.GetSnapshotAsync(cancellationToken);
        if (existing.Exists && string.Equals(
                Convert.ToString(existing.ToDictionary().GetValueOrDefault("status")),
                "open",
                StringComparison.Ordinal))
            await issue.SetAsync(
                new Dictionary<string, object>
                {
                    ["status"] = "resolved",
                    ["resolvedAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
                    ["lastRunId"] = result.RunId,
                },
                SetOptions.MergeAll,
                cancellationToken);
    }

    private static long ReadLong(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;
}
