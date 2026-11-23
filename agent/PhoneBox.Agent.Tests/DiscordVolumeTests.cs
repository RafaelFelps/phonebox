using PhoneBox.Agent.MyDiscord;
using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>Reading Discord's per-user volume from RPC voice states.</summary>
public sealed class DiscordVolumeTests
{
    [Theory]
    [InlineData("""{"volume": 100}""", 100)]
    [InlineData("""{"volume": 57.6}""", 58)]
    [InlineData("""{"volume": 250}""", 200)]
    [InlineData("""{"volume": -3}""", 0)]
    [InlineData("""{"volume": null}""", 100)]
    [InlineData("""{}""", 100)]
    public void Reads_the_volume(string json, int expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, DiscordMonitor.Volume(doc.RootElement));
    }
}
