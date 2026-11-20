using Avalonia.Controls;
using Avalonia.Interactivity;
using Sluice.Core.Profiles;
using Sluice.Desktop.Controls;

namespace Sluice.Desktop.Views;

public partial class RuleDialog : SluiceWindow
{
    private sealed record ActionChoice(string Text, RuleAction Action)
    {
        public override string ToString() => Text;
    }

    private readonly bool _isDefault;

    public Rule? Result { get; private set; }

    public RuleDialog() : this(null, Profile.CreateDefault()) { }   // designer

    public RuleDialog(Rule? existing, Profile profile)
    {
        InitializeComponent();
        _isDefault = existing?.IsDefault == true;
        Heading.Text = existing is null ? "Add rule" : "Edit rule";

        var choices = new List<ActionChoice>
        {
            new("Direct — connect without a tunnel", RuleAction.Direct),
            new("Block — refuse the connection", RuleAction.Block),
        };
        choices.AddRange(profile.Tunnels.Select(p => new ActionChoice(
            $"Tunnel — {p.Type.DisplayName()} {p}", RuleAction.ViaTunnel(p.Id))));
        choices.AddRange(profile.Chains.Select(c => new ActionChoice($"Chain — {c.Name}", RuleAction.ViaChain(c.Id))));
        ActionBox.ItemsSource = choices;

        var current = existing?.Action ?? (profile.Tunnels.Count > 0 ? RuleAction.ViaTunnel(profile.Tunnels[0].Id) : RuleAction.Direct);
        ActionBox.SelectedItem = choices.FirstOrDefault(c => c.Action.Type == current.Type && c.Action.TargetId == current.TargetId)
                                 ?? choices[0];

        NameBox.Text = existing?.Name ?? "";
        AppsBox.Text = existing?.Applications ?? "";
        TargetsBox.Text = existing?.Targets ?? "";
        PortsBox.Text = existing?.Ports ?? "";

        if (_isDefault)
        {
            NameBox.IsEnabled = false;
            Conditions.IsVisible = false;
            DefaultNote.IsVisible = true;
        }
        Opened += (_, _) => { if (_isDefault) ActionBox.Focus(); else NameBox.Focus(); };
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (!_isDefault && name.Length == 0) { ShowError("Give the rule a name."); return; }
        if (!_isDefault && name.Equals("Default", StringComparison.OrdinalIgnoreCase)) { ShowError("\"Default\" is reserved for the catch-all rule."); return; }

        Result = new Rule
        {
            Name = name,
            Applications = Clean(AppsBox.Text),
            Targets = Clean(TargetsBox.Text),
            Ports = Clean(PortsBox.Text),
            Action = ((ActionChoice)ActionBox.SelectedItem!).Action,
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }
}
