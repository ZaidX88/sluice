using System.Collections.ObjectModel;
using System.Windows.Input;
using Sluice.App.Mvvm;
using Sluice.App.Services;
using Sluice.Core.Checker;
using Sluice.Core.Profiles;

namespace Sluice.App.ViewModels;

public sealed class TunnelRow(Tunnel tunnel) : ObservableObject
{
    private string _status = "Not checked";
    private bool? _ok;

    public Tunnel Tunnel { get; } = tunnel;
    public string Name => Tunnel.Label ?? $"{Tunnel.Address}:{Tunnel.Port}";
    public string Endpoint => $"{Tunnel.Address}:{Tunnel.Port}";
    public string Type => Tunnel.Type.DisplayName().ToUpperInvariant();
    public string Auth => string.IsNullOrEmpty(Tunnel.Username) ? "None" : Tunnel.Username!;
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool? Ok { get => _ok; set => Set(ref _ok, value); }
}

public sealed record CheckStepView(string Mark, string Title, string Detail, bool? Ok);

public sealed class TunnelsViewModel : PageViewModel
{
    private TunnelRow? _selected;
    private string _checkTitle = "";
    private bool _checking;

    public TunnelsViewModel(EngineHost host)
        : base(host, "Tunnels", "", "Routes out of this PC — SOCKS, HTTP and, soon, SSH and encrypted tunnels. Rules choose which traffic uses which tunnel.")
    {
        AddCommand = new RelayCommand(Add);
        EditCommand = new RelayCommand(Edit, () => Selected is not null);
        RemoveCommand = new RelayCommand(Remove, () => Selected is not null);
        CheckCommand = new AsyncCommand(CheckSelectedAsync, () => Selected is not null && !_checking);
        OnProfileChanged();
    }

    public ObservableCollection<TunnelRow> Rows { get; } = new();
    public ObservableCollection<CheckStepView> CheckSteps { get; } = new();
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CheckCommand { get; }

    public TunnelRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public string CheckTitle { get => _checkTitle; private set => Set(ref _checkTitle, value); }
    public bool HasCheck => CheckSteps.Count > 0 || _checking;
    public bool IsEmpty => Rows.Count == 0;

    public override void OnProfileChanged()
    {
        var keep = Rows.ToDictionary(r => r.Tunnel, r => (r.Status, r.Ok));
        Rows.Clear();
        foreach (var p in Host.Profile.Tunnels)
        {
            var row = new TunnelRow(p);
            if (keep.TryGetValue(p, out var k)) { row.Status = k.Status; row.Ok = k.Ok; }
            Rows.Add(row);
        }
        Raise(nameof(IsEmpty));
    }

    private void Add()
    {
        var p = Dialogs.EditTunnel(null);
        if (p is null) return;
        p.Id = Host.Profile.Tunnels.Count == 0 ? 100 : Host.Profile.Tunnels.Max(x => x.Id) + 1;
        Host.Profile.Tunnels.Add(p);

        // A tunnel does nothing until a rule uses it. Like Proxifier, offer to make it the default
        // route when no rule sends traffic anywhere yet.
        var def = Host.Profile.Rules.FirstOrDefault(r => r.IsDefault);
        var nothingRouted = Host.Profile.Rules.All(r => r.Action.Type is RuleActionType.Direct or RuleActionType.Block);
        if (def is not null && nothingRouted &&
            Dialogs.Confirm("Use this tunnel by default?",
                $"No rule sends traffic through a tunnel yet, so {p} would not be used.\n\n" +
                "Route everything through it now? (This sets the Default rule's action. Localhost stays direct.)",
                "Use by default"))
        {
            def.Action = RuleAction.ViaTunnel(p.Id);
        }

        Host.CommitProfileEdit();
        Selected = Rows.FirstOrDefault(r => r.Tunnel == p);
    }

    private void Edit()
    {
        if (Selected is null) return;
        var edited = Dialogs.EditTunnel(Selected.Tunnel);
        if (edited is null) return;
        var p = Selected.Tunnel;
        p.Type = edited.Type;
        p.Address = edited.Address;
        p.Port = edited.Port;
        p.Username = edited.Username;
        p.Password = edited.Password;
        p.Label = edited.Label;
        p.Socks4aRemoteResolve = edited.Socks4aRemoteResolve;
        p.Settings.Clear();
        foreach (var (k, v) in edited.Settings) p.Settings[k] = v;
        Host.CommitProfileEdit();
        Selected = Rows.FirstOrDefault(r => r.Tunnel == p);
    }

    private void Remove()
    {
        if (Selected is null) return;
        var p = Selected.Tunnel;
        var rules = Host.Profile.Rules.Where(r => r.Action.Type == RuleActionType.Tunnel && r.Action.TargetId == p.Id).ToList();
        var chains = Host.Profile.Chains.Where(c => c.Hops.Any(h => h.TunnelId == p.Id)).ToList();

        var message = $"Remove tunnel {Selected.Name}?";
        if (rules.Count > 0)
            message += $"\n\nThese rules use it and will be switched to Direct:\n  • {string.Join("\n  • ", rules.Select(r => r.Name))}";
        if (chains.Count > 0)
            message += $"\n\nIt will also be removed from chain(s): {string.Join(", ", chains.Select(c => c.Name))}";
        if (!Dialogs.Confirm("Remove tunnel", message, "Remove")) return;

        foreach (var r in rules) r.Action = RuleAction.Direct;
        foreach (var c in chains) c.Hops.RemoveAll(h => h.TunnelId == p.Id);
        Host.Profile.Tunnels.Remove(p);
        Host.CommitProfileEdit();
    }

    private async Task CheckSelectedAsync()
    {
        if (Selected is null) return;
        var row = Selected;
        _checking = true;
        CheckSteps.Clear();
        CheckTitle = $"Checking {row.Name}…";
        row.Status = "Checking…";
        row.Ok = null;
        Raise(nameof(HasCheck));

        var progress = new Progress<CheckStep>(s => CheckSteps.Add(new CheckStepView(
            s.Status switch { CheckStatus.Passed => "", CheckStatus.Failed => "", _ => "" },
            s.Name[..1].ToUpperInvariant() + s.Name[1..],
            (s.Elapsed is { } t && s.Number != 3 && s.Status == CheckStatus.Passed ? $"{t.TotalMilliseconds:F0} ms · " : "") + s.Detail,
            s.Status switch { CheckStatus.Passed => true, CheckStatus.Failed => false, _ => null })));

        try
        {
            var report = await TunnelChecker.CheckAsync(row.Tunnel, new CheckSettings(), progress);
            row.Ok = report.Passed;
            row.Status = report.Passed
                ? $"OK · {report.Latency?.TotalMilliseconds:F0} ms"
                : "Failed";
            CheckTitle = report.Passed ? $"{row.Name} is working" : $"{row.Name} failed the check";
        }
        finally
        {
            _checking = false;
            Raise(nameof(HasCheck));
        }
    }
}
