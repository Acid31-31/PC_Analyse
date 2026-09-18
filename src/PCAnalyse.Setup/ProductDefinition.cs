namespace PCAnalyse.Setup;

public sealed class ProductDefinition
{
    public required string ProductName { get; init; }
    public required string ExeFileName { get; init; }
    public required string ShortcutFileName { get; init; }
    public required string SuggestedFolderName { get; init; }

    public const string InstallerExeName = "Programm installieren.exe";
    public const string UninstallerExeName = "Programm deinstallieren.exe";
    public const string ManifestFileName = "install.manifest";

    public static ProductDefinition MainApp { get; } = new()
    {
        ProductName = "PC Analyse",
        ExeFileName = "PCAnalyse.exe",
        ShortcutFileName = "PC Analyse.lnk",
        SuggestedFolderName = "PC Analyse"
    };

    public static ProductDefinition Verbindung { get; } = new()
    {
        ProductName = "PC Analyse Verbindung",
        ExeFileName = "PCAnalyse.Verbindung.exe",
        ShortcutFileName = "PC Analyse Verbindung.lnk",
        SuggestedFolderName = "PC Analyse Verbindung"
    };

    public string SuggestedInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SuggestedFolderName);
}
