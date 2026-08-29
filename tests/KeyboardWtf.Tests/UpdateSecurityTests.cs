using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class UpdateSecurityTests
{
    [Fact]
    public void Signed_manifest_is_accepted_and_all_signed_fields_are_bound()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = VerifierFor(key);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var manifest = Sign(Manifest(now), key);

        var result = verifier.Verify(JsonSerializer.Serialize(manifest), "stable", new Version(1, 3, 0), now);

        Assert.Equal(new Version(1, 4, 0), result.Version);
        Assert.Equal(UpdateManifestVerifier.CurrentKeyId, result.KeyId);
        var tampered = manifest with { ReleaseNotes = manifest.ReleaseNotes + " injected" };
        Assert.Throws<InvalidDataException>(() => verifier.Verify(
            JsonSerializer.Serialize(tampered), "stable", new Version(1, 3, 0), now));
    }

    [Fact]
    public void Unknown_key_wrong_channel_replay_and_downgrade_are_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = VerifierFor(key);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var manifest = Sign(Manifest(now), key);

        Assert.Throws<InvalidDataException>(() => verifier.Verify(
            JsonSerializer.Serialize(manifest with { KeyId = "unknown" }),
            "stable", new Version(1, 3, 0), now));
        Assert.Throws<InvalidDataException>(() => verifier.Verify(
            JsonSerializer.Serialize(manifest), "beta", new Version(1, 3, 0), now));
        Assert.Throws<InvalidDataException>(() => verifier.Verify(
            JsonSerializer.Serialize(manifest), "stable", new Version(1, 4, 0), now));
        Assert.Throws<InvalidDataException>(() => verifier.Verify(
            JsonSerializer.Serialize(Sign(manifest with { Version = "1.2.0" }, key)),
            "stable", new Version(1, 3, 0), now));
    }

    [Fact]
    public void Valid_latest_release_remains_installable_after_a_long_offline_period()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = VerifierFor(key);
        var published = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var manifest = Sign(Manifest(published), key);

        var result = verifier.Verify(
            JsonSerializer.Serialize(manifest),
            "stable",
            new Version(1, 3, 0),
            published.AddYears(2));

        Assert.Equal(new Version(1, 4, 0), result.Version);
    }

    [Fact]
    public void Missing_signed_fields_are_rejected_as_invalid_data()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = VerifierFor(key);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var manifest = Sign(Manifest(now), key) with { ReleaseNotes = null! };

        Assert.Throws<InvalidDataException>(() => verifier.Verify(
            JsonSerializer.Serialize(manifest), "stable", new Version(1, 3, 0), now));
    }

    [Fact]
    public async Task Package_download_requires_the_signed_size_and_hash()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kwt-update-test-{Guid.NewGuid():N}");
        try
        {
            var bytes = Encoding.UTF8.GetBytes("verified installer fixture");
            var update = UpdateFor(bytes);
            using var service = new SecureUpdateService(
                new StaticResponseHandler(bytes),
                downloadRoot: root);

            var path = await service.DownloadAsync(update, CancellationToken.None);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            using var corruptService = new SecureUpdateService(
                new StaticResponseHandler(Encoding.UTF8.GetBytes("tampered installer fixture")),
                downloadRoot: root);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                corruptService.DownloadAsync(update with { Version = new Version(1, 4, 1) }, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Manifest_without_content_length_is_rejected_at_the_byte_limit()
    {
        var oversized = new byte[(128 * 1024) + 1];
        Array.Fill(oversized, (byte)'x');
        using var service = new SecureUpdateService(new ChunkedResponseHandler(oversized));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CheckAsync("stable", CancellationToken.None));
    }

    [Fact]
    public void Published_stable_manifest_is_signed_by_the_embedded_release_key()
    {
        var manifestPath = Path.Combine(
            FindRepositoryRoot(),
            "production",
            "site",
            "releases",
            "stable.json");
        var json = File.ReadAllText(manifestPath);

        var update = new UpdateManifestVerifier().Verify(
            json,
            "stable",
            new Version(1, 2, 0),
            DateTimeOffset.UtcNow);

        Assert.Equal(new Version(1, 4, 17), update.Version);
        Assert.Equal(new Version(1, 2, 0), update.MinimumVersion);
        Assert.Equal(
            "812aaa434a4d6b618764b044a9a5f96b5dc9b85830ce1f205c5b6ad582ded6cd",
            update.Sha256);
        Assert.Equal(84_251_836, update.SizeBytes);
        Assert.Equal(UpdateManifestVerifier.CurrentKeyId, update.KeyId);
    }

    private static UpdateManifestVerifier VerifierFor(ECDsa key) => new(
        new Dictionary<string, string>
        {
            [UpdateManifestVerifier.CurrentKeyId] = key.ExportSubjectPublicKeyInfoPem(),
        });

    private static SignedUpdateManifest Manifest(DateTimeOffset now) => new(
        1,
        "stable",
        "1.4.0",
        "1.3.0",
        now,
        "https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v1.4.0/keyboard.wtf-setup.exe",
        new string('a', 64),
        1024,
        "Security and reliability improvements.",
        UpdateManifestVerifier.CurrentKeyId,
        "");

    private static SignedUpdateManifest Sign(SignedUpdateManifest manifest, ECDsa key) =>
        manifest with
        {
            Signature = Convert.ToBase64String(key.SignData(
                UpdateManifestVerifier.CanonicalPayload(manifest),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        };

    private static VerifiedUpdate UpdateFor(byte[] bytes) => new(
        new Version(1, 4, 0),
        new Version(1, 3, 0),
        "stable",
        DateTimeOffset.UtcNow,
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v1.4.0/keyboard.wtf-setup.exe"),
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        bytes.Length,
        "Fixture",
        UpdateManifestVerifier.CurrentKeyId);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                && Directory.Exists(Path.Combine(directory.FullName, "production")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class StaticResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
    }


    private sealed class ChunkedResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StreamContent(new MemoryStream(bytes, writable: false));
            content.Headers.ContentLength = null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
