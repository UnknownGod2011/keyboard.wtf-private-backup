namespace KeyboardWtf.Staging.Api.Services;

public sealed class MaintenanceRunner
{
    private readonly PaymentReconciliationService _payments;
    private readonly UsageReconciliationService _usage;
    private readonly LedgerInvariantService _invariants;
    private readonly OperationalHealthService _health;
    private readonly ILogger<MaintenanceRunner> _logger;

    public MaintenanceRunner(
        PaymentReconciliationService payments,
        UsageReconciliationService usage,
        LedgerInvariantService invariants,
        OperationalHealthService health,
        ILogger<MaintenanceRunner> logger)
    {
        _payments = payments;
        _usage = usage;
        _invariants = invariants;
        _health = health;
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
            case "all":
                await _payments.RunAsync(cancellationToken);
                await _usage.RunAsync(cancellationToken);
                await _invariants.RunAsync(cancellationToken);
                break;
            default:
                throw new ArgumentException("Unknown maintenance job. Use payments, usage, invariants, or all.");
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
