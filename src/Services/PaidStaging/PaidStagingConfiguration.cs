namespace KeyboardWtf.Services.PaidStaging;

internal sealed record PaidStagingConfiguration(
    bool Enabled,
    bool EmergencyByokEnabled,
    Uri BackendBaseUri,
    Uri WebBaseUri)
{
    public static PaidStagingConfiguration FromEnvironment()
    {
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
        return new PaidStagingConfiguration(enabled, emergencyByok, backend, web);
    }

    private static bool IsTrue(string value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static Uri ParseBaseUri(string value, string name, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
                throw new InvalidOperationException($"{name} is required when paid staging is enabled.");
            return null;
        }
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw new InvalidOperationException($"{name} must be an HTTPS or loopback URL.");
        return uri;
    }
}
