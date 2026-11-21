using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Sluice.Core.Profiles;
using Sluice.Core.Tunnels;
using Sluice.Desktop.Controls;

namespace Sluice.Desktop.Views;

/// <summary>Adds tunnels to the profile from pasted share links or a subscription URL.</summary>
public partial class ImportDialog : SluiceWindow
{
    private readonly Profile _profile;
    private readonly TunnelTransport.Resolver? _resolve;
    private CancellationTokenSource? _cts;

    public ImportResult? Result { get; private set; }

    public ImportDialog() : this(Profile.CreateDefault(), null) { }   // designer

    public ImportDialog(Profile profile, TunnelTransport.Resolver? resolve, string? initialText = null)
    {
        InitializeComponent();
        _profile = profile;
        _resolve = resolve;
        if (!string.IsNullOrWhiteSpace(initialText)) LinksBox.Text = initialText.Trim();
        Opened += (_, _) => LinksBox.Focus();
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>Clipboard text worth pre-filling (it contains share links).</summary>
    public static async Task<string?> ClipboardLinksAsync()
    {
        try
        {
            var text = await App.GetClipboardTextAsync();
            return text is not null && ShareLinks.ParseMany(text).Any(r => r.Ok) ? text : null;
        }
        catch
        {
            return null;   // clipboard busy
        }
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if (LinksPanel is null || SubPanel is null) return;   // during InitializeComponent
        var sub = SubMode.IsChecked == true;
        LinksPanel.IsVisible = !sub;
        SubPanel.IsVisible = sub;
        ImportButton.Content = sub ? "Subscribe" : "Import";
        StatusText.IsVisible = false;
        (sub ? UrlBox : LinksBox).Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (SubMode.IsChecked != true)
        {
            var r = Subscriptions.ImportLinks(_profile, LinksBox.Text ?? "");
            if (r.Added.Count + r.Unchanged.Count == 0)
            {
                ShowStatus(r.Warnings.FirstOrDefault() ?? "No share links found.", error: true);
                return;
            }
            Result = r;
            Close(true);
            return;
        }

        Subscription sub;
        try
        {
            sub = Subscriptions.AddSubscription(_profile, UrlBox.Text ?? "", NameBox.Text);
        }
        catch (FormatException ex)
        {
            ShowStatus(ex.Message, error: true);
            return;
        }
        var isNew = sub.LastUpdated is null && !_profile.Tunnels.Any(t => t.SubscriptionId == sub.Id);
        ImportButton.IsEnabled = false;
        ShowStatus("Downloading…", error: false);
        _cts = new CancellationTokenSource();
        try
        {
            Result = await Subscriptions.UpdateAsync(_profile, sub, _resolve, _cts.Token);
            Close(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            if (isNew) _profile.Subscriptions.Remove(sub);   // do not keep a subscription that never worked
            ShowStatus(ex.Message, error: true);
        }
        catch (OperationCanceledException)
        {
            if (isNew) _profile.Subscriptions.Remove(sub);
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    internal void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        // The palette's brushes are per theme, so they are found only with the theme variant (#181); a plain
        // FindResource returns UnsetValue for them. Keep the current colour rather than crash if one is missing.
        if (this.TryFindResource(error ? "Danger" : "TextMuted", ActualThemeVariant, out var brush) && brush is IBrush b)
            StatusText.Foreground = b;
        StatusText.IsVisible = true;
    }
}
