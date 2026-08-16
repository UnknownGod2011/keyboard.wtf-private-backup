using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace KeyboardWtf.Services.Updates;

public sealed class SecureUpdateService : IDisposable
{
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };
    private readonly HttpClient _http;
    private readonly UpdateManifestVerifier _verifier;
    private readonly string _downloadRoot;
    private readonly TimeSpan _manifestTimeout;
    private readonly TimeSpan _downloadTimeout;

    public SecureUpdateService(
        HttpMessageHandler handler = null,
        UpdateManifestVerifier verifier = null,
        string downloadRoot = null,
        TimeSpan? manifestTimeout = null,
        TimeSpan? downloadTimeout = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _verifier = verifier ?? new UpdateManifestVerifier();
        _downloadRoot = downloadRoot ?? Path.Combine(SettingsService.AppDataDir, "updates");
        _manifestTimeout = manifestTimeout ?? TimeSpan.FromSeconds(20);
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(15);
    }

    public async Task<VerifiedUpdate> CheckAsync(string channel, CancellationToken cancellationToken)
    {
        var normalized = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";
        var manifestUri = new Uri($"https://keyboardwtf.com/releases/{normalized}.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_manifestTimeout);
        using var response = await _http.GetAsync(
            manifestUri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 128 * 1024)
            throw new InvalidDataException("The update manifest is too large.");
        var json = await ReadBoundedUtf8Async(response.Content, 128 * 1024, timeout.Token);
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        try { return _verifier.Verify(json, normalized, current, DateTimeOffset.UtcNow); }
        catch (InvalidDataException exception) when (exception.Message.Contains("replay or downgrade", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<string> DownloadAsync(VerifiedUpdate update, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_downloadRoot, update.Version.ToString());
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "keyboard.wtf-setup.exe");
        var partial = destination + ".part";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_downloadTimeout);
            using var response = await GetWithValidatedRedirectsAsync(update.PackageUri, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared
                && declared != update.SizeBytes)
                throw new InvalidDataException("The update package size does not match its signed manifest.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            {
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var target = new FileStream(
                    partial,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    var read = await source.ReadAsync(buffer, timeout.Token);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > update.SizeBytes || total > UpdateManifestVerifier.MaximumPackageBytes)
                        throw new InvalidDataException("The update package exceeded its signed size.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                await target.FlushAsync(timeout.Token);
            }
            if (total != update.SizeBytes)
                throw new InvalidDataException("The update package is incomplete.");
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException("The update package hash is invalid.");
            File.Move(partial, destination, true);
            return destination;
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetWithValidatedRedirectsAsync(
        Uri initial,
        CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            ValidateDownloadUri(current);
            var response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently
                or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new InvalidDataException("The update redirect has no destination.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new InvalidDataException("The update package used too many redirects.");
    }

    private static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !DownloadHosts.Contains(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort)
            throw new InvalidDataException("The update download left the approved release hosts.");
    }

    private static async Task<string> ReadBoundedUtf8Async(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("The update manifest is too large.");
            buffer.Write(chunk, 0, read);
        }

        return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public void Dispose() => _http.Dispose();
}
