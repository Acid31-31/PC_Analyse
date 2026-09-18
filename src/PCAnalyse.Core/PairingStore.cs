using System.Security.Cryptography;
using System.Text.Json;

namespace PCAnalyse.Core;

public sealed class PairingStore
{
    private readonly string _filePath;

    public PairingStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCAnalyse",
            "pairing.bin");
    }

    public Dictionary<string, string> Load()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var protectedBytes = File.ReadAllBytes(_filePath);
            var json = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return loaded is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveToken(string peerId, string token)
    {
        var map = Load();
        map[peerId] = token;
        Save(map);
    }

    public string? GetToken(string peerId)
    {
        Load().TryGetValue(peerId, out var token);
        return token;
    }

    private void Save(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(map);
        var protectedBytes = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_filePath, protectedBytes);
    }
}
