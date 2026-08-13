namespace KeyboardWtf.Services.PaidStaging;

internal sealed record PaidStagingConfiguration(
    bool Enabled,
    bool EmergencyByokEnabled,
    Uri BackendBaseUri,
    Uri WebBaseUri,
    string DeploymentEnvironment = "staging",
    bool PreserveExistingRegistrations = false)
{
    private static readonly Uri ProductionBackendBaseUri =
        new("https://keyboard-wtf-paid-api-mhrc6c7goa-el.a.run.app/");
    private static readonly Uri ProductionWebBaseUri =
        new("https://keyboard-wtf.vercel.app/");

    public bool IsProduction =>
        string.Equals(DeploymentEnvironment, "production", StringComparison.Ordinal);

    public static PaidStagingConfiguration FromEnvironment()
    {
#if KEYBOARD_WTF_PAID_PRODUCTION
        return Production();
#else
        var enabled = IsTrue(Environment.GetEnvironmentVariable("KEYBOARD_WTF_PAID_STAGING"));
        var emergencyByok = IsTrue(Environment.GetEnvironmentVariable("KEYBOARD_WTF_BYOK_EMERGENCY"));
        var backend = ParseBaseUri(
            Environment.GetEnvironmentVariable("KEYBOARD_WTF_PAID_BACKEND_URL"),
            "KEYBOARD_WTF_PAID_BACKEND_URL",
            enabled);
        var web = ParseBaseUri(
            Environment.GetEnvironmentVariable("KEYBOARD_WTF_PAID_WEB_URL"),
            "KEYBOARD_WTF_PAID_WEB_URL",
            enabled);
        return new PaidStagingConfiguration(
            enabled,
            emergencyByok,
            backend,
            web,
            enabled ? "staging" : "disabled",
            PreserveExistingRegistrations: enabled);
#endif
    }

    public static PaidStagingConfiguration Production() =>
        new(
            Enabled: true,
            EmergencyByokEnabled: false,
            BackendBaseUri: ProductionBackendBaseUri,
            WebBaseUri: ProductionWebBaseUri,
            DeploymentEnvironment: "production",
            PreserveExistingRegistrations: IsSideBySideCandidateBuild);

    private static bool IsSideBySideCandidateBuild
    {
        get
        {
#if KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE
            return true;
#else
            return false;
#endif
        }
    }

    private static bool IsTrue(string value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static Uri ParseBaseUri(string value, string name, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
                throw new InvalidOperationException($"{name} is required when paid mode is enabled.");
            return null;
        }
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw new InvalidOperationException($"{name} must be an HTTPS or loopback URL.");
        return uri;
    }
}
