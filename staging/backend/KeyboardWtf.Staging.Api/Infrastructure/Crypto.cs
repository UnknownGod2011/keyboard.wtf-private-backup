using System.Security.Cryptography;
using System.Text;

namespace KeyboardWtf.Staging.Api.Infrastructure;

public static class Crypto
{
    public static string RandomToken(int bytes = 32)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        return Base64Url(data);
    }

    public static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string PkceChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static bool FixedTimeHexEquals(string expectedHex, string actualHex)
    {
        if (!TryHex(expectedHex, out var expected) || !TryHex(actualHex, out var actual))
            return false;
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string HmacSha256Hex(string secret, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)))
            .ToLowerInvariant();
    }

    public static bool IsSafeLoopbackCallback(string callbackUri)
    {
        if (!Uri.TryCreate(callbackUri, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttp
            && (string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            && uri.Port is >= 1024 and <= 65535
            && string.Equals(uri.AbsolutePath, "/keyboard-wtf/callback", StringComparison.Ordinal);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryHex(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
