using System.Security.Cryptography;
using System.Text.Json;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class ReleaseSignerTests
{
    [Fact]
    public async Task Release_signer_creates_a_manifest_that_verifies_with_the_derived_public_key()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kwt-release-signer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "keyboard-wtf-setup.exe");
            var notes = Path.Combine(root, "release-notes.txt");
            var privateKey = Path.Combine(root, "private.pem");
            var output = Path.Combine(root, "stable.json");
            await File.WriteAllBytesAsync(package, "signed installer fixture"u8.ToArray());
            await File.WriteAllTextAsync(notes, "Verified release fixture.");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(privateKey, key.ExportECPrivateKeyPem());

            var exitCode = await ReleaseSigner.RunAsync([
                "--package", package,
                "--output", output,
                "--private-key", privateKey,
                "--release-notes", notes,
                "--channel", "stable",
                "--version", "1.4.0",
                "--minimum-version", "1.3.0",
                "--published-at", "2027-01-15T12:00:00Z",
            ]);

            Assert.Equal(0, exitCode);
            var json = await File.ReadAllTextAsync(output);
            var manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(manifest);
            var verifier = new UpdateManifestVerifier(new Dictionary<string, string>
            {
                [UpdateManifestVerifier.CurrentKeyId] = key.ExportSubjectPublicKeyInfoPem(),
            });
            var verified = verifier.Verify(
                json,
                "stable",
                new Version(1, 3, 0),
                DateTimeOffset.Parse("2027-01-15T12:00:00Z"));
            Assert.Equal(new Version(1, 4, 0), verified.Version);
            Assert.Equal(new FileInfo(package).Length, verified.SizeBytes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
