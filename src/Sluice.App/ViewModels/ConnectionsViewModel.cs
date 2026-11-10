using System.Collections.ObjectModel;
using System.Windows.Input;
using Sluice.App.Mvvm;
using Sluice.App.Services;
using Sluice.Core.Engine;

namespace Sluice.App.ViewModels;

public sealed class ConnectionRow : ObservableObject
{
    private string _sent = "", _received = "", _duration = "", _state = "";

    public ConnectionRow(ConnectionEntry e)
    {
        Entry = e;
        Update();
    }

    public ConnectionEntry Entry { get; }
    public long Id => Entry.Id;
    public string Application => Entry.ProcessName;
    public string ProcessDetail => Entry.ProcessPath is null ? $"PID {Entry.ProcessId}" : $"{Entry.ProcessPath}  (PID {Entry.ProcessId})";
    public string Target => Entry.Target;
    public string Rule => Entry.RuleName;
    public string Via => Entry.Via;
    public bool IsDirect => Entry.Via == "Direct";

    public string State { get => _state; private set => Set(ref _state, value); }
    public string Sent { get => _sent; private set => Set(ref _sent, value); }
    public string Received { get => _received; private set => Set(ref _received, value); }
    public string Duration { get => _duration; private set => Set(ref _duration, value); }

    public void Update()
    {
        State = Entry.State == ConnectionState.Connecting ? "Connecting" : "Open";
        Sent = Format.Bytes(Entry.BytesSent);
        Received = Format.Bytes(Entry.BytesReceived);
        Duration = Format.Duration(DateTime.Now - Entry.StartedAt);
    }
}

public sealed class ConnectionsViewModel : PageViewModel
{
    private ConnectionRow? _selected;
    private string _filter = "";

    public ConnectionsViewModel(EngineHost host)
        : base(host, "Connections", "", "Live connections from all applications, and how each one is routed.")
    {
        DisconnectCommand = new RelayCommand(() => Selected?.Entry.Disconnect(), () => Selected is not null);
        DisconnectAllCommand = new RelayCommand(() => Host.Tracker.DisconnectAll(), () => Rows.Count > 0);
        UpdateHint();
    }

    public ObservableCollection<ConnectionRow> Rows { get; } = new();
    public ConnectionRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public ICommand DisconnectCommand { get; }
    public ICommand DisconnectAllCommand { get; }

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) Refresh(); }
    }

    public bool IsEmpty => Rows.Count == 0;

    private string? _routingHint;

    /// <summary>Explains an empty list when nothing in the profile actually routes traffic.</summary>
    public string? RoutingHint { get => _routingHint; private set { if (Set(ref _routingHint, value)) Raise(nameof(HasRoutingHint)); } }
    public bool HasRoutingHint => _routingHint is not null;

    private void UpdateHint()
    {
        var o = Host.Profile.Options;
        var routes = Host.Profile.Rules.Any(r => (r.Enabled || r.IsDefault) && r.Action.Type is not Core.Profiles.RuleActionType.Direct);
        RoutingHint = routes || o.HandleDirectConnections
            ? null
            : Host.Profile.Tunnels.Count == 0
                ? "No tunnels yet, and every rule is Direct: captured traffic goes straight out (and isn't listed here). Add a tunnel on the Tunnels page."
                : "Every rule is Direct: captured traffic goes straight out without a tunnel (and isn't listed here). On the Rules page, set the Default rule — or another rule — to use your tunnel.";
    }

    public override void OnProfileChanged() => UpdateHint();

    public override void Refresh()
    {
        UpdateHint();
        var live = Host.Tracker.Active
            .Where(Matches)
            .OrderBy(e => e.Id)
            .ToList();
        var ids = live.Select(e => e.Id).ToHashSet();

        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!ids.Contains(Rows[i].Id)) Rows.RemoveAt(i);

        var known = Rows.Select(r => r.Id).ToHashSet();
        foreach (var r in Rows) r.Update();
        foreach (var e in live.Where(e => !known.Contains(e.Id))) Rows.Add(new ConnectionRow(e));
        Raise(nameof(IsEmpty));
    }

    private bool Matches(ConnectionEntry e) =>
        _filter.Length == 0 ||
        e.ProcessName.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        e.Target.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        e.RuleName.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        e.Via.Contains(_filter, StringComparison.OrdinalIgnoreCase);
}
