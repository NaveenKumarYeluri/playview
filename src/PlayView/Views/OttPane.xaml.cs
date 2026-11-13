using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PlayView.Services;

namespace PlayView.Views;

public partial class OttPane : UserControl
{
    private readonly OttServiceConfig _service;
    private bool _initialized;

    public OttPane(OttServiceConfig service)
    {
        InitializeComponent();
        _service = service;
        AddressText.Text = service.Url;
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // Isolated profile per service so logins/cookies never mix.
            var profileDir = Path.Combine(AppSettings.ProfilesRoot, _service.Id);
            Directory.CreateDirectory(profileDir);

            var environment = await CoreWebView2Environment.CreateAsync(null, profileDir);
            await Web.EnsureCoreWebView2Async(environment);

            var core = Web.CoreWebView2;
            core.NewWindowRequested += (_, e) =>
            {
                // Keep popups inside the same pane instead of spawning windows.
                e.Handled = true;
                Web.Source = new Uri(e.Uri);
            };
            core.NavigationStarting += (_, e) => AddressText.Text = e.Uri;
            core.NavigationCompleted += (_, _) => AddressText.Text = Web.Source?.ToString() ?? "";

            Web.Source = new Uri(_service.Url);
        }
        catch (Exception ex)
        {
            _initialized = false;
            ErrorHint.Text = "Could not start the embedded browser.\n\n" + ex.Message +
                             "\n\nIf the WebView2 Runtime is missing, install it from: " +
                             "https://developer.microsoft.com/microsoft-edge/webview2/";
            ErrorHint.Visibility = Visibility.Visible;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2?.CanGoBack == true) Web.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2?.CanGoForward == true) Web.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e) => Web.Reload();

    private void OnHome(object sender, RoutedEventArgs e) => Web.Source = new Uri(_service.Url);

    public void Dispose()
    {
        try { Web.Dispose(); } catch { /* already closed */ }
    }
}
