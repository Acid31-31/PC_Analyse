using System.Diagnostics;

namespace PCAnalyse.Core;

public static class UpdateApplyRunner
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Programm installieren.exe",
        "Programm deinstallieren.exe",
        "STARTEN.bat",
        "DEINSTALLIEREN.bat",
        "createdump.exe",
        "Update-installieren.cmd"
    };

    public static bool TryParse(string[] args, out string stagedRoot, out string targetRoot, out int parentProcessId)
    {
        stagedRoot = "";
        targetRoot = "";
        parentProcessId = 0;
        if (args.Length < 3 || !args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
            return false;
        stagedRoot = Path.GetFullPath(args[1]);
        targetRoot = Path.GetFullPath(args[2]);
        if (args.Length >= 4)
            int.TryParse(args[3], out parentProcessId);
        return Directory.Exists(stagedRoot);
    }

    public static void ApplyUpdate(
        UpdateChannel channel,
        string stagedRoot,
        string targetRoot,
        IProgress<UpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default,
        int parentProcessId = 0)
    {
        stagedRoot = Path.GetFullPath(stagedRoot);
        targetRoot = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(stagedRoot))
            throw new DirectoryNotFoundException("Update-Paket nicht gefunden: " + stagedRoot);
        if (!File.Exists(Path.Combine(stagedRoot, channel.ExeFileName))
            && !File.Exists(Path.Combine(stagedRoot, Path.ChangeExtension(channel.ExeFileName, ".dll"))))
            throw new InvalidOperationException("Update-Paket enthält keine " + channel.ExeFileName + ".");

        Report(progress, 2, "Warte auf Beendigung der Anwendung…");
        CloseRunningInstances(channel, targetRoot, parentProcessId);
        WaitForUnlock(channel, targetRoot, progress, cancellationToken, parentProcessId);

        var overlay = !File.Exists(Path.Combine(stagedRoot, "coreclr.dll"));
        var backupRoot = Path.Combine(AppInfo.UpdateRoot, "backup-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        try
        {
            if (!overlay)
            {
                Report(progress, 8, "Sicherungskopie wird erstellt…");
                CopyTree(targetRoot, backupRoot, cancellationToken);
            }
            Report(progress, 18, overlay
                ? "Programmdateien werden aktualisiert…"
                : "Dateien werden installiert…");
            CopyTree(stagedRoot, targetRoot, cancellationToken, progress, 18, 92, overlay);

            Report(progress, 98, "Anwendung wird gestartet…");
            var exePath = Path.Combine(targetRoot, channel.ExeFileName);
            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = targetRoot,
                    UseShellExecute = true
                });
            }

            Report(progress, 100, "Update abgeschlossen.");
        }
        catch
        {
            try
            {
                if (!overlay && Directory.Exists(backupRoot))
                    CopyTree(backupRoot, targetRoot, CancellationToken.None);
            }
            catch { /* Restore best effort */ }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true); }
            catch { /* Backup aufräumen optional */ }
        }
    }

    private static void CloseRunningInstances(UpdateChannel channel, string targetRoot, int parentProcessId)
    {
        var targetExe = Path.GetFullPath(Path.Combine(targetRoot, channel.ExeFileName));
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(channel.ExeFileName)))
        {
            try
            {
                if (process.Id == self)
                    continue;
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* Zugriff auf fremde Prozesse */ }
                var sameFile = !string.IsNullOrWhiteSpace(path)
                    && string.Equals(Path.GetFullPath(path), targetExe, StringComparison.OrdinalIgnoreCase);
                if (!sameFile && process.Id != parentProcessId)
                    continue;
                if (!process.HasExited)
                    process.CloseMainWindow();
            }
            catch
            {
                // weiter versuchen
            }
        }

        Thread.Sleep(800);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(channel.ExeFileName)))
        {
            try
            {
                if (process.Id == self || process.HasExited)
                    continue;
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* ignore */ }
                var sameFile = !string.IsNullOrWhiteSpace(path)
                    && string.Equals(Path.GetFullPath(path), targetExe, StringComparison.OrdinalIgnoreCase);
                if (sameFile || process.Id == parentProcessId)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Dateisperre kommt danach
            }
        }
    }

    private static void WaitForUnlock(
        UpdateChannel channel,
        string targetRoot,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken,
        int parentProcessId)
    {
        if (parentProcessId > 0)
        {
            for (var i = 0; i < 20; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var process = Process.GetProcessById(parentProcessId);
                    if (process.HasExited)
                        break;
                }
                catch (ArgumentException)
                {
                    break;
                }

                Report(progress, 2 + i / 10, "Warte auf Beendigung der Anwendung…");
                Thread.Sleep(250);
            }
        }

        var lockFile = Path.Combine(targetRoot, channel.ExeFileName);
        for (var attempt = 0; attempt < 24; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLocked(lockFile))
            {
                Report(progress, 10, "Anwendung beendet, Installation läuft…");
                return;
            }

            Report(progress, Math.Min(10, 2 + attempt / 8), "Warte auf Beendigung der Anwendung…");
            Thread.Sleep(250);
        }

        throw new InvalidOperationException("Die alte Anwendung konnte nicht beendet werden. Bitte alle Fenster schließen und erneut versuchen.");
    }

    private static bool IsLocked(string executablePath)
    {
        if (!File.Exists(executablePath))
            return false;
        try
        {
            using (File.Open(executablePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static bool IsOverlayPayload(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        var name = Path.GetFileName(fileName);
        if (!name.StartsWith("PCAnalyse", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyTree(
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken,
        IProgress<UpdateProgressInfo>? progress = null,
        int percentStart = 0,
        int percentEnd = 100,
        bool overlayOnly = false)
    {
        Directory.CreateDirectory(targetRoot);
        var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var name = Path.GetFileName(file);
                if (SkipNames.Contains(name))
                    return false;
                return !overlayOnly || IsOverlayPayload(name);
            })
            .ToArray();
        var total = Math.Max(1, files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            var relative = file[(sourceRoot.Length)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destination = Path.Combine(targetRoot, relative);
            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.Copy(file, destination, true);
            var percent = percentStart + (int)((i + 1) * (percentEnd - percentStart) / (double)total);
            Report(progress, percent, "Installiere: " + Path.GetFileName(file));
        }
    }

    private static void Report(IProgress<UpdateProgressInfo>? progress, int percent, string message) =>
        progress?.Report(new UpdateProgressInfo(percent, message));
}
