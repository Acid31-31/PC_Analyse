using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PCAnalyse.Core;

public sealed class LinkHub : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly IPAddress Multicast = IPAddress.Parse(LinkPorts.MulticastAddress);
    private const int SioUdpConnreset = -1744830452;

    private readonly PairingStore _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new();
    private UdpClient? _udp;
    private TcpListener? _tcp;
    private TcpClient? _session;
    private bool _connecting;
    private int _broadcastTick;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public string ComputerName { get; } = Environment.MachineName;
    public bool Waiting { get; set; } = true;
    public string? PeerInstanceId { get; private set; }
    public IPAddress? PeerAddress { get; private set; }
    public string Status { get; private set; } = "Getrennt";
    public string? ConnectedPeer { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<string, string>? PeerConnected;
    public event Action<IReadOnlyList<DiscoveredPeer>>? PeersChanged;

    public LinkHub(PairingStore store)
    {
        _store = store;
    }

    public void Start()
    {
        TryOpenFirewall();
        _udp = CreateUdp();
        try
        {
            _tcp = new TcpListener(IPAddress.Any, LinkPorts.Tcp);
            _tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcp.Start();
        }
        catch (Exception ex)
        {
            SetStatus("TCP-Port 49582 belegt: " + ex.Message);
            return;
        }
        _ = ReceiveLoopAsync(_cts.Token);
        _ = BroadcastLoopAsync(_cts.Token);
        _ = AcceptLoopAsync(_cts.Token);
        var ip = LocalIpv4s().FirstOrDefault()?.ToString();
        SetStatus(string.IsNullOrEmpty(ip)
            ? "Sucht den anderen PC im Netzwerk …"
            : $"Sucht den anderen PC … (diese IP: {ip})");
    }

    public IReadOnlyList<DiscoveredPeer> Snapshot() =>
        _peers.Values.Where(p => DateTimeOffset.Now - p.LastSeen < TimeSpan.FromSeconds(12)).ToList();

    public async Task<LinkMessage> ConnectAsync(DiscoveredPeer peer, CancellationToken token = default)
    {
        var endpoint = peer.EndPoint;
        if (!string.IsNullOrWhiteSpace(peer.AnnounceIp) && IPAddress.TryParse(peer.AnnounceIp, out var parsed))
            endpoint = new IPEndPoint(parsed, peer.EndPoint.Port);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint, timeout.Token);
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            var writer = new StreamWriter(stream, Utf8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            _ = await reader.ReadLineAsync(timeout.Token);
            var known = _store.GetToken(peer.InstanceId) ?? "";
            await writer.WriteLineAsync(LinkMessage.ToLine(LinkMessage.Pair(InstanceId, ComputerName, known)));
            var response = LinkMessage.FromLine(await reader.ReadLineAsync(timeout.Token) ?? "")
                           ?? LinkMessage.Reject("Keine Antwort vom zweiten PC.");
            if (response.Type != "paired")
            {
                client.Dispose();
                return response;
            }

            _store.SaveToken(peer.InstanceId, response.Token);
            _session?.Dispose();
            _session = client;
            PeerAddress = ToIpv4(endpoint.Address);
            PeerInstanceId = peer.InstanceId;
            ConnectedPeer = response.Name;
            SetStatus($"Verbunden mit {response.Name}");
            PeerConnected?.Invoke(peer.InstanceId, response.Name);
            _ = KeepAliveAsync(reader, writer, sendPing: true, _cts.Token);
            return response;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _session?.Dispose();
        _udp?.Dispose();
        _tcp?.Stop();
        _cts.Dispose();
    }

    private UdpClient CreateUdp()
    {
        var udp = new UdpClient();
        udp.ExclusiveAddressUse = false;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, LinkPorts.Udp));
        udp.EnableBroadcast = true;
        udp.MulticastLoopback = false;
        try { udp.Client.IOControl(SioUdpConnreset, new byte[] { 0, 0, 0, 0 }, null); }
        catch { /* nicht auf jedem System */ }
        try { udp.JoinMulticastGroup(Multicast); }
        catch { /* Adapter ohne Multicast */ }
        return udp;
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udp is not null)
        {
            try
            {
                await SendAnnouncementsAsync();
            }
            catch
            {
                // nächster Versuch
            }

            try { await Task.Delay(1500, token); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task SendAnnouncementsAsync()
    {
        if (_udp is null)
            return;

        var locals = LocalIpv4s().ToList();
        var announceIp = locals.FirstOrDefault()?.ToString() ?? "";
        var packet = DiscoveryCodec.Encode(new DiscoveryPacket(InstanceId, ComputerName, LinkPorts.Tcp, Waiting, announceIp));
        var targets = new List<IPAddress> { IPAddress.Broadcast, Multicast };
        targets.AddRange(DirectedBroadcasts());
        _broadcastTick++;
        if (_broadcastTick % 3 == 0)
            targets.AddRange(SubnetHosts(locals));

        foreach (var ip in targets.Distinct())
        {
            try { await _udp.SendAsync(packet, packet.Length, new IPEndPoint(ip, LinkPorts.Udp)); }
            catch { /* Ziel nicht erreichbar */ }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udp is not null)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(token); }
            catch (OperationCanceledException) { return; }
            catch { continue; }

            if (!DiscoveryCodec.TryDecode(result.Buffer, out var packet))
                continue;
            if (packet.InstanceId == InstanceId)
                continue;

            var ip = result.RemoteEndPoint.Address;
            if (!string.IsNullOrWhiteSpace(packet.AnnounceIp) && IPAddress.TryParse(packet.AnnounceIp, out var announced))
                ip = announced;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();

            var peer = new DiscoveredPeer
            {
                InstanceId = packet.InstanceId,
                ComputerName = packet.ComputerName,
                EndPoint = new IPEndPoint(ip, packet.TcpPort),
                AnnounceIp = packet.AnnounceIp,
                Waiting = packet.Waiting,
                LastSeen = DateTimeOffset.Now
            };
            _peers.AddOrUpdate(packet.InstanceId, peer, (_, _) => peer);
            var list = Snapshot();
            PeersChanged?.Invoke(list);
            _ = TryAutoConnectAsync(list);
        }
    }

    private async Task TryAutoConnectAsync(IReadOnlyList<DiscoveredPeer> peers)
    {
        if (_connecting || ConnectedPeer is not null || _session is not null)
            return;
        var peer = peers.FirstOrDefault(p => p.Waiting);
        if (peer is null)
            return;

        _connecting = true;
        try { await ConnectAsync(peer); }
        catch { /* nächster Broadcast */ }
        finally { _connecting = false; }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _tcp is not null)
        {
            TcpClient client;
            try { client = await _tcp.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { return; }
            catch { continue; }
            _ = Task.Run(() => HandleClientAsync(client, token), token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        await using (var writer = new StreamWriter(stream, Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" })
        {
            await writer.WriteLineAsync(LinkMessage.ToLine(LinkMessage.Hello(InstanceId, ComputerName)));
            var message = LinkMessage.FromLine(await reader.ReadLineAsync(token) ?? "");
            if (message?.Type != "pair")
            {
                await writer.WriteLineAsync(LinkMessage.ToLine(LinkMessage.Reject("Ungültige Anfrage.")));
                return;
            }

            var known = _store.GetToken(message.Id);
            var accepted = Waiting ||
                           (!string.IsNullOrEmpty(known) && PairingToken.EqualsFixed(known, message.Token));
            if (!accepted)
            {
                await writer.WriteLineAsync(LinkMessage.ToLine(LinkMessage.Reject("Dieser PC wartet gerade nicht.")));
                return;
            }

            var tokenValue = string.IsNullOrEmpty(known) ? PairingToken.Create() : known;
            _store.SaveToken(message.Id, tokenValue);
            await writer.WriteLineAsync(LinkMessage.ToLine(LinkMessage.Paired(InstanceId, ComputerName, tokenValue)));
            ConnectedPeer = message.Name;
            PeerAddress = ToIpv4((client.Client.RemoteEndPoint as IPEndPoint)?.Address);
            PeerInstanceId = message.Id;
            SetStatus($"Verbunden mit {message.Name}");
            PeerConnected?.Invoke(message.Id, message.Name);
            await KeepAliveAsync(reader, writer, sendPing: false, token);
        }
    }

    private static async Task KeepAliveAsync(StreamReader reader, StreamWriter writer, bool sendPing, CancellationToken token)
    {
        var gate = new SemaphoreSlim(1, 1);
        var pinger = sendPing ? SendPingsAsync(writer, gate, token) : Task.CompletedTask;
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(token); }
                catch { break; }
                if (line is null)
                    break;
                if (LinkMessage.FromLine(line)?.Type != "ping")
                    continue;
                await gate.WaitAsync(token);
                try { await writer.WriteLineAsync(LinkMessage.ToLine(new LinkMessage { Type = "pong" })); }
                finally { gate.Release(); }
            }
        }
        finally
        {
            try { await pinger; } catch { /* Verbindung beendet */ }
        }
    }

    private static async Task SendPingsAsync(StreamWriter writer, SemaphoreSlim gate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(4000, token); }
            catch (TaskCanceledException) { return; }
            await gate.WaitAsync(token);
            try { await writer.WriteLineAsync(LinkMessage.ToLine(new LinkMessage { Type = "ping" })); }
            catch { return; }
            finally { gate.Release(); }
        }
    }

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }

    private static IEnumerable<IPAddress> LocalIpv4s()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return info.Address;
            }
        }
    }

    private static IEnumerable<IPAddress> DirectedBroadcasts()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask is null)
                    continue;
                var ip = ToUInt(info.Address);
                var mask = ToUInt(info.IPv4Mask);
                yield return ToIp(ip | ~mask);
            }
        }
    }

    private static IEnumerable<IPAddress> SubnetHosts(IReadOnlyList<IPAddress> locals)
    {
        var localSet = locals.Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask is null)
                    continue;
                var ip = ToUInt(info.Address);
                var mask = ToUInt(info.IPv4Mask);
                var network = ip & mask;
                var broadcast = network | ~mask;
                var count = broadcast - network;
                if (count is <= 1 or > 512)
                    continue;
                for (var host = network + 1; host < broadcast; host++)
                {
                    var address = ToIp(host);
                    if (!localSet.Contains(address.ToString()))
                        yield return address;
                }
            }
        }
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static IPAddress ToIp(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return new IPAddress(bytes);
    }

    private static IPAddress? ToIpv4(IPAddress? address)
    {
        if (address is null)
            return null;
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetwork ? address : null;
    }

    private static void TryOpenFirewall()
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            var rules = new List<string>
            {
                $"advfirewall firewall add rule name=\"PCAnalyse UDP\" dir=in action=allow protocol=UDP localport={LinkPorts.Udp} profile=domain,private,public",
                $"advfirewall firewall add rule name=\"PCAnalyse TCP\" dir=in action=allow protocol=TCP localport={LinkPorts.Tcp} profile=domain,private,public",
                $"advfirewall firewall add rule name=\"PCAnalyse Maus\" dir=in action=allow protocol=TCP localport={LinkPorts.InputTcp} profile=domain,private,public"
            };
            if (!string.IsNullOrWhiteSpace(exe))
            {
                rules.Add($"advfirewall firewall add rule name=\"PCAnalyse Programm\" dir=in action=allow program=\"{exe}\" profile=domain,private,public");
                rules.Add($"advfirewall firewall add rule name=\"PCAnalyse Programm out\" dir=out action=allow program=\"{exe}\" profile=domain,private,public");
            }

            foreach (var args in rules)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                })?.WaitForExit(3000);
            }
        }
        catch
        {
            // Ohne Admin-Recht weiterversuchen.
        }
    }
}
