using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCAnalyse.Core;

public sealed class LinkMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    public static LinkMessage Hello(string id, string name) =>
        new() { Type = "hello", Id = id, Name = name };

    public static LinkMessage Pair(string id, string name, string token) =>
        new() { Type = "pair", Id = id, Name = name, Token = token };

    public static LinkMessage Paired(string id, string name, string token) =>
        new() { Type = "paired", Id = id, Name = name, Token = token };

    public static LinkMessage Reject(string reason) =>
        new() { Type = "reject", Reason = reason };

    public static string ToLine(LinkMessage message) =>
        JsonSerializer.Serialize(message);

    public static LinkMessage? FromLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        line = line.Trim().Trim('\uFEFF');
        try
        {
            return JsonSerializer.Deserialize<LinkMessage>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
