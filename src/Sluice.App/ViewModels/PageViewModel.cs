using Sluice.App.Mvvm;
using Sluice.App.Services;

namespace Sluice.App.ViewModels;

/// <summary>A page in the sidebar.</summary>
public abstract class PageViewModel(EngineHost host, string title, string glyph, string subtitle) : ObservableObject
{
    private bool _isCurrent;

    protected EngineHost Host { get; } = host;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Subtitle { get; } = subtitle;

    /// <summary>True while this page is shown (drives the sidebar highlight).</summary>
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    /// <summary>Called once a second while the page is visible.</summary>
    public virtual void Refresh() { }

    /// <summary>Called when the profile is replaced or edited elsewhere.</summary>
    public virtual void OnProfileChanged() { }
}

public sealed record NavGroup(string Header, IReadOnlyList<PageViewModel> Pages);
