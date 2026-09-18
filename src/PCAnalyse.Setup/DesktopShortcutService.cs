using System.Runtime.InteropServices;

namespace PCAnalyse.Setup;

public static class DesktopShortcutService
{
    public static bool TryCreate(ProductDefinition product, string targetExe, out string message)
    {
        message = "";
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            {
                message = "Desktop nicht gefunden.";
                return false;
            }

            var shortcutPath = Path.Combine(desktop, product.ShortcutFileName);
            var working = Path.GetDirectoryName(targetExe) ?? "";
            var wshType = Type.GetTypeFromProgID("WScript.Shell");
            if (wshType is null)
            {
                message = "Windows-Shell nicht verfügbar.";
                return false;
            }

                dynamic shell = Activator.CreateInstance(wshType)
                    ?? throw new InvalidOperationException("Windows-Shell nicht verfügbar.");
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = working;
            shortcut.IconLocation = targetExe + ",0";
            shortcut.Description = product.ProductName + " starten";
            shortcut.Save();
            message = shortcutPath;
            return File.Exists(shortcutPath);
        }
        catch (COMException ex)
        {
            message = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public static void TryRemove(ProductDefinition product)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(desktop, product.ShortcutFileName);
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
        }
        catch
        {
            // Verknüpfung ist optional.
        }
    }
}
