namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Globalization;
using System.Text.RegularExpressions;

internal static class BrowserAutomationText
{
    private static readonly Regex SensitiveUrlParameter = new(
        @"^(?:access_?token|auth(?:entication|orization)?|code|credential|id_?token|api_?key|key|otp|passcode|password|refresh_?token|secret|session(?:_?id)?|sid|sig|signature|token)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Truncate(string value, int maxTextElements)
    {
        if (string.IsNullOrEmpty(value) || maxTextElements <= 0)
            return maxTextElements <= 0 ? "" : value ?? "";

        var indexes = StringInfo.ParseCombiningCharacters(value);
        if (indexes.Length <= maxTextElements)
            return value;

        return value[..indexes[maxTextElements]];
    }

    /// <summary>
    /// Keeps the page location useful for planning while preventing common
    /// credential-bearing query or fragment values from leaving the desktop
    /// app. The live executor retains the original URL locally.
    /// </summary>
    public static string RedactUrlForProvider(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return Truncate(value, 500);
        }

        var builder = new UriBuilder(uri)
        {
            Query = RedactParameterComponent(uri.Query.TrimStart('?')),
            Fragment = RedactParameterComponent(uri.Fragment.TrimStart('#')),
        };
        return Truncate(builder.Uri.AbsoluteUri, 500);
    }

    private static string RedactParameterComponent(string component)
    {
        if (string.IsNullOrWhiteSpace(component))
            return "";

        return string.Join(
            "&",
            component.Split('&', StringSplitOptions.None).Select(part =>
            {
                var equals = part.IndexOf('=');
                if (equals < 0)
                    return part;

                var encodedName = part[..equals];
                var decodedName = Uri.UnescapeDataString(
                    encodedName.Replace("+", " ", StringComparison.Ordinal));
                return SensitiveUrlParameter.IsMatch(decodedName)
                    ? $"{encodedName}=%5Bredacted%5D"
                    : part;
            }));
    }
}
