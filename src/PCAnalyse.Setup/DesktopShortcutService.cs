using System.Diagnostics;
using System.Runtime.InteropServices;
using PCAnalyse.Core;

namespace PCAnalyse.Setup;

public static class DesktopShortcutService
{
    public static bool TryCreate(ProductDefinition product, string targetExe, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(targetExe) || !File.Exists(targetExe))
        {
            message = "Zieldatei fehlt.";
            return false;
        }

        var working = Path.GetDirectoryName(targetExe) ?? "";
        var created = new List<string>();
        var errors = new List<string>();
        foreach (var folder in ShortcutFolders(product))
        {
            Directory.CreateDirectory(folder);
            var shortcutPath = Path.Combine(folder, product.ShortcutFileName);
            if (CreateOne(shortcutPath, targetExe, working, product.ProductName, out var error))
                created.Add(shortcutPath);
            else if (!string.IsNullOrWhiteSpace(error))
                errors.Add(error);
        }

        if (created.Count > 0)
        {
            message = created[0];
            return true;
        }

        message = errors.Count > 0 ? string.Join(" ", errors.Distinct()) : "Desktop-Verknüpfung konnte nicht erstellt werden.";
        return false;
    }

    public static void EnsureForRunningApp(ProductDefinition product)
    {
        if (LaunchMode.IsInstaller() || LaunchMode.IsUninstaller())
            return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return;
        var dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrWhiteSpace(dir))
            return;
        if (!File.Exists(Path.Combine(dir, ProductDefinition.ManifestFileName)))
            return;
        if (AppInfo.IsDevelopmentBuildPath(dir))
            return;
        TryCreate(product, exe, out _);
    }

    public static void TryRemove(ProductDefinition product)
    {
        foreach (var folder in ShortcutFolders(product))
        {
            try
            {
                var shortcutPath = Path.Combine(folder, product.ShortcutFileName);
                if (File.Exists(shortcutPath))
                    File.Delete(shortcutPath);
            }
            catch
            {
                // Verknüpfung ist optional.
            }
        }
    }

    private static IEnumerable<string> ShortcutFolders(ProductDefinition product)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs)
                 })
        {
            if (string.IsNullOrWhiteSpace(folder) || !seen.Add(Path.GetFullPath(folder)))
                continue;
            yield return folder;
        }
    }

    private static bool CreateOne(string shortcutPath, string targetExe, string working, string productName, out string error)
    {
        error = "";
        if (CreateViaStaCom(shortcutPath, targetExe, working, productName, out error))
            return File.Exists(shortcutPath);
        if (CreateViaPowerShell(shortcutPath, targetExe, working, productName, out error))
            return File.Exists(shortcutPath);
        return false;
    }

    private static bool CreateViaStaCom(string shortcutPath, string targetExe, string working, string productName, out string error)
    {
        string captured = "";
        var ok = false;
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            ok = CreateViaWsh(shortcutPath, targetExe, working, productName, out captured);
        }
        else
        {
            var thread = new Thread(() =>
            {
                ok = CreateViaWsh(shortcutPath, targetExe, working, productName, out captured);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(8)))
            {
                error = "Zeitüberschreitung bei der Verknüpfung.";
                return false;
            }
        }

        error = captured;
        return ok;
    }

    private static bool CreateViaWsh(string shortcutPath, string targetExe, string working, string productName, out string error)
    {
        error = "";
        try
        {
            var wshType = Type.GetTypeFromProgID("WScript.Shell");
            if (wshType is null)
            {
                error = "Windows-Shell nicht verfügbar.";
                return false;
            }

            dynamic shell = Activator.CreateInstance(wshType)
                            ?? throw new InvalidOperationException("Windows-Shell nicht verfügbar.");
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = working;
            shortcut.IconLocation = targetExe + ",0";
            shortcut.Description = productName + " starten";
            shortcut.Save();
            return File.Exists(shortcutPath);
        }
        catch (COMException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool CreateViaPowerShell(string shortcutPath, string targetExe, string working, string productName, out string error)
    {
        error = "";
        try
        {
            var script =
                "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('" + EscapePs(shortcutPath) + "'); " +
                "$s.TargetPath = '" + EscapePs(targetExe) + "'; " +
                "$s.WorkingDirectory = '" + EscapePs(working) + "'; " +
                "$s.IconLocation = '" + EscapePs(targetExe) + ",0'; " +
                "$s.Description = '" + EscapePs(productName + " starten") + "'; " +
                "$s.Save()";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-STA -NoProfile -WindowStyle Hidden -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(8000);
            return File.Exists(shortcutPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string EscapePs(string value) => (value ?? "").Replace("'", "''");
}
