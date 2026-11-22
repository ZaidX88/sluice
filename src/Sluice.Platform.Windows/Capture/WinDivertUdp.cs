using System.Net;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Platform;

namespace Sluice.Platform.Windows.Capture;

/// <summary>
/// Windows UDP over Core's <see cref="UdpFlows"/> (docs/architecture/platforms.md §6.4): each captured outbound packet
/// becomes an <see cref="AppDatagram"/> whose context is its WinDivert address, and a reply to the app is built as an
/// inbound packet (<see cref="UdpPacket.Build"/>) and injected with that address.
/// </summary>
internal sealed class WinDivertUdp : IDatagramSender, IAsyncDisposable
{
    /// <summary>Injects a complete inbound IP packet; <c>context</c> is what the capture passed with the app's packet.</summary>
    public delegate void Injector(byte[] packet, object? context);

    private readonly UdpFlows _flows;
    private readonly Injector _inject;

    public WinDivertUdp(RoutingEngine engine, FakeIpPool fakes, Injector inject, Func<IPEndPoint, int>? findOwner = null)
    {
        _inject = inject;
        _flows = new UdpFlows(engine, fakes, this, findOwner ?? ProcessLookup.FindUdpOwner);
    }

    public int FlowCount => _flows.FlowCount;
    public long DatagramsTunneled => _flows.DatagramsTunneled;
    public long DatagramsDropped => _flows.DatagramsDropped;

    /// <summary>Inspects one outbound UDP packet (the first <paramref name="length"/> bytes of <paramref name="packet"/>).
    /// Drop: taken (tunneled, sent by Sluice, or blocked); the capture must not send it on. Not an IP/UDP packet: Pass.</summary>
    public UdpFlows.Verdict ProcessOutbound(byte[] packet, int length, object? context)
    {
        if (!UdpPacket.TryParse(packet.AsSpan(0, length), out var src, out var dst, out var off, out var len)) return UdpFlows.Verdict.Pass;
        return _flows.Handle(new AppDatagram(src, dst, packet.AsMemory(off, len), context));   // UdpFlows copies the payload
    }

    /// <summary>Ends flows idle for longer than <paramref name="idle"/>.</summary>
    public void Sweep(TimeSpan idle) => _flows.Sweep(idle);

    void IDatagramSender.SendToApp(IPEndPoint from, IPEndPoint app, ReadOnlyMemory<byte> payload, object? context) =>
        _inject(UdpPacket.Build(from, app, payload.Span), context);

    public ValueTask DisposeAsync() => _flows.DisposeAsync();
}
