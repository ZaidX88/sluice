using System.Windows.Controls;
using Sluice.App.ViewModels;

namespace Sluice.App.Views;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView() => InitializeComponent();
}

public partial class StatisticsView : UserControl
{
    public StatisticsView() => InitializeComponent();
}

public partial class LogView : UserControl
{
    private LogViewModel? _vm;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.NewLines -= ScrollToEnd;
            _vm = DataContext as LogViewModel;
            if (_vm is not null) _vm.NewLines += ScrollToEnd;
            ScrollToEnd();
        };
        Unloaded += (_, _) => { if (_vm is not null) _vm.NewLines -= ScrollToEnd; };
        Loaded += (_, _) => { if (_vm is not null) { _vm.NewLines -= ScrollToEnd; _vm.NewLines += ScrollToEnd; } ScrollToEnd(); };
    }

    private void ScrollToEnd()
    {
        if (List.Items.Count > 0) List.ScrollIntoView(List.Items[^1]);
    }
}

public partial class TunnelsView : UserControl
{
    public TunnelsView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is TunnelsViewModel vm && vm.EditCommand.CanExecute(null)) vm.EditCommand.Execute(null);
    }
}

public partial class RulesView : UserControl
{
    public RulesView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is RulesViewModel vm && vm.EditCommand.CanExecute(null)) vm.EditCommand.Execute(null);
    }
}

/// <summary>Inverts a bool (used as "IsDefault → not editable").</summary>
public sealed class NotConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => value is not true;
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
