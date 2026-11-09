using System.Windows;
using Sluice.Core.Profiles;

namespace Sluice.App.Views;

public partial class RuleDialog : Window
{
    private sealed record ActionChoice(string Text, RuleAction Action);

    private readonly bool _isDefault;

    public Rule? Result { get; private set; }

    public RuleDialog(Rule? existing, Profile profile)
    {
        InitializeComponent();
        _isDefault = existing?.IsDefault == true;
        Heading.Text = existing is null ? "Add rule" : "Edit rule";

        var choices = new List<ActionChoice>
        {
            new("Direct — connect without a proxy", RuleAction.Direct),
            new("Block — refuse the connection", RuleAction.Block),
        };
        choices.AddRange(profile.Proxies.Select(p => new ActionChoice(
            $"Proxy — {p.Type.ToString().ToUpperInvariant()} {p}", RuleAction.ViaProxy(p.Id))));
        choices.AddRange(profile.Chains.Select(c => new ActionChoice($"Chain — {c.Name}", RuleAction.ViaChain(c.Id))));
        ActionBox.ItemsSource = choices;

        var current = existing?.Action ?? (profile.Proxies.Count > 0 ? RuleAction.ViaProxy(profile.Proxies[0].Id) : RuleAction.Direct);
        ActionBox.SelectedItem = choices.FirstOrDefault(c => c.Action.Type == current.Type && c.Action.TargetId == current.TargetId)
                                 ?? choices[0];

        NameBox.Text = existing?.Name ?? "";
        AppsBox.Text = existing?.Applications ?? "";
        TargetsBox.Text = existing?.Targets ?? "";
        PortsBox.Text = existing?.Ports ?? "";

        if (_isDefault)
        {
            NameBox.IsEnabled = false;
            Conditions.Visibility = Visibility.Collapsed;
            DefaultNote.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => (_isDefault ? (UIElement)ActionBox : NameBox).Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (!_isDefault && name.Length == 0) { ShowError("Give the rule a name."); return; }
        if (!_isDefault && name.Equals("Default", StringComparison.OrdinalIgnoreCase)) { ShowError("\"Default\" is reserved for the catch-all rule."); return; }

        Result = new Rule
        {
            Name = name,
            Applications = Clean(AppsBox.Text),
            Targets = Clean(TargetsBox.Text),
            Ports = Clean(PortsBox.Text),
            Action = ((ActionChoice)ActionBox.SelectedItem).Action,
        };
        DialogResult = true;
    }

    private static string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
