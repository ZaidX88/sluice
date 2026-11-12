using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Avalonia.Threading;
using Sluice.Desktop.Mvvm;
using Sluice.Desktop.Services;
using Sluice.Core.Checker;
using Sluice.Core.Profiles;
using Sluice.Core.Tunnels;

namespace Sluice.Desktop.ViewModels;

public sealed class TunnelRow(Tunnel tunnel, string? source = null, Action<TunnelRow, bool>? setEnabled = null) : ObservableObject
{
    /// <summary>The on/off switch: turning a tunnel off closes its session and makes its rules fail (no leak).</summary>
    public bool Enabled
    {
        get => Tunnel.Enabled;
        set
        {
            if (value == Tunnel.Enabled) return;
            setEnabled?.Invoke(this, value);
            Raise();
            Raise(nameof(Session));
            Raise(nameof(SessionTip));
        }
    }

    private string _status = "Not checked";
    private bool? _ok;

    public Tunnel Tunnel { get; } = tunnel;
    public string Name => Tunnel.Label ?? $"{Tunnel.Address}:{Tunnel.Port}";
    public string Endpoint => $"{Tunnel.Address}:{Tunnel.Port}";
    /// <summary>Subscription the tunnel came from, if any.</summary>
    public string? Source { get; } = source;

    /// <summary>Second line under the name: who logs in, and the subscription it came from.</summary>
    public string Subtitle => Source is null ? Auth : $"{Auth} · from {Source}";
    public string Type => Tunnel.Type.DisplayName().ToUpperInvariant();
    public string Auth => Tunnel.Type switch
    {
        TunnelType.VLess or TunnelType.VMess => "UUID",
        TunnelType.Tuic => "UUID + password",
        TunnelType.Shadowsocks or TunnelType.Trojan or TunnelType.Hysteria2 => "Password",
        TunnelType.Ssh when Tunnel.Settings.ContainsKey("privatekey") => $"{Tunnel.Username} (key)",
        _ => string.IsNullOrEmpty(Tunnel.Username) ? "None" : Tunnel.Username!,
    };
    public string Status { get => _status; set => Set(ref _status, value); }

    private SessionStatus _session = SessionStatus.None;

    /// <summary>Encrypted tunnels keep a session (SSH login, sing-box helper); plain proxies have none.</summary>
    public bool HasSession => TunnelBackends.Handles(Tunnel.Type);

    public SessionStatus SessionStatus
    {
        get => _session;
        set { _session = value; Raise(nameof(Session)); Raise(nameof(SessionOk)); Raise(nameof(SessionTip)); Raise(nameof(SessionGood)); Raise(nameof(SessionBad)); }
    }

    public string Session => !Tunnel.Enabled ? "Off" : !HasSession ? "—" : _session.State switch
    {
        SessionState.Connecting => "Connecting…",
        SessionState.Connected => $"Connected {_session.Since:HH:mm}",
        SessionState.Reconnecting => "Reconnecting…",
        SessionState.NotResponding => "Not responding",
        SessionState.Failed => "Failed",
        SessionState.Idle => "Idle",
        SessionState.Disabled => "Off",
        _ => "Not started",
    };

    public bool SessionGood => SessionOk == true;
    public bool SessionBad => SessionOk == false;

    /// <summary>True = connected, false = failed or reconnecting, null = neither.</summary>
    public bool? SessionOk => !Tunnel.Enabled ? null : _session.State switch
    {
        SessionState.Connected => true,
        SessionState.Failed or SessionState.Reconnecting or SessionState.NotResponding => false,
        _ => null,
    };

    public string SessionTip => !Tunnel.Enabled
        ? "Turned off: rules that use this tunnel fail instead of going direct, and chains skip it."
        : !HasSession
            ? "This tunnel type has no session: each connection goes to the proxy on its own."
            : _session.State switch
            {
                SessionState.NotStarted => "Starts when capture starts (if a rule uses it), on the first connection, or with Check.",
                SessionState.Failed => $"{_session.Detail}. Press Check, or turn it off and on, to try again.",
                _ => $"{Session}{(_session.Detail is { } d ? " — " + d : "")}",
            };
    public bool? Ok { get => _ok; set { Set(ref _ok, value); Raise(nameof(StatusGood)); Raise(nameof(StatusBad)); } }
    public bool StatusGood => _ok == true;
    public bool StatusBad => _ok == false;
}

public sealed record CheckStepView(string Mark, string Title, string Detail, bool? Ok)
{
    public bool IsGood => Ok == true;
    public bool IsBad => Ok == false;
}

public sealed class TunnelsViewModel : PageViewModel
{
    private TunnelRow? _selected;
    private string _checkTitle = "";
    private bool _checking;
    private bool _updating;

    public TunnelsViewModel(EngineHost host)
        : base(host, "Tunnels", "tunnel", "Routes out of this PC — SSH, VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC, SOCKS and HTTP. Rules choose which traffic uses which tunnel.")
    {
        AddCommand = new RelayCommand(Add);
        EditCommand = new RelayCommand(Edit, () => Selected is not null);
        RemoveCommand = new RelayCommand(Remove, () => Selected is not null);
        ImportCommand = new RelayCommand(Import);
        CopyLinkCommand = new RelayCommand(CopyLink, () => Selected is not null);
        UpdateCommand = new AsyncCommand(UpdateSubscriptionsAsync, () => !_updating && Host.Profile.Subscriptions.Count > 0);
        CheckCommand = new AsyncCommand(CheckSelectedAsync, () => Selected is not null && !_checking);
        // Check steps arrive through Progress<T>, possibly after the check finished: keep the panel in sync.
        CheckSteps.CollectionChanged += (_, _) => Raise(nameof(HasCheck));
        Host.Engine.Backends.StatusChanged += (tunnel, st) => Dispatcher.UIThread.Post(() =>
        {
            var id = tunnel.Id;
            foreach (var r in Rows.Where(r => r.Tunnel.Id == id)) r.SessionStatus = Host.Engine.Backends.GetStatus(id);
        });
        // A session can die silently; refresh "Disconnected" now and then.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { foreach (var r in Rows.Where(r => r.HasSession)) r.SessionStatus = Host.Engine.Backends.GetStatus(r.Tunnel.Id); };
        timer.Start();
        OnProfileChanged();
    }

    public ObservableCollection<TunnelRow> Rows { get; } = new();
    public ObservableCollection<CheckStepView> CheckSteps { get; } = new();
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CopyLinkCommand { get; }
    public ICommand UpdateCommand { get; }
    public bool HasSubscriptions => Host.Profile.Subscriptions.Count > 0;
    public string UpdateLabel => _updating ? "Updating…" : Host.Profile.Subscriptions.Count == 1 ? "Update subscription" : "Update subscriptions";

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
            var source = p.SubscriptionId is { } sid ? Host.Profile.Subscriptions.FirstOrDefault(s => s.Id == sid)?.Name : null;
            var row = new TunnelRow(p, source, SetEnabled);
            if (keep.TryGetValue(p, out var k)) { row.Status = k.Status; row.Ok = k.Ok; }
            row.SessionStatus = Host.Engine.Backends.GetStatus(p.Id);
            Rows.Add(row);
        }
        Raise(nameof(IsEmpty));
        Raise(nameof(HasSubscriptions));
        Raise(nameof(UpdateLabel));
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

    private void SetEnabled(TunnelRow row, bool on)
    {
        row.Tunnel.Enabled = on;
        Host.Tracker.Write(Sluice.Core.Engine.LogLevel.Normal, $"Tunnel {row.Name} turned {(on ? "on" : "off")}");
        Host.CommitProfileEdit(notify: false);   // closes the session if turned off
        if (on && Host.Intercepting) Host.Engine.WarmUpTunnels();
    }

    private void Import()
    {
        var hadRouting = Host.Profile.Rules.Any(r => r.Action.Type is RuleActionType.Tunnel or RuleActionType.Chain);
        var result = Dialogs.Import(Host.Profile, Host.Engine.Backends.Resolver);
        if (result is null) return;
        Host.CommitProfileEdit();
        Selected = Rows.FirstOrDefault(r => result.Added.Count > 0 && r.Tunnel == result.Added[0]);
        var note = $"Import: {result.Summary}.";
        if (result.Warnings.Count > 0) note += " " + string.Join(" · ", result.Warnings.Take(3)) + (result.Warnings.Count > 3 ? " …" : "");
        if (!hadRouting && result.Added.Count > 0) note += " Point a rule at a tunnel to use it.";
        Host.Notify(note);
    }

    private void CopyLink()
    {
        if (Selected is null) return;
        try
        {
            App.SetClipboardText(ShareLinks.ToLink(Selected.Tunnel));
            Host.Notify($"Share link for {Selected.Name} copied. It contains the tunnel's password or key — share it only with people you trust.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Host.Notify("The clipboard is busy; try again.");
        }
    }

    private async Task UpdateSubscriptionsAsync()
    {
        _updating = true;
        Raise(nameof(UpdateLabel));
        var notes = new List<string>();
        try
        {
            foreach (var sub in Host.Profile.Subscriptions.ToList())
            {
                try
                {
                    var r = await Subscriptions.UpdateAsync(Host.Profile, sub, Host.Engine.Backends.Resolver);
                    notes.Add($"{sub.Name}: {r.Summary}" + (r.Warnings.Count > 0 ? $" ({string.Join("; ", r.Warnings.Take(2))})" : ""));
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.InvalidDataException)
                {
                    notes.Add($"{ex.Message} — kept the current tunnels");
                }
            }
        }
        finally
        {
            _updating = false;
            Host.CommitProfileEdit();
        }
        Host.Notify(string.Join(" · ", notes));
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

        if (p.SubscriptionId is { } subId && Host.Profile.Subscriptions.FirstOrDefault(s => s.Id == subId) is { } sub)
        {
            var count = Host.Profile.Tunnels.Count(t => t.SubscriptionId == subId);
            var answer = Dialogs.Ask("Remove tunnel",
                $"{Selected.Name} comes from the subscription \"{sub.Name}\" and would return on the next update.\n\n" +
                $"Yes — remove the whole subscription and its {count} tunnel(s)\nNo — remove only this tunnel\n\n" +
                "Rules that use removed tunnels are switched to Direct.");
            if (answer is null) return;
            var removed = new ImportResult();
            if (answer == true) Subscriptions.RemoveSubscription(Host.Profile, sub);
            else Subscriptions.RemoveTunnel(Host.Profile, p, removed);
            Host.CommitProfileEdit();
            return;
        }

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
            s.Status switch { CheckStatus.Passed => "check", CheckStatus.Failed => "close", _ => "dash" },
            s.Name[..1].ToUpperInvariant() + s.Name[1..],
            (s.Elapsed is { } t && s.Number != 3 && s.Status == CheckStatus.Passed ? $"{t.TotalMilliseconds:F0} ms · " : "") + s.Detail,
            s.Status switch { CheckStatus.Passed => true, CheckStatus.Failed => false, _ => null })));

        try
        {
            Host.Engine.Backends.ResetFailures(row.Tunnel.Id);   // a manual check always tries again
            var report = await TunnelChecker.CheckAsync(row.Tunnel, new CheckSettings(), progress, backends: Host.Engine.Backends);
            row.Ok = report.Passed;
            row.Status = report.Passed
                ? $"OK · {report.Latency?.TotalMilliseconds:F0} ms"
                : "Failed";
            CheckTitle = report.Passed ? $"{row.Name} is working" : $"{row.Name} failed the check";
            var failed = report.Steps.FirstOrDefault(s => s.Status == CheckStatus.Failed);
            Host.Tracker.Write(report.Passed ? Sluice.Core.Engine.LogLevel.Normal : Sluice.Core.Engine.LogLevel.Error,
                report.Passed ? $"Check: {row.Name} is working ({row.Status})" : $"Check: {row.Name} failed at test {failed?.Number}: {failed?.Detail}");
        }
        finally
        {
            _checking = false;
            Raise(nameof(HasCheck));
        }
    }
}
