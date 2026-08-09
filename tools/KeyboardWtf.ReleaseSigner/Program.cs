using System.Security.Cryptography;
using System.Text.Json;
using KeyboardWtf.Services.Updates;

return await ReleaseSigner.RunAsync(args);

public static class ReleaseSigner
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            var package = Path.GetFullPath(Required(options, "package"));
            var output = Path.GetFullPath(Required(options, "output"));
            var privateKeyPath = Path.GetFullPath(Required(options, "private-key"));
            var releaseNotesPath = Path.GetFullPath(Required(options, "release-notes"));
            var channel = Required(options, "channel").Trim().ToLowerInvariant();
            var versionText = Required(options, "version").Trim();
            var minimumVersionText = Required(options, "minimum-version").Trim();
            var keyId = options.GetValueOrDefault("key-id")?.Trim()
                ?? UpdateManifestVerifier.CurrentKeyId;

            if (channel is not ("stable" or "beta"))
                throw new ArgumentException("--channel must be stable or beta.");
            if (!Version.TryParse(versionText, out var version)
                || !Version.TryParse(minimumVersionText, out var minimumVersion)
                || version <= minimumVersion)
                throw new ArgumentException("The release version must be newer than the minimum version.");
            if (!File.Exists(package) || !string.Equals(Path.GetExtension(package), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("The release package must be an existing Windows installer.", package);
            if (!File.Exists(privateKeyPath))
                throw new FileNotFoundException("The release signing key was not found.", privateKeyPath);
            if (!File.Exists(releaseNotesPath))
                throw new FileNotFoundException("The release notes file was not found.", releaseNotesPath);
            EnsurePrivateKeyOutsideRepository(privateKeyPath);

            var packageInfo = new FileInfo(package);
            if (packageInfo.Length is <= 0 or > UpdateManifestVerifier.MaximumPackageBytes)
                throw new InvalidDataException("The installer size is outside the updater's signed limits.");
            var releaseNotes = await File.ReadAllTextAsync(releaseNotesPath);
            if (releaseNotes.Length > 20_000)
                throw new InvalidDataException("Release notes exceed the signed manifest limit.");
            await using var packageStream = File.OpenRead(package);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream)).ToLowerInvariant();
            var packageUrl =
                $"https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v{versionText}/{Uri.EscapeDataString(packageInfo.Name)}";
            var publishedAt = options.TryGetValue("published-at", out var timestamp)
                ? DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture)
                : DateTimeOffset.UtcNow;
            var unsigned = new SignedUpdateManifest(
                1,
                channel,
                versionText,
                minimumVersionText,
                publishedAt,
                packageUrl,
                hash,
                packageInfo.Length,
                releaseNotes,
                keyId,
                "");

            using var signingKey = ECDsa.Create();
            signingKey.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
            var signed = unsigned with
            {
                Signature = Convert.ToBase64String(signingKey.SignData(
                    UpdateManifestVerifier.CanonicalPayload(unsigned),
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            };

            // Verify with the derived public key before writing anything. This
            // catches a wrong key, malformed manifest, or canonicalization drift.
            var verifier = new UpdateManifestVerifier(new Dictionary<string, string>
            {
                [keyId] = signingKey.ExportSubjectPublicKeyInfoPem(),
            });
            verifier.Verify(JsonSerializer.Serialize(signed, OutputJson), channel, minimumVersion, publishedAt);

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var temporary = output + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(signed, OutputJson) + Environment.NewLine);
            File.Move(temporary, output, true);
            Console.WriteLine($"Signed {channel} update manifest for {versionText}.");
            Console.WriteLine($"Manifest: {output}");
            Console.WriteLine($"Installer SHA-256: {hash}");
            Console.WriteLine($"Signing key ID: {keyId}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Release manifest was not created: {exception.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Arguments must be supplied as --name value pairs.");
            result[args[index][2..]] = args[index + 1];
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required --{name} argument.");

    private static void EnsurePrivateKeyOutsideRepository(string privateKeyPath)
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null
               && !Directory.Exists(Path.Combine(current.FullName, ".git"))
               && !File.Exists(Path.Combine(current.FullName, ".git")))
            current = current.Parent;
        if (current is null)
            return;
        var root = current.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (privateKeyPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release signing private key must remain outside the repository.");
    }
}
