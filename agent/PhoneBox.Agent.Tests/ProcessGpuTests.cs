using PhoneBox.Agent;
using Xunit;

/// <summary>Per-process GPU load from "GPU Engine" counter instances, as Task Manager computes it.</summary>
public sealed class ProcessGpuTests
{
    [Fact]
    public void Takes_the_busiest_engine_of_each_process()
    {
        var rows = new (string, double)[]
        {
            ("pid_100_luid_0x00000000_0x0001C621_phys_0_eng_0_engtype_3D", 30),
            ("pid_100_luid_0x00000000_0x0001C621_phys_0_eng_0_engtype_3D", 12.5),   // a second instance on the same engine adds up
            ("pid_100_luid_0x00000000_0x0001C621_phys_0_eng_3_engtype_VideoDecode", 20),
            ("pid_200_luid_0x00000000_0x0001C621_phys_0_eng_3_engtype_VideoDecode", 7),
            ("pid_300_luid_0x00000000_0x0001C621_phys_0_eng_0_engtype_3D", 0),
            ("pid_0_luid_0x00000000_0x0001C621_phys_0_eng_0_engtype_3D", 50),        // no process
            ("luid_0x00000000_0x0001C621_phys_0", 99),                                 // not a process instance
            ("pid_400_luid_0x00000000_0x0001C621_phys_0_eng_0_engtype_3D", 140),     // capped
        };
        var byPid = ProcessMonitor.GpuByPid(rows);
        Assert.Equal(42.5, byPid[100], 6);
        Assert.Equal(7, byPid[200], 6);
        Assert.Equal(100, byPid[400], 6);
        Assert.False(byPid.ContainsKey(300));
        Assert.False(byPid.ContainsKey(0));
        Assert.Equal(3, byPid.Count);
    }
}
