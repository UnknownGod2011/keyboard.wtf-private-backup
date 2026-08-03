namespace KeyboardWtf.Services.PaidStaging;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record PaidSessionTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string SessionId,
    string DeviceId,
    string DeviceInstallationId);

internal sealed class PaidSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("keyboard.wtf/paid-session/v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public PaidSessionStore(string path = null) =>
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(SettingsService.AppDataDir, "paid-staging-session.dat")
            : path;

    public void Save(PaidSessionTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Paid desktop sessions require Windows DPAPI.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonOptions);
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, _path, true);
    }

    public PaidSessionTokens Load()
    {
        if (!File.Exists(_path))
            return null;
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<PaidSessionTokens>(plaintext, JsonOptions);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException)
        {
            AppLog.Warning(exception, "Paid session could not be restored; local credentials were cleared");
            Clear();
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
            if (File.Exists(_path + ".tmp"))
                File.Delete(_path + ".tmp");
        }
        catch (IOException exception)
        {
            AppLog.Warning(exception, "Paid session cleanup failed");
        }
    }
}
