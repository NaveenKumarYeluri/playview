using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PlayView.Services;

namespace PlayView.Views;

public partial class MirrorPane : UserControl
{
    private AppSettings _settings = new();
    private string? _scrcpy;
    private string? _adb;

    public MirrorPane()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    public void Initialize(AppSettings settings)
    {
        _settings = settings;
        ScrcpyPathBox.Text = settings.ScrcpyPath;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshBtn.IsEnabled = false;

        _scrcpy = ScrcpyService.FindScrcpy(_settings.ScrcpyPath, AppContext.BaseDirectory);
        if (_scrcpy == null)
        {
            StatusText.Text = "scrcpy not found. Install it (winget install scrcpy), " +
                              "or drop it into a tools\\scrcpy folder next to PlayView.exe, or set the path below.";
            LaunchBtn.IsEnabled = false;
            RefreshBtn.IsEnabled = true;
            return;
        }

        ScrcpyPathBox.Text = _scrcpy;

        _adb = ScrcpyService.FindAdb(_scrcpy);
        if (_adb == null)
        {
            StatusText.Text = "adb.exe not found next to scrcpy. Reinstall scrcpy (it bundles adb) or add adb to PATH.";
            LaunchBtn.IsEnabled = false;
            RefreshBtn.IsEnabled = true;
            return;
        }

        StatusText.Text = "Checking devices…";
        var devices = await ScrcpyService.ListDevicesAsync(_adb);
        DeviceList.ItemsSource = devices;

        LaunchBtn.IsEnabled = devices.Count > 0;
        StatusText.Text = devices.Count == 0
            ? "No devices found. Enable USB debugging on the phone, plug it in, and accept the debugging prompt."
            : $"{devices.Count} device(s) found. Select one and click Launch mirror.";

        RefreshBtn.IsEnabled = true;
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (_scrcpy == null) return;

        var serial = (DeviceList.SelectedItem as ScrcpyDevice)?.Serial;
        try
        {
            ScrcpyService.Launch(_scrcpy, serial, ExtraArgsBox.Text);
            StatusText.Text = "Mirror launched. Close the scrcpy window when you're done.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to launch scrcpy: " + ex.Message;
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Locate scrcpy.exe",
            Filter = "scrcpy.exe|scrcpy.exe",
        };

        if (dlg.ShowDialog() != true) return;

        _settings.ScrcpyPath = dlg.FileName;
        _settings.Save();
        ScrcpyPathBox.Text = dlg.FileName;
        _ = RefreshAsync();
    }

    private async void OnWifiEnable(object sender, RoutedEventArgs e)
    {
        if (_adb == null) { StatusText.Text = "Refresh devices first (scrcpy/adb must be found)."; return; }

        var output = await ScrcpyService.RunAdbAsync(_adb, "tcpip 5555");
        StatusText.Text = $"Wi-Fi mode: {output}\nNow unplug USB, enter the phone's IP above and click Connect.";
    }

    private async void OnWifiConnect(object sender, RoutedEventArgs e)
    {
        if (_adb == null) { StatusText.Text = "Refresh devices first (scrcpy/adb must be found)."; return; }

        var ip = PhoneIpBox.Text.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            StatusText.Text = "Enter the phone's IP address first (e.g. 192.168.1.8).";
            return;
        }

        var target = ip.Contains(':') ? ip : $"{ip}:5555";
        StatusText.Text = $"Connecting to {target}…";
        var output = await ScrcpyService.RunAdbAsync(_adb, $"connect {target}");
        StatusText.Text = $"Connect: {output}";
        await RefreshAsync();
    }
}
