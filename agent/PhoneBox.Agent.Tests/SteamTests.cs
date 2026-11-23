using PhoneBox.Agent.MySteam;
using PhoneBox.Agent;
using Xunit;

/// <summary>Reading the Steam client's files (VDF) and the image hosts the agent may fetch.</summary>
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

    /// <summary>Writes binary KeyValues: (key, value) pairs; a value that is an array opens a block.</summary>
    private static byte[] Bin(params (string Key, object Value)[] items)
    {
        var ms = new MemoryStream();
        void Key(string k) { var b = System.Text.Encoding.UTF8.GetBytes(k); ms.Write(b); ms.WriteByte(0); }
        void Write((string Key, object Value)[] list)
        {
            foreach (var (k, v) in list)
            {
                switch (v)
                {
                    case (string, object)[] block: ms.WriteByte(0); Key(k); Write(block); ms.WriteByte(8); break;
                    case string s: ms.WriteByte(1); Key(k); Key(s); break;
                    case int n: ms.WriteByte(2); Key(k); ms.Write(BitConverter.GetBytes(n)); break;
                }
            }
        }
        Write(items);
        ms.WriteByte(8);
        return ms.ToArray();
    }

    private static (string, object)[] B(params (string, object)[] items) => items;

    [Fact]
    public void Reads_achievements_from_the_clients_stats_files()
    {
        var root = Directory.CreateTempSubdirectory("steam").FullName;
        try
        {
            var stats = Path.Combine(root, "appcache", "stats");
            Directory.CreateDirectory(stats);
            var ach = (string api, string en, string br, object hidden) => (api, (object)B(("name", api), ("display", B(
                ("name", B(("english", en), ("brazilian", br), ("token", "X"))),
                ("desc", B(("english", en + " desc"))),
                ("hidden", hidden), ("icon", api + ".jpg"), ("icon_gray", api + "_g.jpg")))));
            File.WriteAllBytes(Path.Combine(stats, "UserGameStatsSchema_42.bin"), Bin(("42", B(("stats", B(
                ("1", B(("bits", B(
                    ("0", ach("WINGS", "Wings", "Asas", 0).Item2),
                    ("1", ach("CROWN", "Crown", "Coroa", 0).Item2),
                    ("2", ach("SECRET", "Secret", "Segredo", "1").Item2))))),
                ("2", B(("type", 1)))))))));                       // a plain stat without achievements
            // bits 0 and 2 unlocked; -1 would be all 32
            File.WriteAllBytes(Path.Combine(stats, "UserGameStats_7_42.bin"), Bin(("cache", B(("crc", 123),
                ("1", B(("data", 0b101), ("AchievementTimes", B(("0", 1_700_000_000), ("2", 1_700_000_500)))))))));
            var steamId = SteamLocal.SteamId64Base + 7;

            var list = SteamLocal.Achievements(root, steamId, 42, "brazilian")!;
            Assert.Equal(["Asas", "Coroa", "Segredo"], list.Select(a => a.Name));
            Assert.Equal([true, false, true], list.Select(a => a.Achieved));
            Assert.Equal(1_700_000_500, list[2].UnlockedAt);
            Assert.Equal(0, list[1].UnlockedAt);
            Assert.True(list[2].Hidden);
            Assert.Equal("Wings desc", list[0].Description);           // falls back to English
            Assert.Equal("CROWN_g.jpg", list[1].IconGray);
            Assert.Null(SteamLocal.Achievements(root, steamId, 43, "brazilian")); // never opened on this PC
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("abc.jpg", "https://shared.akamai.steamstatic.com/community_assets/images/apps/42/abc.jpg")]
    [InlineData("https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/42/abc.jpg", "https://shared.akamai.steamstatic.com/community_assets/images/apps/42/abc.jpg")]
    [InlineData("https://avatars.steamstatic.com/x.jpg", "https://avatars.steamstatic.com/x.jpg")]
    [InlineData(null, null)]
    public void Achievement_icons_use_the_current_cdn(string? icon, string? url) =>
        Assert.Equal(url, SteamMonitor.IconUrl(42, icon));

    [Fact]
    public void Binary_reader_stops_cleanly_on_a_truncated_file()
    {
        var bytes = Bin(("a", B(("n", 5), ("s", "text")))).AsSpan(0, 12).ToArray();
        var v = Vdf.ParseBinary(bytes);
        Assert.Equal(5, v.Child("a")!.Long("n"));
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

    [Fact]
    public void Sees_an_update_steam_has_not_started()
    {
        // installed (4) + update required (2), nothing downloaded yet: waiting for Steam's schedule
        var waiting = Manifest.Replace("\"1026\"", "\"6\"").Replace("\"2000\"", "\"0\"").Replace("\"500\"", "\"0\"").Replace("\"3000\"", "\"0\"");
        var g = SteamLocal.ParseManifest(Vdf.Parse(waiting), "x")!;
        Assert.False(g.Downloading);
        Assert.True(g.UpdateAvailable);

        var current = waiting.Replace("\"6\"", "\"4\"");
        Assert.False(SteamLocal.ParseManifest(Vdf.Parse(current), "x")!.UpdateAvailable);

        var scheduled = current.Replace("\"SizeOnDisk\"", "\"ScheduledAutoUpdate\"\t\t\"1794900000\"\n\t\"SizeOnDisk\"");
        Assert.True(SteamLocal.ParseManifest(Vdf.Parse(scheduled), "x")!.UpdateAvailable);

        // already downloading: shown as a download, not as a pending update
        Assert.False(SteamLocal.ParseManifest(Vdf.Parse(Manifest), "x")!.UpdateAvailable);
    }

    [Fact]
    public void Follows_an_update_in_the_content_log()
    {
        var log = new SteamContentLog();
        log.Feed("[2026-11-17 09:46:05] AppID 2406770 state changed : Update Required,Update Queued,Update Running,");
        log.Feed("[2026-11-17 09:46:07] AppID 2406770 App update changed : Running Update,Verifying Installed,");
        Assert.Equal("Running Update,Verifying Installed", log.Get(2406770)!.Step);

        log.Feed("[2026-11-17 09:46:07] AppID 2406770 update started : download 0/66164575056, store 0/0, reuse 0/0, delta 0/0, stage 0/74623415803 ");
        log.Feed("[2026-11-17 09:46:07] AppID 2406770 App update changed : Running Update,Downloading,Staging,");
        log.Feed("[2026-11-17 10:07:37] Current download rate: 342.483 Mbps");
        var app = log.Get(2406770)!;
        Assert.Equal(66164575056, app.DownloadTotal);
        Assert.Equal(74623415803, app.StageTotal);
        Assert.Contains("Downloading", app.Step);
        Assert.Contains("Update Running", app.State);
        Assert.Equal(342.483e6 / 8, log.Rate, 3);
        Assert.Equal(new DateTime(2026, 11, 17, 10, 7, 37), log.RateAt);

        log.Feed("[2026-11-17 10:09:35] AppID 2406770 scheduler finished : removed from schedule (result No Error, state 0xc) ");
        Assert.Null(log.Get(2406770));
        // noise is ignored
        log.Feed("[2026-11-17 10:10:30] HTTP/2 (SteamCache,460) - cache18: Connection has been idle for '61' seconds, closing");
        log.Feed("garbage");
    }
}
