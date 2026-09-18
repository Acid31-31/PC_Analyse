using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PCAnalyse.Core;

public sealed class LinkListener : IDisposable
{
    private readonly PairingStore _store;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _tcp;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public string ComputerName { get; } = Environment.MachineName;
    public bool Waiting { get; set; } = true;
    public string Status { get; private set; } = "Getrennt";
    public string? ConnectedPeer { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<string, string>? PeerConnected;

    public LinkListener(PairingStore store)
    {
        _store = store;
    }

    public void Start()
    {
        TryOpenFirewall();
        _tcp = new TcpListener(IPAddress.Any, LinkPorts.Tcp);
        _tcp.Start();
        _ = Task.WhenAll(BroadcastLoopAsync(_cts.Token), AcceptLoopAsync(_cts.Token));
        SetStatus("Wartet auf Verbindung – kein Passwort nötig.");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _tcp?.Stop();
        _cts.Dispose();
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        using var sender = new UdpClient { EnableBroadcast = true };
        while (!token.IsCancellationRequested)
        {
            try
            {
                var packet = DiscoveryCodec.Encode(new DiscoveryPacket(InstanceId, ComputerName, LinkPorts.Tcp, Waiting, ""));
                await sender.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, LinkPorts.Udp));
            }
            catch
            {
                // Netz kurz nicht erreichbar.
            }

            try { await Task.Delay(2000, token); }
            catch (TaskCanceledException) { return; }
        }
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
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
        {
            await writer.WriteLineAsync(LinkMessage.ToLine(LinkMessage.Hello(InstanceId, ComputerName)));
            var line = await reader.ReadLineAsync(token);
            var message = LinkMessage.FromLine(line ?? "");
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
            SetStatus($"Verbunden mit {message.Name} – ohne Passwort.");
            PeerConnected?.Invoke(message.Id, message.Name);

            while (!token.IsCancellationRequested)
            {
                var keepAlive = await reader.ReadLineAsync(token);
                if (keepAlive is null)
                    break;
                if (LinkMessage.FromLine(keepAlive)?.Type == "ping")
                    await writer.WriteLineAsync(LinkMessage.ToLine(new LinkMessage { Type = "pong" }));
            }
        }
    }

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }

    private static void TryOpenFirewall()
    {
        try
        {
            foreach (var args in new[]
            {
                $"advfirewall firewall add rule name=\"PCAnalyse Verbindung UDP\" dir=in action=allow protocol=UDP localport={LinkPorts.Udp} profile=private,domain",
                $"advfirewall firewall add rule name=\"PCAnalyse Verbindung TCP\" dir=in action=allow protocol=TCP localport={LinkPorts.Tcp} profile=private,domain"
            })
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                })?.WaitForExit(4000);
            }
        }
        catch
        {
            // Ohne Admin-Recht kann UDP im privaten Netz trotzdem funktionieren.
        }
    }
}
