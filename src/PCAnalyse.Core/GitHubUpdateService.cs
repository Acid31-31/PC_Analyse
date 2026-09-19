using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
            using var client = CreateApiClient();
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

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Report(progress, 4, attempt == 1
                    ? "Update wird heruntergeladen…"
                    : "Download-Versuch " + attempt + " …");
                await DownloadFileAsync(uri, packagePath, update.AssetSizeBytes, progress, cancellationToken);
                lastError = null;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                lastError = ex;
                Report(progress, 4, "Download unterbrochen, neuer Versuch …");
                await Task.Delay(800, cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
            throw lastError;

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

    private static HttpClient CreateApiClient()
    {
        var client = new HttpClient(CreateHandler()) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.ProductFamily + "/" + AppInfo.DisplayVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient(CreateHandler()) { Timeout = TimeSpan.FromHours(1) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.ProductFamily + "/" + AppInfo.DisplayVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        return client;
    }

    private static SocketsHttpHandler CreateHandler() =>
        new()
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectIpv4Async
        };

    private static async ValueTask<Stream> ConnectIpv4Async(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? throw new InvalidOperationException("Kein IPv4 für " + context.DnsEndPoint.Host);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            TryBindBestInterface(socket, ipv4);
            await socket.ConnectAsync(ipv4, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void TryBindBestInterface(Socket socket, IPAddress destination)
    {
        try
        {
            var bytes = destination.GetAddressBytes();
            if (bytes.Length != 4)
                return;
            var dest = BitConverter.ToUInt32(bytes, 0);
            if (GetBestInterface(dest, out var index) != 0)
                return;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                int nicIndex;
                try { nicIndex = nic.GetIPProperties().GetIPv4Properties().Index; }
                catch { continue; }
                if (nicIndex != (int)index)
                    continue;
                var local = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    ?.Address;
                if (local is null)
                    return;
                socket.Bind(new IPEndPoint(local, 0));
                return;
            }
        }
        catch
        {
            // Standard-Routing verwenden
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    private static async Task DownloadFileAsync(
        Uri uri,
        string packagePath,
        long expectedSize,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        long existing = 0;
        if (File.Exists(packagePath))
        {
            existing = new FileInfo(packagePath).Length;
            if (expectedSize > 0 && existing == expectedSize)
                return;
            if (expectedSize > 0 && existing > expectedSize)
            {
                File.Delete(packagePath);
                existing = 0;
            }
        }

        using var client = CreateDownloadClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return;
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            existing = 0;
            total ??= expectedSize > 0 ? expectedSize : null;
        }
        else if (total is not null)
            total += existing;
        else if (expectedSize > 0)
            total = expectedSize;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(
            packagePath,
            existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            256 * 1024);
        await CopyWithProgressAsync(stream, file, total, existing, 5, 75, progress, cancellationToken);
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
        long alreadyRead,
        int percentStart,
        int percentEnd,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        long totalRead = alreadyRead;
        var started = DateTime.UtcNow;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            var percent = percentStart;
            if (totalBytes is > 0)
                percent = percentStart + (int)(totalRead * (percentEnd - percentStart) / totalBytes.Value);
            var elapsed = Math.Max(0.5, (DateTime.UtcNow - started).TotalSeconds);
            var speed = (totalRead - alreadyRead) / elapsed;
            var remain = totalBytes is > 0 && speed > 1
                ? TimeSpan.FromSeconds(Math.Max(0, (totalBytes.Value - totalRead) / speed))
                : (TimeSpan?)null;
            var message = remain is null
                ? "Update wird heruntergeladen…"
                : "Update wird heruntergeladen … noch ca. " + FormatEta(remain.Value);
            Report(progress, percent, message, totalRead, totalBytes.GetValueOrDefault(), (long)speed);
        }
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
            return (int)eta.TotalHours + " Std. " + eta.Minutes + " Min.";
        if (eta.TotalMinutes >= 1)
            return (int)eta.TotalMinutes + " Min.";
        return Math.Max(1, (int)eta.TotalSeconds) + " Sek.";
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
        long totalBytes = 0,
        long bytesPerSecond = 0) =>
        progress?.Report(new UpdateProgressInfo(percent, message, bytesRead, totalBytes, bytesPerSecond));

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
