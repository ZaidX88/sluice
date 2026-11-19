using System.Net;
using Sluice.Core.Inbound;
using Sluice.Platform.Linux.Capture;

namespace Sluice.Tests;

/// <summary>
/// Issue #38: Linux lets two connections share a source port when their destinations differ. After nftables'
/// <c>redirect</c> both go to the relay, so the flow records must be keyed by source and destination, and the relay must
/// find a connection by its original tuple (conntrack), which also covers a source port conntrack renumbered.
/// </summary>
public class LinuxFlowRecordsTests
{
    private static readonly IPEndPoint App = IPEndPoint.Parse("192.0.2.10:40000");
    private static readonly IPEndPoint ServerA = IPEndPoint.Parse("198.51.100.1:80");
    private static readonly IPEndPoint ServerB = IPEndPoint.Parse("198.51.100.2:80");
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A conntrack stand-in: the relay sees <c>seen</c>, the connection was <c>original</c>.</summary>
    private static FlowRecords.OriginalLookup Conntrack(params (IPEndPoint Seen, IPEndPoint Source, IPEndPoint Destination)[] entries) =>
        (IPEndPoint client, out IPEndPoint source, out IPEndPoint destination) =>
        {
            foreach (var e in entries)
                if (e.Seen.Equals(client))
                {
                    (source, destination) = (e.Source, e.Destination);
                    return true;
                }
            source = destination = null!;
            return false;
        };

    [Fact]
    public void Two_records_with_the_same_source_and_different_destinations_are_both_kept_and_found()
    {
        var flows = new FlowRecords();
        flows.Add(App, ServerA, new OriginalDestination(ServerA, 1, "a.example"), Now);
        flows.Add(App, ServerB, new OriginalDestination(ServerB, 2, "b.example"), Now);     // used to overwrite A's record
        Assert.Equal(2, flows.Count);

        // The second connection's source port was renumbered on its way to the relay (40000 -> 1130).
        var renumbered = new IPEndPoint(App.Address, 1130);
        var lookup = Conntrack((App, App, ServerA), (renumbered, App, ServerB));

        Assert.True(flows.TryTake(renumbered, lookup, out var b, out var howB));
        Assert.Equal((ServerB, "b.example"), (b.Destination, b.Hostname));
        Assert.Contains("renumbered from 40000", howB);

        Assert.True(flows.TryTake(App, lookup, out var a, out var howA));
        Assert.Equal((ServerA, "a.example"), (a.Destination, a.Hostname));
        Assert.Equal("conntrack", howA);
        Assert.Equal(0, flows.Count);
    }

    [Fact]
    public void A_conntrack_answer_without_a_record_takes_nothing()
    {
        var flows = new FlowRecords();
        flows.Add(App, ServerA, new OriginalDestination(ServerA, 1), Now);

        Assert.False(flows.TryTake(App, Conntrack((App, App, ServerB)), out _, out var how));
        Assert.Contains("no record", how);
        Assert.Equal(1, flows.Count);                       // not handed out to the wrong connection
    }

    [Fact]
    public void Without_conntrack_the_only_record_for_the_source_is_used()
    {
        var flows = new FlowRecords();
        flows.Add(App, ServerA, new OriginalDestination(ServerA, 1), Now);

        Assert.True(flows.TryTake(App, null, out var a, out var how));
        Assert.Equal(ServerA, a.Destination);
        Assert.Equal("by source", how);

        // conntrack knows nothing about it (an entry that already expired): same fallback
        flows.Add(App, ServerB, new OriginalDestination(ServerB, 1), Now);
        Assert.True(flows.TryTake(App, Conntrack(), out var b, out _));
        Assert.Equal(ServerB, b.Destination);
    }

    [Fact]
    public void Without_conntrack_two_records_for_one_source_are_not_guessed()
    {
        var flows = new FlowRecords();
        flows.Add(App, ServerA, new OriginalDestination(ServerA, 1), Now);
        flows.Add(App, ServerB, new OriginalDestination(ServerB, 2), Now);

        Assert.False(flows.TryTake(App, null, out _, out var how));
        Assert.Equal("several records for this source", how);
        Assert.Equal(2, flows.Count);
    }

    [Fact]
    public void Mapped_ipv4_addresses_match_plain_ones_and_ipv6_works()
    {
        var flows = new FlowRecords();
        flows.Add(new IPEndPoint(App.Address.MapToIPv6(), App.Port), ServerA, new OriginalDestination(ServerA, 1), Now);
        Assert.True(flows.TryTake(App, Conntrack((App, App, ServerA)), out _, out _));

        var app6 = IPEndPoint.Parse("[2001:db8::10]:40000");
        var server6 = IPEndPoint.Parse("[2001:db8::80]:443");
        flows.Add(app6, server6, new OriginalDestination(server6, 1), Now);
        Assert.True(flows.TryTake(app6, Conntrack((app6, app6, server6)), out var r, out _));
        Assert.Equal(server6, r.Destination);
    }

    [Fact]
    public void Old_records_are_swept()
    {
        var flows = new FlowRecords();
        flows.Add(App, ServerA, new OriginalDestination(ServerA, 1), Now - TimeSpan.FromMinutes(2));
        flows.Add(App, ServerB, new OriginalDestination(ServerB, 1), Now);

        flows.Sweep(Now, TimeSpan.FromMinutes(1));
        Assert.Equal(1, flows.Count);
        Assert.True(flows.TryTake(App, null, out var left, out _));
        Assert.Equal(ServerB, left.Destination);
    }
}
