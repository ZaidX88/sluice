using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Sluice.Desktop.Mvvm;
using Sluice.Desktop.Services;
using Sluice.Core;
using Sluice.Core.Engine;

namespace Sluice.Desktop.ViewModels;

public sealed record LogLine(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Normal => "INFO",
        LogLevel.Verbose => "VERBOSE",
        _ => "DEBUG",
    };
    public bool IsError => Level == LogLevel.Error;
    public bool IsQuiet => Level >= LogLevel.Verbose;
}

public sealed class LogViewModel : PageViewModel
{
    private const int MaxLines = 5000;
    private readonly ConcurrentQueue<LogLine> _incoming = new();
    private readonly List<LogLine> _all = new();
    private int _levelIndex = 1;   // Normal

    public LogViewModel(EngineHost host)
        : base(host, "Log", "list", "What Sluice is doing: connections, rule decisions and errors.")
    {
        // The first line is the version banner the log files start with (#96). Added here, not written to the tracker:
        // the host logs before this page subscribes, and FileLog already writes the banner at the top of each file.
        _incoming.Enqueue(new LogLine(DateTime.Now, LogLevel.Normal, SluiceVersion.Banner));
        host.Tracker.Log += (level, message) => _incoming.Enqueue(new LogLine(DateTime.Now, level, message));
        ClearCommand = new RelayCommand(() => { _all.Clear(); Lines.Clear(); });
        CopyCommand = new RelayCommand(() =>
        {
            var text = string.Join(Environment.NewLine, Lines.Select(l => $"[{l.TimeText}] {l.LevelText,-7} {l.Message}"));
            if (text.Length > 0) App.SetClipboardText(text);
        }, () => Lines.Count > 0);
    }

    public ObservableCollection<LogLine> Lines { get; } = new();
    public ICommand ClearCommand { get; }
    public ICommand CopyCommand { get; }

    public string[] Levels { get; } = ["Errors only", "Normal", "Verbose", "Debug"];

    public int LevelIndex
    {
        get => _levelIndex;
        set
        {
            if (!Set(ref _levelIndex, value)) return;
            // The tracker must emit at least as much as we want to show.
            if ((LogLevel)value > Host.Tracker.Level) Host.Tracker.Level = (LogLevel)value;
            Rebuild();
        }
    }

    /// <summary>Drain queued messages; runs every second regardless of which page is visible.</summary>
    public void Pump()
    {
        var added = false;
        while (_incoming.TryDequeue(out var line))
        {
            _all.Add(line);
            if (Visible(line)) { Lines.Add(line); added = true; }
        }
        if (_all.Count > MaxLines) _all.RemoveRange(0, _all.Count - MaxLines);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        if (added) NewLines?.Invoke();
    }

    /// <summary>Raised after new lines are appended (the view scrolls to the end).</summary>
    public event Action? NewLines;

    private bool Visible(LogLine l) => (int)l.Level <= _levelIndex;

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var l in _all.Where(Visible)) Lines.Add(l);
        NewLines?.Invoke();
    }
}
