param(
    [string]$BaseUrl = "https://keyboard-wtf-paid-api-689128038682.asia-south1.run.app",
    [ValidateRange(1, 5000)]
    [int]$RequestCount = 500,
    [ValidateRange(1, 200)]
    [int]$Concurrency = 100,
    [string]$OutputPath = ".artifacts\paid-staging-load-http.json"
)

$ErrorActionPreference = "Stop"
$uri = [Uri]$BaseUrl
if ($uri.Scheme -ne "https" -or $uri.Host -notmatch '^keyboard-wtf-paid-api-[a-z0-9-]+\.(asia-south1\.run\.app|a\.run\.app)$') {
    throw "The load smoke test is restricted to the paid staging Cloud Run service."
}

if (-not ("KeyboardWtfStagingHttpLoad" -as [type])) {
    Add-Type -Language CSharp -ReferencedAssemblies "System.Net.Http.dll" -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public static class KeyboardWtfStagingHttpLoad
{
    public sealed class Result
    {
        public int Requests { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public double DurationMs { get; set; }
        public double RequestsPerSecond { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public double MaxMs { get; set; }
    }

    public static async Task<Result> RunAsync(string baseUrl, int requestCount, int concurrency)
    {
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
        using (var gate = new SemaphoreSlim(concurrency, concurrency))
        {
            var durations = new ConcurrentBag<double>();
            var succeeded = 0;
            var failed = 0;
            var overall = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, requestCount).Select(async ordinal =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        var path = ordinal % 5 == 0 ? "/health/ready" : "/health/live";
                        using (var response = await client.GetAsync(baseUrl.TrimEnd('/') + path).ConfigureAwait(false))
                        {
                            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                                Interlocked.Increment(ref succeeded);
                            else
                                Interlocked.Increment(ref failed);
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        timer.Stop();
                        durations.Add(timer.Elapsed.TotalMilliseconds);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            overall.Stop();
            var ordered = durations.OrderBy(value => value).ToArray();
            Func<double, double> percentile = fraction => ordered.Length == 0
                ? 0
                : ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * fraction) - 1)];
            return new Result
            {
                Requests = requestCount,
                Succeeded = succeeded,
                Failed = failed,
                DurationMs = overall.Elapsed.TotalMilliseconds,
                RequestsPerSecond = overall.Elapsed.TotalSeconds <= 0 ? 0 : requestCount / overall.Elapsed.TotalSeconds,
                P50Ms = percentile(0.50),
                P95Ms = percentile(0.95),
                P99Ms = percentile(0.99),
                MaxMs = ordered.Length == 0 ? 0 : ordered[ordered.Length - 1],
            };
        }
    }
}
'@
}

$result = [KeyboardWtfStagingHttpLoad]::RunAsync($uri.GetLeftPart([UriPartial]::Authority), $RequestCount, $Concurrency).GetAwaiter().GetResult()
$report = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    target = $uri.GetLeftPart([UriPartial]::Authority)
    requestCount = $result.Requests
    concurrency = $Concurrency
    succeeded = $result.Succeeded
    failed = $result.Failed
    successRate = if ($result.Requests -eq 0) { 0 } else { $result.Succeeded / $result.Requests }
    durationMs = [Math]::Round($result.DurationMs, 2)
    requestsPerSecond = [Math]::Round($result.RequestsPerSecond, 2)
    p50Ms = [Math]::Round($result.P50Ms, 2)
    p95Ms = [Math]::Round($result.P95Ms, 2)
    p99Ms = [Math]::Round($result.P99Ms, 2)
    maxMs = [Math]::Round($result.MaxMs, 2)
    scope = "Unauthenticated liveness/readiness infrastructure smoke; no Vertex or payment calls."
}

$fullOutput = Join-Path (Get-Location) $OutputPath
New-Item -ItemType Directory -Path (Split-Path -Parent $fullOutput) -Force | Out-Null
$report | ConvertTo-Json | Set-Content -LiteralPath $fullOutput -Encoding utf8
$report | Format-List
if ($result.Failed -ne 0) {
    throw "The staging HTTP load smoke test recorded $($result.Failed) failed requests."
}
