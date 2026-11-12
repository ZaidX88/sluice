using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sluice.Desktop.ViewModels;

namespace Sluice.Desktop.Views;

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
        DataContextChanged += (_, _) => Attach(DataContext as LogViewModel);
        AttachedToVisualTree += (_, _) => { Attach(DataContext as LogViewModel); ScrollToEnd(); };
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void Attach(LogViewModel? vm)
    {
        if (_vm is not null) _vm.NewLines -= ScrollToEnd;
        _vm = vm;
        if (_vm is not null) _vm.NewLines += ScrollToEnd;
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        if (List.ItemCount > 0) List.ScrollIntoView(List.ItemCount - 1);
    }, DispatcherPriority.Background);
}

public partial class TunnelsView : UserControl
{
    private TunnelsViewModel? _vm;

    public TunnelsView()
    {
        InitializeComponent();
        // The check results panel takes the Address column's room: hide the column while it is open.
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as TunnelsViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            SyncAddressColumn();
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TunnelsViewModel.HasCheck)) SyncAddressColumn();
    }

    private void SyncAddressColumn()
    {
        if (this.FindControl<DataGrid>("Grid") is { } grid && grid.Columns.FirstOrDefault(c => Equals(c.Header, "Address")) is { } col)
            col.IsVisible = _vm?.HasCheck != true;
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is TunnelsViewModel vm && vm.EditCommand.CanExecute(null)) vm.EditCommand.Execute(null);
    }
}

public partial class RulesView : UserControl
{
    public RulesView() => InitializeComponent();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RulesViewModel vm && vm.EditCommand.CanExecute(null)) vm.EditCommand.Execute(null);
    }
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
