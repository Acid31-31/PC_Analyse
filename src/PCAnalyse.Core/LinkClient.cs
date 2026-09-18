using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PCAnalyse.Core;

public static class LinkClient
{
    public static async Task<LinkMessage> ConnectAsync(
        IPEndPoint endPoint,
        string localId,
        string localName,
        string existingToken,
        CancellationToken token = default)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(endPoint, timeout.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        _ = await reader.ReadLineAsync(timeout.Token);
        await writer.WriteLineAsync(LinkMessage.ToLine(LinkMessage.Pair(localId, localName, existingToken)));
        var responseLine = await reader.ReadLineAsync(timeout.Token);
        var response = LinkMessage.FromLine(responseLine ?? "")
                       ?? LinkMessage.Reject("Keine Antwort vom zweiten PC.");
        if (response.Type == "paired")
        {
            await writer.WriteLineAsync(LinkMessage.ToLine(new LinkMessage { Type = "ping" }));
            _ = await reader.ReadLineAsync(timeout.Token);
        }

        return response;
    }
}
