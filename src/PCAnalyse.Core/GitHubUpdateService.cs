using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PCAnalyse.Core;

public static class GitHubUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<AppUpdateInfo> CheckForUpdateAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        var result = new AppUpdateInfo();
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(AppInfo.GitHubLatestReleaseApiUrl, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "Noch kein GitHub-Release vorhanden."
                    : $"GitHub-Release konnte nicht gelesen werden ({(int)response.StatusCode}).";
                return result;
            }

            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);
            result.ReleaseTag = release?.TagName?.Trim() ?? "";
            result.ReleaseNotes = release?.Body ?? "";
            if (!TryParseReleaseVersion(result.ReleaseTag, out var remote))
            {
                result.ErrorMessage = "Release-Version konnte nicht gelesen werden: " + result.ReleaseTag;
                return result;
            }

            result.RemoteVersion = remote;
            result.UpdateAvailable = remote > AppInfo.ApplicationVersion;
            if (!result.UpdateAvailable)
                return result;

            var asset = release?.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, channel.AssetFileName, StringComparison.OrdinalIgnoreCase));
            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                result.UpdateAvailable = false;
                result.ErrorMessage = "Kein Update-Paket (" + channel.AssetFileName + ") in der Release gefunden.";
                return result;
            }

            result.DownloadUrl = asset.BrowserDownloadUrl;
            result.AssetId = asset.Id;
            result.AssetName = asset.Name ?? channel.AssetFileName;
            result.AssetSizeBytes = asset.Size;
            result.ExpectedSha256 = ExtractSha256(result.ReleaseNotes, result.AssetName);
            if (string.IsNullOrWhiteSpace(result.ExpectedSha256))
            {
                result.UpdateAvailable = false;
                result.ErrorMessage = "Release enthält keine SHA256-Prüfsumme für " + result.AssetName + ".";
            }
        }
        catch (TaskCanceledException)
        {
            result.ErrorMessage = "Update-Prüfung hat zu lange gedauert.";
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public static async Task<string> DownloadAndStageUpdateAsync(
        UpdateChannel channel,
        AppUpdateInfo update,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            throw new InvalidOperationException("Kein Update-Download verfügbar.");
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri) || !IsTrustedDownloadUrl(uri))
            throw new InvalidOperationException("Unsicherer Download-Link blockiert.");
        if (string.IsNullOrWhiteSpace(update.ExpectedSha256))
            throw new InvalidOperationException("Release enthält keine SHA256-Prüfsumme.");

        Directory.CreateDirectory(AppInfo.UpdateRoot);
        var packagePath = Path.Combine(AppInfo.UpdateRoot, SanitizeFileName(update.AssetName));
        var extractPath = Path.Combine(AppInfo.UpdateRoot, "staging-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        if (Directory.Exists(extractPath))
            Directory.Delete(extractPath, true);

        Report(progress, 4, "Update wird heruntergeladen…");
        using (var client = CreateClient())
        using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(packagePath);
            await CopyWithProgressAsync(
                stream, file,
                response.Content.Headers.ContentLength ?? (update.AssetSizeBytes > 0 ? update.AssetSizeBytes : null),
                5, 75, "Update wird heruntergeladen…", progress, cancellationToken);
        }

        Report(progress, 78, "Prüfsumme wird verifiziert…");
        var actual = ComputeSha256Hex(packagePath);
        if (!string.Equals(actual, update.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(packagePath);
            throw new InvalidOperationException(
                "Update-Prüfsumme ungültig.\nErwartet: " + update.ExpectedSha256 + "\nErhalten: " + actual);
        }

        Report(progress, 82, "Update wird entpackt…");
        ExtractZip(packagePath, extractPath, progress, cancellationToken);
        var stagedRoot = FindApplicationRoot(extractPath, channel.ExeFileName);
        if (stagedRoot is null)
            throw new InvalidOperationException("Update-Paket enthält keine gültige " + channel.ExeFileName + ".");

        Report(progress, 100, "Download abgeschlossen.");
        return stagedRoot;
    }

    public static void LaunchUpdaterAndShutdown(UpdateChannel channel, string stagedAppRoot)
    {
        stagedAppRoot = Path.GetFullPath(stagedAppRoot);
        var appRoot = channel.TryResolveUpdateTargetDirectory();
        if (string.IsNullOrWhiteSpace(appRoot))
        {
            throw new InvalidOperationException(
                "Updates sind nur in einer Installation erlaubt. Bitte zuerst „Programm installieren.exe“ ausführen.");
        }

        var updaterExe = Path.Combine(stagedAppRoot, channel.ExeFileName);
        if (!File.Exists(updaterExe))
            throw new InvalidOperationException("Update-EXE im Paket nicht gefunden.");

        var arguments = $"--apply-update \"{stagedAppRoot}\" \"{appRoot}\" {Environment.ProcessId}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = updaterExe,
            Arguments = arguments,
            WorkingDirectory = stagedAppRoot,
            UseShellExecute = true
        });
    }

    public static bool TryParseReleaseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        var cleaned = tag.Trim().TrimStart('v', 'V');
        var numeric = Regex.Match(cleaned, @"^(\d+)\.(\d+)(?:\.(\d+))?");
        if (!numeric.Success)
            return false;
        var major = int.Parse(numeric.Groups[1].Value);
        var minor = int.Parse(numeric.Groups[2].Value);
        var build = numeric.Groups[3].Success ? int.Parse(numeric.Groups[3].Value) : 0;
        version = new Version(major, minor, build);
        return true;
    }

    public static string ExtractSha256(string releaseNotes, string assetName)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes) || string.IsNullOrWhiteSpace(assetName))
            return "";
        var named = Regex.Match(
            releaseNotes,
            @"SHA-?256\s+" + Regex.Escape(assetName) + @"\s*[:=]\s*([0-9a-fA-F]{64})",
            RegexOptions.IgnoreCase);
        if (named.Success)
            return named.Groups[1].Value.ToLowerInvariant();
        return "";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.ProductFamily + "/" + AppInfo.DisplayVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool IsTrustedDownloadUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
         || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
         || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "update.zip" : cleaned;
    }

    private static string? FindApplicationRoot(string extractPath, string exeName)
    {
        if (File.Exists(Path.Combine(extractPath, exeName)))
            return extractPath;
        var nested = Directory.GetFiles(extractPath, exeName, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
        return nested is null ? null : Path.GetDirectoryName(nested);
    }

    private static void ExtractZip(
        string zipFilePath,
        string destinationDirectory,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var root = Path.GetFullPath(destinationDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipFilePath);
        var entries = archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)).ToArray();
        var total = Math.Max(1, entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            var entryPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!entryPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsicherer ZIP-Eintrag blockiert: " + entry.FullName);
            var dir = Path.GetDirectoryName(entryPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            entry.ExtractToFile(entryPath, true);
            Report(progress, 82 + (int)((i + 1) * 14.0 / total), "Entpacke: " + entry.Name);
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        int percentStart,
        int percentEnd,
        string message,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            var percent = percentStart;
            if (totalBytes is > 0)
                percent = percentStart + (int)(totalRead * (percentEnd - percentStart) / totalBytes.Value);
            Report(progress, percent, message, totalRead, totalBytes.GetValueOrDefault());
        }
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Report(
        IProgress<UpdateProgressInfo>? progress,
        int percent,
        string message,
        long bytesRead = 0,
        long totalBytes = 0) =>
        progress?.Report(new UpdateProgressInfo(percent, message, bytesRead, totalBytes));

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
