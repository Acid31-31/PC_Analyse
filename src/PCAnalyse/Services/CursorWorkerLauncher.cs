using System.Diagnostics;
using System.IO;
using PCAnalyse.Core;

namespace PCAnalyse.Services;

public static class CursorWorkerLauncher
{
    public static void Start(string machineName, string workerDir)
    {
        Directory.CreateDirectory(workerDir);
        var command = CursorWorkerCommand.BuildStartCommand(machineName, workerDir);
        var script = "$ErrorActionPreference='Stop'; " +
                     "if (-not (Get-Command agent -ErrorAction SilentlyContinue)) { " +
                     "irm 'https://cursor.com/install?win32=true' | iex }; " +
                     "if (-not (Get-Command agent -ErrorAction SilentlyContinue)) { " +
                     "throw 'Cursor CLI (agent) wurde nicht gefunden.' }; " +
                     command;

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoExit -ExecutionPolicy Bypass -Command " + Quote(script),
            UseShellExecute = true,
            WorkingDirectory = workerDir
        };
        Process.Start(start);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "`\"") + "\"";
}
