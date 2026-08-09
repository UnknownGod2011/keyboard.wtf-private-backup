using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Configuration;

public static class ClientCompatibility
{
    public const string DesktopVersionHeader = "X-Keyboard-Wtf-Desktop-Version";

    public static void Require(string? suppliedVersion, string minimumVersion)
    {
        if (!Version.TryParse(minimumVersion, out var minimum))
            throw new InvalidOperationException("The server model route has an invalid minimum desktop version.");
        if (!Version.TryParse(suppliedVersion, out var supplied))
            throw new ClientUpgradeRequiredException(minimumVersion);
        if (supplied < minimum)
            throw new ClientUpgradeRequiredException(minimumVersion);
    }
}
