namespace KeyboardWtf.Staging.Api.Configuration;

public static class DeploymentSafety
{
    public static bool Validate(PaidSystemOptions options, string? razorpayKeyId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateRazorpayKey(options.DeploymentEnvironment, razorpayKeyId);

        if (options.DeploymentEnvironment == PaidDeploymentEnvironment.Production)
        {
            if (options.AllowBootstrapCatalog)
                throw new InvalidOperationException("Production cannot use the bootstrap product catalog.");
            if (options.AllowBootstrapModelRouting)
                throw new InvalidOperationException("Production cannot use bootstrap model routing or pricing.");
            RejectStagingMarker(options.ProjectId, "project ID");
            RejectStagingMarker(options.WebBaseUrl, "website URL");
            foreach (var origin in options.AllowedOrigins)
                RejectStagingMarker(origin, "allowed origin");
        }

        return true;
    }

    public static string ProviderEnvironment(PaidDeploymentEnvironment environment) =>
        environment switch
        {
            PaidDeploymentEnvironment.Production => "production",
            PaidDeploymentEnvironment.Staging => "staging",
            PaidDeploymentEnvironment.Development => "development",
            _ => throw new InvalidOperationException("Unknown deployment environment."),
        };

    public static bool ValidateRequiredSecrets(
        PaidSystemOptions options,
        string? razorpayKeyId,
        string? razorpayKeySecret,
        string? razorpayWebhookSecret)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DeploymentEnvironment != PaidDeploymentEnvironment.Production)
            return true;
        if (string.IsNullOrWhiteSpace(razorpayKeyId)
            || string.IsNullOrWhiteSpace(razorpayKeySecret)
            || string.IsNullOrWhiteSpace(razorpayWebhookSecret))
            throw new InvalidOperationException("Production Razorpay credentials are incomplete.");
        return true;
    }

    private static void ValidateRazorpayKey(
        PaidDeploymentEnvironment environment,
        string? keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            return;

        var isTest = keyId.StartsWith("rzp_test_", StringComparison.Ordinal);
        var isLive = keyId.StartsWith("rzp_live_", StringComparison.Ordinal);
        if (!isTest && !isLive)
            throw new InvalidOperationException("The Razorpay key ID has an unknown environment prefix.");
        if (environment == PaidDeploymentEnvironment.Production && !isLive)
            throw new InvalidOperationException("Production requires a Razorpay Live key ID.");
        if (environment != PaidDeploymentEnvironment.Production && !isTest)
            throw new InvalidOperationException("A non-production environment cannot use a Razorpay Live key ID.");
    }

    private static void RejectStagingMarker(string value, string field)
    {
        if (value.Contains("staging", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Production {field} cannot reference staging.");
    }
}
