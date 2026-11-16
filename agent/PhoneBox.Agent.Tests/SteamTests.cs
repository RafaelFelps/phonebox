using PhoneBox.Agent;
using Xunit;

/// <summary>Reading the Steam client's files (VDF), news text and the image hosts the agent may fetch.</summary>
public sealed class SteamTests
{
    private const string Manifest = """
        "AppState"
        {
        	"appid"		"1086940"
        	"name"		"Baldur's Gate 3"
        	"StateFlags"		"1026"
        	"SizeOnDisk"		"150000000000"
        	"BytesToDownload"		"2000"
        	"BytesDownloaded"		"500"
        	"BytesToStage"		"3000"
        	"BytesStaged"		"0"
        	"InstalledDepots"
        	{
        		"1086941" { "manifest" "123" "size" "42" }
        	}
        }
        """;

    [Fact]
    public void Parses_nested_blocks_quotes_and_comments()
    {
        var v = Vdf.Parse("""
            // a comment
            "root"
            {
                "Key"   "va\"lue"
                "child" { "n" "1" }
                bare token
            }
            """);
        var root = v.Child("root")!;
        Assert.Equal("va\"lue", root["key"]);            // keys are case-insensitive
        Assert.Equal(1, root.Path("child")!.Long("n"));
        Assert.Equal("token", root["bare"]);
        Assert.Null(root["missing"]);
        Assert.Equal(["Key", "child", "bare"], root.Keys);
    }

    [Fact]
    public void Reads_a_game_that_is_updating()
    {
        var g = SteamLocal.ParseManifest(Vdf.Parse(Manifest), @"D:\SteamLibrary")!;
        Assert.Equal(1086940, g.AppId);
        Assert.Equal("Baldur's Gate 3", g.Name);
        Assert.True(g.Downloading);
        Assert.Equal(500, g.BytesDownloaded);
        Assert.Equal(2000, g.BytesToDownload);
    }

    [Fact]
    public void Leaves_out_redistributables_and_finished_installs()
    {
        var redist = Manifest.Replace("1086940", "228980").Replace("Baldur's Gate 3", "Steamworks Common Redistributables");
        Assert.Null(SteamLocal.ParseManifest(Vdf.Parse(redist), "x"));
        var done = Manifest.Replace("\"1026\"", "\"4\"").Replace("\"500\"", "\"2000\"").Replace("\"BytesStaged\"\t\t\"0\"", "\"BytesStaged\"\t\t\"3000\"");
        Assert.False(SteamLocal.ParseManifest(Vdf.Parse(done), "x")!.Downloading);
    }

    [Fact]
    public void Cleans_news_markup()
    {
        var text = SteamMonitor.CleanNews("[h1]Patch 7[/h1] [b]New:[/b]  <i>photo</i> mode &amp; more {STEAM_CLAN_IMAGE}/abc/def.png");
        Assert.Equal("Patch 7 New: photo mode & more", text);
        Assert.EndsWith("…", SteamMonitor.CleanNews(new string('a', 400)));
    }

    [Fact]
    public void Finds_covers_in_old_and_new_cache_layouts()
    {
        var root = Directory.CreateTempSubdirectory("steam").FullName;
        try
        {
            var cache = Path.Combine(root, "appcache", "librarycache");
            Directory.CreateDirectory(Path.Combine(cache, "20", "abc"));
            File.WriteAllText(Path.Combine(cache, "10_library_600x900.jpg"), "x");
            File.WriteAllText(Path.Combine(cache, "20", "abc", "library_capsule.jpg"), "x");
            File.WriteAllText(Path.Combine(cache, "20", "abc", "library_header_brazilian.jpg"), "x");
            File.WriteAllText(Path.Combine(cache, "20", "abc", "library_hero_blur.jpg"), "x");
            File.WriteAllText(Path.Combine(cache, "20", "abc", "library_hero.jpg"), "x");
            Assert.EndsWith("10_library_600x900.jpg", SteamLocal.CoverPath(root, 10, "portrait"));
            Assert.EndsWith("library_capsule.jpg", SteamLocal.CoverPath(root, 20, "portrait"));
            Assert.EndsWith("library_header_brazilian.jpg", SteamLocal.CoverPath(root, 20, "header"));
            Assert.EndsWith("library_hero.jpg", SteamLocal.CoverPath(root, 20, "hero"));
            Assert.Null(SteamLocal.CoverPath(root, 30, "portrait"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("https://avatars.steamstatic.com/abc_full.jpg", true)]
    [InlineData("https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/1/a.jpg", true)]
    [InlineData("https://steamcdn-a.akamaihd.net/x.jpg", true)]
    [InlineData("http://avatars.steamstatic.com/abc.jpg", false)]
    [InlineData("https://evil.example.com/x.jpg", false)]
    [InlineData("https://steamstatic.com.evil.example/x.jpg", false)]
    public void Fetches_images_only_from_steam(string url, bool allowed) =>
        Assert.Equal(allowed, SteamMonitor.SteamImageHost(new Uri(url)));
}
