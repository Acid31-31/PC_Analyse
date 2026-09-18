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
        "createdump.exe"
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
        if (!File.Exists(Path.Combine(stagedRoot, channel.ExeFileName)))
            throw new InvalidOperationException("Update-Paket enthält keine " + channel.ExeFileName + ".");

        Report(progress, 2, "Warte auf Beendigung der Anwendung…");
        WaitForUnlock(channel, targetRoot, progress, cancellationToken, parentProcessId);

        var backupRoot = Path.Combine(AppInfo.UpdateRoot, "backup-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        try
        {
            Report(progress, 8, "Sicherungskopie wird erstellt…");
            CopyTree(targetRoot, backupRoot, cancellationToken);

            Report(progress, 18, "Dateien werden installiert…");
            CopyTree(stagedRoot, targetRoot, cancellationToken, progress, 18, 92);

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
            try { if (Directory.Exists(backupRoot)) CopyTree(backupRoot, targetRoot, CancellationToken.None); }
            catch { /* Restore best effort */ }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true); }
            catch { /* Backup aufräumen optional */ }
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
            for (var i = 0; i < 80; i++)
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
        for (var attempt = 0; attempt < 80; attempt++)
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

    private static void CopyTree(
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken,
        IProgress<UpdateProgressInfo>? progress = null,
        int percentStart = 0,
        int percentEnd = 100)
    {
        Directory.CreateDirectory(targetRoot);
        var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var name = Path.GetFileName(file);
                return !SkipNames.Contains(name);
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
