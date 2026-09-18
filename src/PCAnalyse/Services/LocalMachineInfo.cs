using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PCAnalyse.Services;

public sealed class LocalMachineSnapshot
{
    public string ComputerName { get; init; } = "";
    public string UserName { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string ProcessorSummary { get; init; } = "";
    public string MemorySummary { get; init; } = "";
    public string NetworkSummary { get; init; } = "";
    public string DiskSummary { get; init; } = "";
}

public static class LocalMachineInfo
{
    public static LocalMachineSnapshot Collect()
    {
        var ramGb = Math.Round(GetTotalMemoryBytes() / 1024d / 1024d / 1024d, 1);
        var disks = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => $"{d.Name.TrimEnd('\\')} {Math.Round(d.TotalSize / 1024d / 1024d / 1024d)} GB");
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .Distinct();

        return new LocalMachineSnapshot
        {
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessorSummary = $"{Environment.ProcessorCount} Kerne",
            MemorySummary = $"{ramGb} GB",
            NetworkSummary = addresses.Any() ? string.Join(", ", addresses) : "Keine IPv4",
            DiskSummary = disks.Any() ? string.Join("  ·  ", disks) : "Keine festen Laufwerke"
        };
    }

    [DllImport("kernel32.dll")]
    private static extern void GetPhysicallyInstalledSystemMemory(out ulong totalMemoryKb);

    private static ulong GetTotalMemoryBytes()
    {
        try
        {
            GetPhysicallyInstalledSystemMemory(out var kb);
            if (kb > 0)
                return kb * 1024;
        }
        catch
        {
            // Fallback unten.
        }

        return (ulong)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }
}
