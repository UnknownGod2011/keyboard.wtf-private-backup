using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyboardWtf.Services.Updates;

public sealed record SignedUpdateManifest(
    int SchemaVersion,
    string Channel,
    string Version,
    string MinimumVersion,
    DateTimeOffset PublishedAt,
    string PackageUrl,
    string Sha256,
    long SizeBytes,
    string ReleaseNotes,
    string KeyId,
    string Signature);

public sealed record VerifiedUpdate(
    Version Version,
    Version MinimumVersion,
    string Channel,
    DateTimeOffset PublishedAt,
    Uri PackageUri,
    string Sha256,
    long SizeBytes,
    string ReleaseNotes,
    string KeyId);

public sealed class UpdateManifestVerifier
{
    public const string CurrentKeyId = "kwt-release-2026-01";
    public const long MaximumPackageBytes = 250L * 1024 * 1024;
    private const string CurrentPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkjZG4MMSbsmBkkaVf94urXncdXC+
        ce0ZbQF9v+Qe7WqRmAHDHiVumuWRKhZ9k5NhO4JiBKDMckBx9s4gw7FG4w==
        -----END PUBLIC KEY-----
        """;

    private readonly IReadOnlyDictionary<string, string> _publicKeys;
    private readonly string _expectedPackagePathPrefix;

    public UpdateManifestVerifier(
        IReadOnlyDictionary<string, string> publicKeys = null,
        string expectedPackagePathPrefix = "/UnknownGod2011/keyboard.wtf/releases/download/")
    {
        _publicKeys = publicKeys ?? new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CurrentKeyId] = CurrentPublicKeyPem,
        };
        _expectedPackagePathPrefix = expectedPackagePathPrefix;
    }

    public VerifiedUpdate Verify(
        string manifestJson,
        string expectedChannel,
        Version currentVersion,
        DateTimeOffset now)
    {
        if (Encoding.UTF8.GetByteCount(manifestJson) > 128 * 1024)
            throw new InvalidDataException("The update manifest is too large.");
        var manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidDataException("The update manifest is empty.");
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException("The update manifest schema is unsupported.");
        if (string.IsNullOrWhiteSpace(manifest.Channel)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.MinimumVersion)
            || string.IsNullOrWhiteSpace(manifest.PackageUrl)
            || string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.ReleaseNotes is null
            || string.IsNullOrWhiteSpace(manifest.KeyId)
            || string.IsNullOrWhiteSpace(manifest.Signature))
            throw new InvalidDataException("The update manifest is missing required signed fields.");
        if (!string.Equals(manifest.Channel, expectedChannel, StringComparison.OrdinalIgnoreCase)
            || manifest.Channel is not ("stable" or "beta"))
            throw new InvalidDataException("The update channel is invalid.");
        if (!Version.TryParse(manifest.Version, out var version)
            || !Version.TryParse(manifest.MinimumVersion, out var minimumVersion))
            throw new InvalidDataException("The update version is invalid.");
        if (version <= currentVersion)
            throw new InvalidDataException("The manifest is a replay or downgrade.");
        if (minimumVersion > currentVersion)
            throw new InvalidDataException("This update requires an unsupported upgrade path.");
        if (manifest.PublishedAt > now.AddHours(24))
            throw new InvalidDataException("The update publication time is outside the trusted window.");
        if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var packageUri)
            || packageUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(packageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !packageUri.AbsolutePath.StartsWith(_expectedPackagePathPrefix, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(packageUri.UserInfo)
            || !packageUri.IsDefaultPort)
            throw new InvalidDataException("The update package URL is not an approved release URL.");
        if (manifest.SizeBytes is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException("The update package size is invalid.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The update package hash is invalid.");
        if (manifest.ReleaseNotes.Length > 20_000)
            throw new InvalidDataException("The release notes are too large.");
        if (!_publicKeys.TryGetValue(manifest.KeyId, out var publicKeyPem))
            throw new InvalidDataException("The update signing key is unknown or revoked.");
        byte[] signature;
        try { signature = Convert.FromBase64String(manifest.Signature); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The update signature is invalid.", exception);
        }
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(publicKeyPem);
        if (!publicKey.VerifyData(
                CanonicalPayload(manifest),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("The update manifest signature is invalid.");
        return new VerifiedUpdate(
            version,
            minimumVersion,
            manifest.Channel.ToLowerInvariant(),
            manifest.PublishedAt,
            packageUri,
            manifest.Sha256.ToLowerInvariant(),
            manifest.SizeBytes,
            manifest.ReleaseNotes,
            manifest.KeyId);
    }

    public static byte[] CanonicalPayload(SignedUpdateManifest manifest)
    {
        var notesHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ReleaseNotes)))
            .ToLowerInvariant();
        return Encoding.UTF8.GetBytes(string.Join('\n',
            manifest.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            manifest.Channel.Trim().ToLowerInvariant(),
            manifest.Version.Trim(),
            manifest.MinimumVersion.Trim(),
            manifest.PublishedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            manifest.PackageUrl.Trim(),
            manifest.Sha256.Trim().ToLowerInvariant(),
            manifest.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            notesHash,
            manifest.KeyId.Trim()));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };
}
