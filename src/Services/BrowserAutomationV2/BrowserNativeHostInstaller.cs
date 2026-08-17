namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Text.Json;
using Microsoft.Win32;

public sealed class BrowserNativeHostInstaller
{
    private static readonly string[] ExtensionFiles =
    {
        "manifest.json",
        "service-worker.js",
        "content-script.js",
        "popup.html",
        "popup.css",
        "popup.js",
        "README.md",
    };

    private static readonly string[] ExtensionDirectories =
    {
        "icons",
    };

    private const string RegistryPath =
        @"Software\Google\Chrome\NativeMessagingHosts\wtf.keyboard.browser";

    public string ManifestPath => Path.Combine(
        SettingsService.AppDataDir,
        "browser-helper",
        "wtf.keyboard.browser.json");

    public string StableExtensionDirectory => Path.Combine(
        SettingsService.AppDataDir,
        "browser-helper",
        "extension");

    public bool IsRegistered(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var path = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(path)
                || !File.Exists(path)
                || !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(ManifestPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var registeredOrigins = document.RootElement
                .GetProperty("allowed_origins")
                .EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return string.Equals(
                    document.RootElement.GetProperty("path").GetString(),
                    Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase)
                && HasExactAllowedOrigins(registeredOrigins);
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasExactAllowedOrigins(
        IEnumerable<string> registeredOrigins)
    {
        var actual = (registeredOrigins ?? Array.Empty<string>())
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return actual.SetEquals(BrowserHelperProtocol.AllowedExtensionOrigins);
    }

    public void Register(string executablePath)
    {
        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath))
            throw new FileNotFoundException("The keyboard.wtf executable was not found.", fullExecutablePath);

        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        var manifest = new
        {
            name = BrowserHelperProtocol.NativeHostName,
            description = "keyboard.wtf Chrome Helper bridge",
            path = fullExecutablePath,
            type = "stdio",
            allowed_origins = BrowserHelperProtocol.AllowedExtensionOrigins,
        };
        File.WriteAllText(
            ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not register the Chrome native messaging host.");
        key.SetValue(null, ManifestPath, RegistryValueKind.String);
    }

    public string ResolveExtensionSourceDirectory()
    {
        if (ExtensionVersionMatches(StableExtensionDirectory))
            return StableExtensionDirectory;

        return ResolvePackagedExtensionDirectory();
    }

    public string InstallExtension()
    {
        var source = ResolvePackagedExtensionDirectory();
        InstallExtension(source, StableExtensionDirectory);
        return StableExtensionDirectory;
    }

    internal static void InstallExtension(string source, string destination)
    {
        if (!ExtensionVersionMatches(source))
        {
            throw new InvalidDataException(
                $"The packaged Chrome Helper is missing or does not match package {BrowserHelperProtocol.PackageVersion} / protocol {BrowserHelperProtocol.Version}.");
        }

        Directory.CreateDirectory(destination);
        foreach (var file in ExtensionFiles)
        {
            var sourcePath = Path.Combine(source, file);
            if (!File.Exists(sourcePath))
                continue;
            File.Copy(sourcePath, Path.Combine(destination, file), overwrite: true);
        }

        foreach (var directory in ExtensionDirectories)
        {
            var sourcePath = Path.Combine(source, directory);
            if (!Directory.Exists(sourcePath))
                continue;
            CopyDirectory(sourcePath, Path.Combine(destination, directory));
        }

        if (!ExtensionVersionMatches(destination))
        {
            throw new InvalidDataException(
                $"The installed Chrome Helper does not match package {BrowserHelperProtocol.PackageVersion} / protocol {BrowserHelperProtocol.Version}.");
        }
    }

    internal static bool ExtensionVersionMatches(string directory)
    {
        try
        {
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, "manifest.json")));
            var version = manifest.RootElement.GetProperty("version").GetString();
            return string.Equals(
                    version,
                    BrowserHelperProtocol.PackageVersion,
                    StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(directory, "service-worker.js"))
                    .Contains(
                        $"const VERSION = \"{BrowserHelperProtocol.Version}\"",
                        StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(directory, "content-script.js"))
                    .Contains(
                        $"const VERSION = \"{BrowserHelperProtocol.Version}\"",
                        StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(
                file,
                Path.Combine(destination, Path.GetFileName(file)),
                overwrite: true);
        }

        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(
                child,
                Path.Combine(destination, Path.GetFileName(child)));
        }
    }

    private static string ResolvePackagedExtensionDirectory()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "browser-helper");
        if (ExtensionVersionMatches(packaged))
            return packaged;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "extensions",
                "keyboard-wtf-browser-helper-v2");
            if (ExtensionVersionMatches(candidate))
                return candidate;
        }

        return packaged;
    }
}
