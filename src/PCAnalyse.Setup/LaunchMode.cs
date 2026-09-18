using System.Reflection;

namespace PCAnalyse.Setup;

public static class LaunchMode
{
    public static bool IsInstaller() => FileNameEquals(ProductDefinition.InstallerExeName);
    public static bool IsUninstaller() => FileNameEquals(ProductDefinition.UninstallerExeName);

    public static string AppDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath)
        ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        ?? AppContext.BaseDirectory;

    private static bool FileNameEquals(string name)
    {
        var file = Path.GetFileName(Environment.ProcessPath ?? "");
        return file.Equals(name, StringComparison.OrdinalIgnoreCase);
    }
}
