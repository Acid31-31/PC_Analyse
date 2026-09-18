using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PCAnalyse.Core;

public sealed class DiscoveredPeer
{
    public required string InstanceId { get; init; }
    public required string ComputerName { get; init; }
    public required IPEndPoint EndPoint { get; init; }
    public string AnnounceIp { get; init; } = "";
    public bool Waiting { get; init; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.Now;
}

public sealed class LinkScanner : IDisposable
{
    private readonly string _localInstanceId;
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public event Action<IReadOnlyList<DiscoveredPeer>>? PeersChanged;

    public LinkScanner(string localInstanceId)
    {
        _localInstanceId = localInstanceId;
    }

    public void Start()
    {
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, LinkPorts.Udp));
        _udp.EnableBroadcast = true;
        _ = ListenAsync(_cts.Token);
    }

    public IReadOnlyList<DiscoveredPeer> Snapshot() =>
        _peers.Values.Where(p => DateTimeOffset.Now - p.LastSeen < TimeSpan.FromSeconds(8)).ToList();

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udp is not null)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(token); }
            catch (OperationCanceledException) { return; }
            catch { continue; }

            if (!DiscoveryCodec.TryDecode(result.Buffer, out var packet))
                continue;
            if (packet.InstanceId == _localInstanceId)
                continue;

            var peer = new DiscoveredPeer
            {
                InstanceId = packet.InstanceId,
                ComputerName = packet.ComputerName,
                EndPoint = new IPEndPoint(result.RemoteEndPoint.Address, packet.TcpPort),
                Waiting = packet.Waiting,
                LastSeen = DateTimeOffset.Now
            };
            _peers.AddOrUpdate(packet.InstanceId, peer, (_, _) => peer);
            PeersChanged?.Invoke(Snapshot());
        }
    }
}
