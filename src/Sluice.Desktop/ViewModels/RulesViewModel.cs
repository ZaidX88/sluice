using System.Collections.ObjectModel;
using System.Windows.Input;
using Sluice.Desktop.Mvvm;
using Sluice.Desktop.Services;
using Sluice.Core.Profiles;

namespace Sluice.Desktop.ViewModels;

public sealed class RuleRow(Rule rule, string action, Action commit) : ObservableObject
{
    public Rule Rule { get; } = rule;
    public string Name => Rule.Name;
    public string Applications => Show(Rule.Applications);
    public string Targets => Show(Rule.Targets);
    public string Ports => Show(Rule.Ports);
    public string Action { get; } = action;
    public bool IsDefault => Rule.IsDefault;
    public bool IsBlock => Rule.Action.Type == RuleActionType.Block;
    public bool IsDirect => Rule.Action.Type == RuleActionType.Direct;

    public bool Enabled
    {
        get => Rule.Enabled || Rule.IsDefault;
        set
        {
            if (Rule.IsDefault || Rule.Enabled == value) return;
            Rule.Enabled = value;
            Raise();
            commit();
        }
    }

    private static string Show(string? s) => string.IsNullOrWhiteSpace(s) ? "Any" : s;
}

public sealed class RulesViewModel : PageViewModel
{
    private RuleRow? _selected;

    public RulesViewModel(EngineHost host)
        : base(host, "Rules", "filter", "Checked from top to bottom; the first matching rule decides. Default catches everything else.")
    {
        AddCommand = new RelayCommand(Add);
        EditCommand = new RelayCommand(Edit, () => Selected is not null);
        CloneCommand = new RelayCommand(Clone, () => Selected is { IsDefault: false });
        RemoveCommand = new RelayCommand(Remove, () => Selected is { IsDefault: false });
        UpCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        DownCommand = new RelayCommand(() => Move(+1), () => CanMove(+1));
        OnProfileChanged();
    }

    public ObservableCollection<RuleRow> Rows { get; } = new();
    public RuleRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand CloneCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand DownCommand { get; }

    /// <summary>Rules in evaluation order, with Default always last.</summary>
    private List<Rule> Ordered =>
        Host.Profile.Rules.Where(r => !r.IsDefault).Concat(Host.Profile.Rules.Where(r => r.IsDefault)).ToList();

    public override void OnProfileChanged()
    {
        var selectedRule = Selected?.Rule;
        Rows.Clear();
        foreach (var r in Ordered)
            Rows.Add(new RuleRow(r, Host.Engine.Describe(r.Action), () => Host.CommitProfileEdit(notify: false)));
        Selected = Rows.FirstOrDefault(r => r.Rule == selectedRule);
    }

    private void Save(List<Rule> ordered, Rule? select)
    {
        Host.Profile.Rules.Clear();
        Host.Profile.Rules.AddRange(ordered);
        Host.CommitProfileEdit();
        Selected = Rows.FirstOrDefault(r => r.Rule == select);
    }

    private void Add()
    {
        var rule = Dialogs.EditRule(null, Host.Profile);
        if (rule is null) return;
        var list = Ordered;
        // New rules go just above Default (or above the selection).
        var at = Selected is { IsDefault: false } ? list.IndexOf(Selected.Rule) : list.Count - 1;
        list.Insert(Math.Max(0, at), rule);
        Save(list, rule);
    }

    private void Edit()
    {
        if (Selected is null) return;
        var edited = Dialogs.EditRule(Selected.Rule, Host.Profile);
        if (edited is null) return;
        var r = Selected.Rule;
        if (!r.IsDefault) r.Name = edited.Name;
        r.Applications = edited.Applications;
        r.Targets = edited.Targets;
        r.Ports = edited.Ports;
        r.Action = edited.Action;
        Save(Ordered, r);
    }

    private void Clone()
    {
        if (Selected is null) return;
        var s = Selected.Rule;
        var copy = new Rule
        {
            Name = s.Name + " (copy)", Enabled = s.Enabled, Applications = s.Applications,
            Targets = s.Targets, Ports = s.Ports,
            Action = new RuleAction { Type = s.Action.Type, TargetId = s.Action.TargetId },
        };
        var list = Ordered;
        list.Insert(list.IndexOf(s) + 1, copy);
        Save(list, copy);
    }

    private void Remove()
    {
        if (Selected is not { IsDefault: false } row) return;
        if (!Dialogs.Confirm("Remove rule", $"Remove rule \"{row.Name}\"?", "Remove")) return;
        var list = Ordered;
        list.Remove(row.Rule);
        Save(list, null);
    }

    private bool CanMove(int delta)
    {
        if (Selected is not { IsDefault: false } row) return false;
        var list = Ordered;
        var i = list.IndexOf(row.Rule) + delta;
        return i >= 0 && i < list.Count - 1;   // never past Default
    }

    private void Move(int delta)
    {
        if (!CanMove(delta)) return;
        var r = Selected!.Rule;
        var list = Ordered;
        var i = list.IndexOf(r);
        list.RemoveAt(i);
        list.Insert(i + delta, r);
        Save(list, r);
    }
}
