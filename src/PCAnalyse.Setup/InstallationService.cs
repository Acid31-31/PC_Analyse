namespace PCAnalyse.Setup;

public static class InstallationService
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ProductDefinition.InstallerExeName,
        ProductDefinition.UninstallerExeName,
        "STARTEN.bat",
        "DEINSTALLIEREN.bat",
        "createdump.exe"
    };

    public static void Install(ProductDefinition product, string sourceDir, string targetDir, IProgress<string>? progress)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        targetDir = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(targetDir);

        progress?.Report("Programmdateien werden kopiert…");
        if (!PathsEqual(sourceDir, targetDir))
            CopyFiles(sourceDir, targetDir);

        var exe = Path.Combine(targetDir, product.ExeFileName);
        if (!File.Exists(exe))
            throw new InvalidOperationException("Programmdatei fehlt: " + product.ExeFileName);

        progress?.Report("Installation wird eingetragen…");
        File.WriteAllText(Path.Combine(targetDir, ProductDefinition.ManifestFileName),
            $"Product={product.ProductName}{Environment.NewLine}Machine={Environment.MachineName}{Environment.NewLine}Installed={DateTimeOffset.Now:O}{Environment.NewLine}");
        Remember(product, targetDir);

        progress?.Report("Desktop-Verknüpfung wird erstellt…");
        DesktopShortcutService.TryCreate(product, exe, out _);
    }

    public static void Uninstall(ProductDefinition product, string installDir)
    {
        DesktopShortcutService.TryRemove(product);
        var target = LastInstallDir(product) ?? installDir;
        if (Directory.Exists(target) && !PathsEqual(target, LaunchMode.AppDirectory))
            Directory.Delete(target, true);
        var pointer = PointerFile(product);
        if (File.Exists(pointer))
            File.Delete(pointer);
    }

    public static void Remember(ProductDefinition product, string targetDir)
    {
        var file = PointerFile(product);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, targetDir);
    }

    public static string? LastInstallDir(ProductDefinition product)
    {
        var file = PointerFile(product);
        if (!File.Exists(file))
            return null;
        var dir = File.ReadAllText(file).Trim();
        return Directory.Exists(dir) ? dir : null;
    }

    private static string PointerFile(ProductDefinition product) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCAnalyse", product.SuggestedFolderName + ".install");

    private static void CopyFiles(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (SkipNames.Contains(name))
                continue;
            File.Copy(file, Path.Combine(target, name), true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "Mein PC" or "Zweiter PC")
                continue;
            var dest = Path.Combine(target, name);
            Directory.CreateDirectory(dest);
            CopyFiles(dir, dest);
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
