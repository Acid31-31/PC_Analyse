using System.Text;

namespace PCAnalyse.Core;

public sealed record DiscoveryPacket(
    string InstanceId,
    string ComputerName,
    int TcpPort,
    bool Waiting,
    string AnnounceIp = "");

public static class DiscoveryCodec
{
    public static byte[] Encode(DiscoveryPacket packet)
    {
        var line = string.Join('\t',
            LinkPorts.DiscoveryPrefix,
            packet.InstanceId,
            packet.ComputerName.Replace('\t', ' '),
            packet.TcpPort.ToString(),
            packet.Waiting ? "1" : "0",
            packet.AnnounceIp);
        return Encoding.UTF8.GetBytes(line);
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out DiscoveryPacket packet)
    {
        packet = new DiscoveryPacket("", "", 0, false, "");
        var text = Encoding.UTF8.GetString(data).Trim('\0', ' ', '\r', '\n');
        var parts = text.Split('\t');
        if (parts.Length < 5 || parts[0] != LinkPorts.DiscoveryPrefix)
            return false;
        if (!int.TryParse(parts[3], out var port) || port is < 1 or > 65535)
            return false;
        if (string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]))
            return false;

        var ip = parts.Length >= 6 ? parts[5].Trim() : "";
        packet = new DiscoveryPacket(parts[1].Trim(), parts[2].Trim(), port, parts[4].Trim() == "1", ip);
        return true;
    }
}
