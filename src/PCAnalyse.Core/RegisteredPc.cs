namespace PCAnalyse.Core;

public sealed class RegisteredPc
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public bool IsLocalWorker { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}
