namespace KeyboardWtf.Staging.Api.Services;

public sealed class MaintenanceRunner
{
    private readonly PaymentReconciliationService _payments;
    private readonly UsageReconciliationService _usage;
    private readonly LedgerInvariantService _invariants;
    private readonly OperationalHealthService _health;
    private readonly ProviderCostReconciliationService _providerCosts;
    private readonly ILogger<MaintenanceRunner> _logger;

    public MaintenanceRunner(
        PaymentReconciliationService payments,
        UsageReconciliationService usage,
        LedgerInvariantService invariants,
        OperationalHealthService health,
        ProviderCostReconciliationService providerCosts,
        ILogger<MaintenanceRunner> logger)
    {
        _payments = payments;
        _usage = usage;
        _invariants = invariants;
        _health = health;
        _providerCosts = providerCosts;
        _logger = logger;
    }

    public async Task RunAsync(string job, CancellationToken cancellationToken)
    {
        switch (job.Trim().ToLowerInvariant())
        {
            case "payments":
                await _payments.RunAsync(cancellationToken);
                break;
            case "usage":
                await _usage.RunAsync(cancellationToken);
                break;
            case "invariants":
                await _invariants.RunAsync(cancellationToken);
                break;
            case "provider-cost":
                await _providerCosts.RunAsync(cancellationToken);
                break;
            case "all":
                await _payments.RunAsync(cancellationToken);
                await _usage.RunAsync(cancellationToken);
                await _invariants.RunAsync(cancellationToken);
                await _providerCosts.RunAsync(cancellationToken);
                break;
            default:
                throw new ArgumentException("Unknown maintenance job. Use payments, usage, invariants, provider-cost, or all.");
        }
        await _health.CheckAsync(cancellationToken);
        _logger.LogInformation("Maintenance command completed job={Job}", job);
    }

    internal static string? ParseJob(IEnumerable<string> args)
    {
        const string prefix = "--maintenance-job=";
        return args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
    }
}
