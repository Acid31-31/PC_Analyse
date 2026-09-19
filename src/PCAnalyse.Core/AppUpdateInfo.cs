using System.Globalization;

namespace PCAnalyse.Core;

public sealed class AppUpdateInfo
{
    public bool UpdateAvailable { get; set; }
    public Version? RemoteVersion { get; set; }
    public string ReleaseTag { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
    public long AssetId { get; set; }
    public long AssetSizeBytes { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string LocalPackagePath { get; set; } = "";
    public bool FromLocalShare { get; set; }

    public string AssetSizeDisplay => FormatMegabytes(AssetSizeBytes);

    public static string FormatMegabytes(long bytes)
    {
        if (bytes <= 0)
            return "";
        var culture = CultureInfo.GetCultureInfo("de-DE");
        if (bytes < 1024 * 1024)
            return string.Format(culture, "{0:0} KB", bytes / 1024.0);
        return string.Format(culture, "{0:0.0} MB", bytes / (1024.0 * 1024.0));
    }
}

public sealed class UpdateProgressInfo
{
    public UpdateProgressInfo(int percent, string message, long bytesRead = 0, long totalBytes = 0, long bytesPerSecond = 0)
    {
        Percent = percent;
        Message = message;
        BytesRead = bytesRead;
        TotalBytes = totalBytes;
        BytesPerSecond = bytesPerSecond;
    }

    public int Percent { get; }
    public string Message { get; }
    public long BytesRead { get; }
    public long TotalBytes { get; }
    public long BytesPerSecond { get; }
}
