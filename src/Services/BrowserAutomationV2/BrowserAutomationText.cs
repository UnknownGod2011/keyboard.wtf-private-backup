namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Globalization;

internal static class BrowserAutomationText
{
    public static string Truncate(string value, int maxTextElements)
    {
        if (string.IsNullOrEmpty(value) || maxTextElements <= 0)
            return maxTextElements <= 0 ? "" : value ?? "";

        var indexes = StringInfo.ParseCombiningCharacters(value);
        if (indexes.Length <= maxTextElements)
            return value;

        return value[..indexes[maxTextElements]];
    }
}
