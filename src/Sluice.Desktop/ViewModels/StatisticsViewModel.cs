using System.Collections.ObjectModel;
using System.Windows.Input;
using Sluice.Desktop.Mvvm;
using Sluice.Desktop.Services;
using Sluice.Core.Engine;

namespace Sluice.Desktop.ViewModels;

public sealed record StatisticsRowView(string Key, int Connections, int Active, int Failed, string Sent, string Received, string Total, double Share);

public sealed class StatisticsViewModel : PageViewModel
{
    private StatisticsGrouping _grouping = StatisticsGrouping.Application;
    private IReadOnlyList<StatisticsRowView> _rows = [];

    public StatisticsViewModel(EngineHost host)
        : base(host, "Statistics", "chart", "Traffic totals since Sluice started, including connections still open.")
    {
        ResetCommand = new RelayCommand(() => { Host.Statistics.Reset(); Refresh(); });
    }

    public ICommand ResetCommand { get; }

    public bool ByApplication { get => _grouping == StatisticsGrouping.Application; set { if (value) SetGrouping(StatisticsGrouping.Application); } }
    public bool ByTarget { get => _grouping == StatisticsGrouping.Target; set { if (value) SetGrouping(StatisticsGrouping.Target); } }
    public bool ByRule { get => _grouping == StatisticsGrouping.Rule; set { if (value) SetGrouping(StatisticsGrouping.Rule); } }

    public string KeyHeader => _grouping switch
    {
        StatisticsGrouping.Application => "Application",
        StatisticsGrouping.Target => "Target host",
        _ => "Rule",
    };

    public IReadOnlyList<StatisticsRowView> Rows { get => _rows; private set => Set(ref _rows, value); }
    public bool IsEmpty => Rows.Count == 0;

    private void SetGrouping(StatisticsGrouping g)
    {
        _grouping = g;
        Raise(nameof(ByApplication));
        Raise(nameof(ByTarget));
        Raise(nameof(ByRule));
        Raise(nameof(KeyHeader));
        Refresh();
    }

    public override void Refresh()
    {
        var snap = Host.Statistics.Snapshot(_grouping);
        var max = Math.Max(1, snap.Count == 0 ? 1 : snap.Max(r => r.BytesTotal));
        Rows = snap.Select(r => new StatisticsRowView(r.Key, r.Connections, r.Active, r.Failed,
            Format.Bytes(r.BytesSent), Format.Bytes(r.BytesReceived), Format.Bytes(r.BytesTotal),
            (double)r.BytesTotal / max)).ToList();
        Raise(nameof(IsEmpty));
    }
}
