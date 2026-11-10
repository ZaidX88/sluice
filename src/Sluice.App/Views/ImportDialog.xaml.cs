using System.IO;
using System.Net.Http;
using System.Windows;
using Sluice.Core.Profiles;
using Sluice.Core.Tunnels;

namespace Sluice.App.Views;

/// <summary>Adds tunnels to the profile from pasted share links or a subscription URL.</summary>
public partial class ImportDialog : Window
{
    private readonly Profile _profile;
    private readonly TunnelTransport.Resolver? _resolve;
    private CancellationTokenSource? _cts;

    public ImportResult? Result { get; private set; }

    public ImportDialog(Profile profile, TunnelTransport.Resolver? resolve, string? initialText = null)
    {
        InitializeComponent();
        _profile = profile;
        _resolve = resolve;
        if (!string.IsNullOrWhiteSpace(initialText)) LinksBox.Text = initialText.Trim();
        Loaded += (_, _) => LinksBox.Focus();
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>Clipboard text worth pre-filling (it contains share links).</summary>
    public static string? ClipboardLinks()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            return text is not null && ShareLinks.ParseMany(text).Any(r => r.Ok) ? text : null;
        }
        catch
        {
            return null;   // clipboard busy
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (LinksPanel is null) return;   // during InitializeComponent
        var sub = SubMode.IsChecked == true;
        LinksPanel.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        SubPanel.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Content = sub ? "Subscribe" : "Import";
        StatusText.Visibility = Visibility.Collapsed;
        (sub ? UrlBox : LinksBox).Focus();
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (SubMode.IsChecked != true)
        {
            var r = Subscriptions.ImportLinks(_profile, LinksBox.Text);
            if (r.Added.Count + r.Unchanged.Count == 0)
            {
                Show(r.Warnings.FirstOrDefault() ?? "No share links found.", error: true);
                return;
            }
            Result = r;
            DialogResult = true;
            return;
        }

        Subscription sub;
        try
        {
            sub = Subscriptions.AddSubscription(_profile, UrlBox.Text, NameBox.Text);
        }
        catch (FormatException ex)
        {
            Show(ex.Message, error: true);
            return;
        }
        var isNew = sub.LastUpdated is null && !_profile.Tunnels.Any(t => t.SubscriptionId == sub.Id);
        ImportButton.IsEnabled = false;
        Show("Downloading…", error: false);
        _cts = new CancellationTokenSource();
        try
        {
            Result = await Subscriptions.UpdateAsync(_profile, sub, _resolve, _cts.Token);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            if (isNew) _profile.Subscriptions.Remove(sub);   // do not keep a subscription that never worked
            Show(ex.Message, error: true);
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

    private void Show(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Danger" : "TextMuted");
        StatusText.Visibility = Visibility.Visible;
    }
}
