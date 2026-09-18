using PCAnalyse.Core;
using Xunit;

namespace PCAnalyse.Tests;

public class HostnameValidatorTests
{
    [Theory]
    [InlineData("WIN-G2OC48399EJ", "WIN-G2OC48399EJ")]
    [InlineData("  192.168.1.10  ", "192.168.1.10")]
    [InlineData(@"\\BUERO-PC\Data", "BUERO-PC")]
    public void Accepts_valid_hosts(string input, string expected)
    {
        Assert.True(HostnameValidator.TryNormalize(input, out var host, out var error));
        Assert.Equal(expected, host);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pc/name")]
    [InlineData("host:8080")]
    [InlineData("zwei woerter")]
    public void Rejects_invalid_hosts(string input)
    {
        Assert.False(HostnameValidator.TryNormalize(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public class PcRegistryTests
{
    [Fact]
    public void Roundtrip_add_and_update()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcanalyse-tests", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var registry = new PcRegistry(path);
            registry.AddOrUpdate(new RegisteredPc { DisplayName = "Büro", Hostname = "BUERO-PC" });
            var second = registry.AddOrUpdate(new RegisteredPc { DisplayName = "Büro 2", Hostname = "BUERO-PC" });
            Assert.Single(second);
            Assert.Equal("Büro 2", second[0].DisplayName);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

public class CursorWorkerCommandTests
{
    [Fact]
    public void Builds_start_command()
    {
        var command = CursorWorkerCommand.BuildStartCommand("Zweiter-PC", @"Z:\PC_Analyse");
        Assert.Contains("--name \"Zweiter-PC\"", command);
        Assert.Contains("--worker-dir \"Z:\\PC_Analyse\"", command);
    }
}

public class DiscoveryCodecTests
{
    [Fact]
    public void Roundtrip_packet()
    {
        var original = new DiscoveryPacket("abc123", "BUERO-PC", 49582, true, "192.168.1.20");
        Assert.True(DiscoveryCodec.TryDecode(DiscoveryCodec.Encode(original), out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Rejects_garbage()
    {
        Assert.False(DiscoveryCodec.TryDecode("NOPE"u8.ToArray(), out _));
    }
}

public class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.1", 1, 0, 1)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v2.0", 2, 0, 0)]
    public void Parses_release_tags(string tag, int major, int minor, int build)
    {
        Assert.True(GitHubUpdateService.TryParseReleaseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void Extracts_named_sha256()
    {
        var notes = "SHA256 PCAnalyse-MeinPC.zip: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n"
                    + "SHA256 PCAnalyse-ZweiterPC.zip: fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            GitHubUpdateService.ExtractSha256(notes, "PCAnalyse-MeinPC.zip"));
        Assert.Equal(
            "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
            GitHubUpdateService.ExtractSha256(notes, "PCAnalyse-ZweiterPC.zip"));
    }
}
