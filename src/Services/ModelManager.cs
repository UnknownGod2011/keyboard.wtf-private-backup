namespace KeyboardWtf.Services;

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using KeyboardWtf.Models;

public static class ModelManager
{
    private const string VoskModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
    private const string VoskModelFolder = "vosk-model";
    private const string VoskModelMarker = "README";
    private const long VoskMinimumArchiveBytes = 30L * 1024 * 1024;

    private const string WhisperModelFolder = "whisper-model";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly object _whisperValidationLock = new();
    private static WhisperValidationCache _whisperValidationCache;

    private sealed record WhisperValidationCache(
        string Path,
        long Length,
        DateTime LastWriteUtc,
        WhisperModelSize Size,
        bool IsValid);

    public static string ModelsBaseDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "keyboard.wtf", "models");

    public static string VoskModelPath => Path.Combine(ModelsBaseDir, VoskModelFolder);

    public static string WhisperModelPath =>
        Path.Combine(ModelsBaseDir, WhisperModelFolder, GetWhisperFileName(KeyboardWtfState.SelectedWhisperModel));

    public static bool IsVoskPresent => Directory.Exists(VoskModelPath)
        && File.Exists(Path.Combine(VoskModelPath, VoskModelMarker))
        && Directory.Exists(Path.Combine(VoskModelPath, "am"))
        && Directory.Exists(Path.Combine(VoskModelPath, "conf"))
        && Directory.Exists(Path.Combine(VoskModelPath, "graph"))
        && Directory.Exists(Path.Combine(VoskModelPath, "ivector"));

    public static bool IsWhisperPresent => IsWhisperModelValid(WhisperModelPath, KeyboardWtfState.SelectedWhisperModel);

    public static string GetWhisperFileName(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => "ggml-base.bin",
        WhisperModelSize.Small => "ggml-small.bin",
        _ => "ggml-small.bin"
    };

    private static string GetWhisperDownloadUrl(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        WhisperModelSize.Small => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        _ => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"
    };

    private static string GetWhisperSizeLabel(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => "~140 MB",
        WhisperModelSize.Small => "~460 MB",
        _ => "~460 MB"
    };

    private static long GetWhisperMinimumBytes(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Base => 100L * 1024 * 1024,
        WhisperModelSize.Small => 300L * 1024 * 1024,
        _ => 300L * 1024 * 1024
    };

    private static string GetWhisperSha1(WhisperModelSize size) => size switch
    {
        // Published by the upstream whisper.cpp model inventory.
        WhisperModelSize.Base => "465707469ff3a37a2b9b8d8f89f2f99de7299dac",
        WhisperModelSize.Small => "55356645c2b361a969dfd0ef2c5a50d530afd8d5",
        _ => "55356645c2b361a969dfd0ef2c5a50d530afd8d5",
    };

    private static bool IsWhisperModelValid(string path, WhisperModelSize size)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            var file = new FileInfo(path);
            if (file.Length < GetWhisperMinimumBytes(size))
                return false;

            lock (_whisperValidationLock)
            {
                if (_whisperValidationCache is not null
                    && string.Equals(_whisperValidationCache.Path, path, StringComparison.OrdinalIgnoreCase)
                    && _whisperValidationCache.Length == file.Length
                    && _whisperValidationCache.LastWriteUtc == file.LastWriteTimeUtc
                    && _whisperValidationCache.Size == size)
                    return _whisperValidationCache.IsValid;
            }

            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA1.HashData(stream));
            var isValid = string.Equals(
                actual,
                GetWhisperSha1(size),
                StringComparison.OrdinalIgnoreCase);
            lock (_whisperValidationLock)
            {
                _whisperValidationCache = new WhisperValidationCache(
                    path,
                    file.Length,
                    file.LastWriteTimeUtc,
                    size,
                    isValid);
            }
            return isValid;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Path.Combine(ModelsBaseDir, VoskModelFolder));
        Directory.CreateDirectory(Path.Combine(ModelsBaseDir, WhisperModelFolder));
    }

    public static async Task<bool> DownloadVoskModelAsync(Action<string> onProgress = null)
    {
        if (IsVoskPresent)
        {
            AppLog.Info("Vosk model already present");
            return true;
        }

        try
        {
            EnsureDirectories();
            var zipPath = Path.Combine(ModelsBaseDir, "vosk-model-download.zip");

            onProgress?.Invoke("Downloading Vosk model (~40 MB)...");
            AppLog.Info($"Downloading Vosk model from {VoskModelUrl}");

            using (var response = await _http.GetAsync(VoskModelUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength
                    && contentLength < VoskMinimumArchiveBytes)
                    throw new InvalidDataException($"Vosk download was too small ({contentLength} bytes)");
                await DownloadResponseToFileAsync(
                    response,
                    zipPath,
                    "Vosk model",
                    onProgress);
            }

            if (new FileInfo(zipPath).Length < VoskMinimumArchiveBytes)
                throw new InvalidDataException("Vosk download did not produce a complete model archive");

            onProgress?.Invoke("Extracting Vosk model...");
            AppLog.Info("Extracting Vosk model");

            var tempExtract = Path.Combine(ModelsBaseDir, "vosk-extract-temp");
            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, true);

            ZipFile.ExtractToDirectory(zipPath, tempExtract);

            var extractedDirs = Directory.GetDirectories(tempExtract);
            var sourceDir = extractedDirs.Length > 0 ? extractedDirs[0] : tempExtract;

            if (Directory.Exists(VoskModelPath))
                Directory.Delete(VoskModelPath, true);

            Directory.Move(sourceDir, VoskModelPath);

            if (!IsVoskPresent)
                throw new InvalidDataException("Vosk download did not contain the required model structure");

            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, true);
            File.Delete(zipPath);

            AppLog.Info($"Vosk model installed at: {VoskModelPath}");
            onProgress?.Invoke("Vosk model ready");
            return true;
        }
        catch (Exception ex)
        {
            var zipPath = Path.Combine(ModelsBaseDir, "vosk-model-download.zip");
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            AppLog.Error($"Vosk model download failed: {ex.Message}");
            onProgress?.Invoke($"Download failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> DownloadWhisperModelAsync(Action<string> onProgress = null)
    {
        if (IsWhisperPresent)
        {
            AppLog.Info($"Whisper model already present: {WhisperModelPath}");
            return true;
        }

        var size = KeyboardWtfState.SelectedWhisperModel;
        var url = GetWhisperDownloadUrl(size);
        var sizeLabel = GetWhisperSizeLabel(size);

        try
        {
            EnsureDirectories();

            onProgress?.Invoke($"Downloading Whisper {size} model ({sizeLabel})...");
            AppLog.Info($"Downloading Whisper {size} model from {url}");

            var tempPath = WhisperModelPath + ".download";
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength
                    && contentLength < GetWhisperMinimumBytes(size))
                    throw new InvalidDataException($"Whisper download was too small ({contentLength} bytes)");

                await DownloadResponseToFileAsync(
                    response,
                    tempPath,
                    $"Whisper {size}",
                    onProgress);
            }

            if (!IsWhisperModelValid(tempPath, size))
                throw new InvalidDataException($"Whisper download failed the upstream {size} model checksum");

            File.Move(tempPath, WhisperModelPath, true);
            AppLog.Info($"Whisper model installed at: {WhisperModelPath}");
            onProgress?.Invoke("Whisper model ready");
            return true;
        }
        catch (Exception ex)
        {
            var tempPath = WhisperModelPath + ".download";
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            AppLog.Error($"Whisper model download failed: {ex.Message}");
            onProgress?.Invoke($"Download failed: {ex.Message}");
            return false;
        }
    }

    public static async Task EnsureModelsAsync(Action<string> onProgress = null)
    {
        EnsureDirectories();

        if (!IsVoskPresent && !await DownloadVoskModelAsync(onProgress))
            throw new InvalidOperationException(
                "The Vosk speech model could not be downloaded. Check the connection and retry.");

        if (!IsWhisperPresent && !await DownloadWhisperModelAsync(onProgress))
            throw new InvalidOperationException(
                "The Whisper speech model could not be downloaded. Check the connection and retry.");
    }

    private static async Task DownloadResponseToFileAsync(
        HttpResponseMessage response,
        string path,
        string label,
        Action<string> onProgress)
    {
        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var destination = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var lastReportedPercent = -5;
        long lastReportedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read));
            received += read;

            if (totalBytes is > 0)
            {
                var percent = (int)Math.Min(100, received * 100 / totalBytes.Value);
                if (percent >= lastReportedPercent + 5)
                {
                    lastReportedPercent = percent;
                    onProgress?.Invoke($"Downloading {label}... {percent}%");
                }
            }
            else if (received - lastReportedBytes >= 5L * 1024 * 1024)
            {
                lastReportedBytes = received;
                onProgress?.Invoke($"Downloading {label}... {received / (1024 * 1024)} MB");
            }
        }
        await destination.FlushAsync();
    }
}
