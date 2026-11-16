using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class AppVolumeTests
{
    [Theory]
    [InlineData("Spotify", "Spotify")]
    [InlineData("Chrome", null)]
    [InlineData("Internet Radio", null)]
    [InlineData(null, null)]
    public void Only_listed_media_apps_get_their_own_volume(string? mediaApp, string? process) =>
        Assert.Equal(process, AppVolumeApps.Process(mediaApp));
}
