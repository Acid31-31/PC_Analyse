namespace PCAnalyse.Core;

public sealed class UpdateChannel
{
    public required string ProductName { get; init; }
    public required string ExeFileName { get; init; }
    public required string AssetFileName { get; init; }
    public required string AppAssetFileName { get; init; }
    public required string InstallPointerName { get; init; }

    public static UpdateChannel Main { get; } = new()
    {
        ProductName = "PC Analyse",
        ExeFileName = "PCAnalyse.exe",
        AssetFileName = "PCAnalyse-MeinPC.zip",
        AppAssetFileName = "PCAnalyse-MeinPC-app.zip",
        InstallPointerName = "PC Analyse.install"
    };

    public static UpdateChannel Verbindung { get; } = new()
    {
        ProductName = "PC Analyse Verbindung",
        ExeFileName = "PCAnalyse.Verbindung.exe",
        AssetFileName = "PCAnalyse-ZweiterPC.zip",
        AppAssetFileName = "PCAnalyse-ZweiterPC-app.zip",
        InstallPointerName = "PC Analyse Verbindung.install"
    };

    public string PointerFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCAnalyse", InstallPointerName);

    public bool IsRegisteredInstallDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;
        return File.Exists(Path.Combine(directory, "install.manifest"))
               && File.Exists(Path.Combine(directory, ExeFileName));
    }

    public string? TryResolveUpdateTargetDirectory()
    {
        var current = AppInfo.GetApplicationDirectory();
        if (IsRegisteredInstallDirectory(current) && !AppInfo.IsDevelopmentBuildPath(current))
            return Path.GetFullPath(current);

        try
        {
            if (File.Exists(PointerFile))
            {
                var pointer = File.ReadAllText(PointerFile).Trim();
                if (!string.IsNullOrWhiteSpace(pointer))
                {
                    pointer = Path.GetFullPath(pointer);
                    if (File.Exists(Path.Combine(pointer, ExeFileName)) && !AppInfo.IsDevelopmentBuildPath(pointer))
                        return pointer;
                }
            }
        }
        catch
        {
            // Pointer optional
        }

        if (!string.IsNullOrWhiteSpace(current)
            && File.Exists(Path.Combine(current, ExeFileName))
            && !AppInfo.IsDevelopmentBuildPath(current))
            return Path.GetFullPath(current);

        return null;
    }
}
