using System.Text.Json;

namespace PCAnalyse.Core;

public sealed class PcRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public PcRegistry(string filePath)
    {
        _filePath = filePath;
    }

    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCAnalyse", "machines.json");

    public IReadOnlyList<RegisteredPc> Load()
    {
        if (!File.Exists(_filePath))
            return Array.Empty<RegisteredPc>();

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<List<RegisteredPc>>(json, JsonOptions) ?? new List<RegisteredPc>();
    }

    public void Save(IEnumerable<RegisteredPc> machines)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var list = machines.ToList();
        File.WriteAllText(_filePath, JsonSerializer.Serialize(list, JsonOptions));
    }

    public IReadOnlyList<RegisteredPc> AddOrUpdate(RegisteredPc machine)
    {
        var list = Load().ToList();
        var existing = list.FindIndex(x =>
            string.Equals(x.Hostname, machine.Hostname, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            machine.Id = list[existing].Id;
            machine.AddedAt = list[existing].AddedAt;
            list[existing] = machine;
        }
        else
        {
            list.Add(machine);
        }

        Save(list);
        return list;
    }

    public IReadOnlyList<RegisteredPc> Remove(string id)
    {
        var list = Load().Where(x => x.Id != id).ToList();
        Save(list);
        return list;
    }
}
