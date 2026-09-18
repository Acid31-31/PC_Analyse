using System.Reflection;

namespace PCAnalyse.Core;

public static class AppInfo
{
    public const string GitHubOwner = "Acid31-31";
    public const string GitHubRepo = "PC_Analyse";
    public const string ProductFamily = "PC Analyse";
    public const bool GitHubUpdatesArePublic = true;

    public static string DisplayVersion
    {
        get
        {
            var version = ApplicationVersion;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static Version ApplicationVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version
                          ?? Assembly.GetExecutingAssembly().GetName().Version
                          ?? new Version(1, 0, 1);
            var build = version.Build >= 0 ? version.Build : 0;
            return new Version(version.Major, version.Minor, build);
        }
    }

    public static string GitHubLatestReleaseApiUrl =>
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public static string UserDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCAnalyse");

    public static string UpdateRoot => Path.Combine(UserDataDirectory, "Updates");

    public static string GetApplicationDirectory()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
            return Path.GetDirectoryName(path)!;
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsDevelopmentBuildPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var full = Path.GetFullPath(path);
        return full.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
               || full.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)
               || full.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase)
               || full.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase);
    }
}
