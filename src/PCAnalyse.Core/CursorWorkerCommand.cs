namespace PCAnalyse.Core;

public static class CursorWorkerCommand
{
    public static string BuildStartCommand(string machineName, string workerDir)
    {
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("Maschinenname fehlt.", nameof(machineName));
        if (string.IsNullOrWhiteSpace(workerDir))
            throw new ArgumentException("Projektordner fehlt.", nameof(workerDir));

        var name = machineName.Replace("\"", "");
        var dir = workerDir.Replace("\"", "");
        return $"agent worker start --name \"{name}\" --worker-dir \"{dir}\" --computer-use";
    }
}
