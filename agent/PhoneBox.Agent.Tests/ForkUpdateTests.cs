using PhoneBox.Agent;
using Xunit;

/// <summary>
/// This fork must never pull the original's releases by itself: a config the agent cannot read at boot falls back to
/// the defaults, and those once installed the original over the fork's changes.
/// </summary>
public sealed class ForkUpdateTests
{
    [Fact]
    public void Defaults_do_not_update_from_the_original()
    {
        var u = new UpdateConfig();
        Assert.False(u.Auto);
        Assert.Equal("RafaelFelps/phonebox", u.Repo);
    }

    [Fact]
    public void An_unreadable_config_does_not_turn_updates_on()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("pbcfg").FullName, "config.json");
        File.WriteAllText(path, "{ not json");
        var cfg = new ConfigStore(path).Get();
        Assert.False(cfg.Updates.Auto);
        Assert.Equal(UpdateConfig.ForkRepo, cfg.Updates.Repo);
    }
}
