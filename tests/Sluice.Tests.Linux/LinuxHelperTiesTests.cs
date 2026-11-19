using System.Diagnostics;
using Sluice.Platform.Linux;
using Proc = Sluice.Platform.Linux.HelperTies.Proc;

namespace Sluice.Tests;

/// <summary>
/// sing-box helpers end with Sluice (#57): tied helpers get SIGTERM when Sluice exits, and the helpers of a Sluice that
/// was killed with SIGKILL are ended by the next one, never a process that only reuses a helper's pid.
/// </summary>
public sealed class LinuxHelperTiesTests : IDisposable
{
    private const string Boot = "6f1c2b9e-0000-4000-8000-000000000001";
    private static readonly Proc Self = new(4000, 9000);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sluice-ties-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, ulong> _running = new();   // the fake /proc: pid → start time
    private readonly List<int> _terminated = new();

    public LinuxHelperTiesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private HelperTies Ties(Proc self) => new(_folder, self, Boot, pid => _running.TryGetValue(pid, out var s) ? s : null,
        pid => { _terminated.Add(pid); return true; });

    private string FileOf(Proc owner) => Path.Combine(_folder, $"{owner.Pid}-{owner.StartTime}{HelperTies.Extension}");

    [Fact]
    public void Start_time_is_field_22_even_with_spaces_and_parentheses_in_the_name()
    {
        const string stat = "4242 (sing (box) x) S 1 4242 4242 0 -1 4194560 1534 0 0 0 12 3 0 0 20 0 9 0 7319942 1281499136 4876 18446744073709551615";
        Assert.Equal(7319942UL, HelperTies.ParseStartTime(stat));
        Assert.Null(HelperTies.ParseStartTime("4242 (sing-box) S 1 2 3"));
        Assert.Null(HelperTies.ParseStartTime("garbage"));
    }

    [Fact]
    public void File_names_and_contents_round_trip()
    {
        Assert.Equal(new Proc(4000, 9000), HelperTies.ParseOwner("4000-9000.helpers"));
        Assert.Null(HelperTies.ParseOwner("4000-9000.helpers.new"));
        Assert.Null(HelperTies.ParseOwner("tunnel-1234.json"));
        Assert.Null(HelperTies.ParseOwner("x-9000.helpers"));

        var (boot, helpers) = HelperTies.Parse(HelperTies.Format(Boot, [new Proc(10, 100), new Proc(11, 110)]));
        Assert.Equal(Boot, boot);
        Assert.Equal([new Proc(10, 100), new Proc(11, 110)], helpers);
    }

    [Fact]
    public void Tying_writes_the_file_and_untying_the_last_helper_removes_it()
    {
        _running[10] = 100;
        _running[11] = 110;
        var ties = Ties(Self);

        ties.Tie(10);
        ties.Tie(11);
        ties.Tie(12);                                               // already gone: nothing to tie
        Assert.Equal([new Proc(10, 100), new Proc(11, 110)], HelperTies.Parse(File.ReadAllText(FileOf(Self))).Helpers);

        ties.Untie(10);
        Assert.Equal([new Proc(11, 110)], HelperTies.Parse(File.ReadAllText(FileOf(Self))).Helpers);
        ties.Untie(11);
        Assert.False(File.Exists(FileOf(Self)));
    }

    [Fact]
    public void Exiting_ends_the_helpers_still_running_but_not_a_reused_pid()
    {
        _running[10] = 100;
        _running[11] = 110;
        var ties = Ties(Self);
        ties.Tie(10);
        ties.Tie(11);
        _running[11] = 555;                                         // helper 11 ended; its pid now belongs to another process

        Assert.Equal(1, ties.EndAll());
        Assert.Equal([10], _terminated);
        Assert.Empty(ties.Tied);
        Assert.False(File.Exists(FileOf(Self)));
    }

    [Fact]
    public void A_helper_started_while_Sluice_exits_is_ended_at_once()
    {
        _running[10] = 100;
        var ties = Ties(Self);
        ties.EndAll();                                              // a signal arrived; Sluice is shutting down

        ties.Tie(10);                                               // a tunnel started a new helper meanwhile
        Assert.Equal([10], _terminated);
        Assert.Empty(ties.Tied);
        Assert.False(File.Exists(FileOf(Self)));
    }

    [Fact]
    public void Leftovers_of_a_killed_Sluice_are_ended_and_a_running_Sluice_is_left_alone()
    {
        var killed = new Proc(3000, 8000);                          // not running any more
        var running = new Proc(3001, 8001);
        var otherBoot = new Proc(3002, 8002);
        _running[running.Pid] = running.StartTime;
        _running[20] = 200;                                         // the killed Sluice's helper, still running
        _running[21] = 999;                                         // its other helper's pid, reused by something else
        _running[30] = 300;                                         // the running Sluice's helper
        _running[40] = 400;

        File.WriteAllText(FileOf(killed), HelperTies.Format(Boot, [new Proc(20, 200), new Proc(21, 210)]));
        File.WriteAllText(FileOf(running), HelperTies.Format(Boot, [new Proc(30, 300)]));
        File.WriteAllText(FileOf(otherBoot), HelperTies.Format("an-earlier-boot", [new Proc(40, 400)]));
        File.WriteAllText(Path.Combine(_folder, "notes.helpers"), "boot " + Boot + "\n50 500\n");   // not a name of ours
        _running[50] = 500;

        var ties = Ties(Self);
        ties.Tie(40);                                               // this Sluice's own file is skipped too
        _running.Remove(Self.Pid);

        Assert.Equal(1, ties.EndLeftovers());
        Assert.Equal([20], _terminated);
        Assert.False(File.Exists(FileOf(killed)));
        Assert.True(File.Exists(FileOf(running)));
        Assert.False(File.Exists(FileOf(otherBoot)));               // pids from an earlier boot name nothing: just removed
        Assert.True(File.Exists(FileOf(Self)));
        Assert.True(File.Exists(Path.Combine(_folder, "notes.helpers")));
    }

    [LinuxFact]
    public void A_real_helper_left_by_a_killed_Sluice_is_ended_by_the_next_one()
    {
        using var helper = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        try
        {
            var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            var start = HelperTies.ReadStartTime(helper.Id);
            Assert.NotNull(start);
            var dead = new Proc(int.MaxValue - 1, 1);              // a Sluice that is gone
            File.WriteAllText(FileOf(dead), HelperTies.Format(boot, [new Proc(helper.Id, start!.Value)]));

            var self = new Proc(Environment.ProcessId, HelperTies.ReadStartTime(Environment.ProcessId)!.Value);
            var ties = new HelperTies(_folder, self, boot, pid => HelperTies.ReadStartTime(pid),
                pid => Sluice.Platform.Linux.Native.Libc.kill(pid, Sluice.Platform.Linux.Native.Libc.SIGTERM) == 0);

            Assert.Equal(1, ties.EndLeftovers());
            Assert.True(helper.WaitForExit(5000), "the helper is still running");
            Assert.False(File.Exists(FileOf(dead)));
        }
        finally
        {
            if (!helper.HasExited) helper.Kill();
        }
    }
}
