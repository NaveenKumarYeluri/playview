using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PlayView.Services;

namespace PlayView.Views;

public partial class MirrorPane : UserControl
{
    private const string WindowName = "PlayViewMirror";

    private AppSettings _settings = new();
    private string? _scrcpy;
    private string? _adb;

    private Process? _scrcpyProcess;
    private Task<string>? _stderrTask;
    private IntPtr _scrcpyHwnd;
    private DispatcherTimer? _livenessTimer;
    private DispatcherTimer? _sizeTimer;

    public MirrorPane()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        MirrorHost.SizeChanged += (_, _) => ResizeMirror();

        // The embedded scrcpy window is a native child HWND — WPF's Visibility
        // does not hide it, so mirror it to the pane's visibility explicitly.
        IsVisibleChanged += OnPaneVisibilityChanged;
    }

    public void Initialize(AppSettings settings)
    {
        _settings = settings;
        ScrcpyPathBox.Text = settings.ScrcpyPath;
    }

    // ── Visibility plumbing ──────────────────────────────────────────────

    private void OnPaneVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_scrcpyHwnd != IntPtr.Zero)
            Native.ShowWindow(_scrcpyHwnd, (bool)e.NewValue ? Native.SW_SHOW : Native.SW_HIDE);
    }

    // ── Device refresh ───────────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RefreshBtn.IsEnabled = false;

        _scrcpy = ScrcpyService.FindScrcpy(_settings.ScrcpyPath, AppContext.BaseDirectory);
        if (_scrcpy == null)
        {
            StatusText.Text = "scrcpy not found. Install it (winget install scrcpy), " +
                              "or drop it into a tools folder next to PlayView.exe, or set the path below.";
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

    // ── Launch + embed ───────────────────────────────────────────────────

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (_scrcpy == null || _adb == null)
        {
            await RefreshAsync();
            if (_scrcpy == null) return;
        }

        if (_scrcpyProcess is { HasExited: false })
        {
            StatusText.Text = "A mirror session is already running — stop it first.";
            return;
        }

        var selected = DeviceList.SelectedItem as ScrcpyDevice;
        if (selected == null && DeviceList.ItemsSource is List<ScrcpyDevice> devices && devices.Count == 1)
            selected = devices[0];
        if (selected == null)
        {
            StatusText.Text = "Select a device (click Refresh) or connect your phone.";
            return;
        }
        if (selected.State != "device")
        {
            StatusText.Text = "Device not ready: " + selected.Description;
            return;
        }

        LaunchBtn.IsEnabled = false;
        StatusText.Text = "Starting mirror on " + selected.Description + "…";

        var args = $"-s {selected.Serial} --window-title {WindowName} --window-borderless";
        if (!string.IsNullOrWhiteSpace(ExtraArgsBox.Text)) args += " " + ExtraArgsBox.Text.Trim();

        try
        {
            _scrcpyProcess = Process.Start(new ProcessStartInfo(_scrcpy, args)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            _stderrTask = _scrcpyProcess!.StandardError.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to start scrcpy: " + ex.Message;
            LaunchBtn.IsEnabled = true;
            return;
        }

        // Wait for scrcpy's window to appear, then embed it into this pane.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_scrcpyProcess.HasExited) break;

            var hwnd = Native.FindWindowW(null, WindowName);
            if (hwnd != IntPtr.Zero)
            {
                await EmbedMirrorAsync(hwnd);
                LaunchBtn.IsEnabled = true;
                return;
            }

            await Task.Delay(250);
        }

        LaunchBtn.IsEnabled = true;

        if (_scrcpyProcess.HasExited)
        {
            string output = "";
            if (_stderrTask != null)
            {
                await Task.WhenAny(_stderrTask, Task.Delay(1000));
                if (_stderrTask.Status == TaskStatus.RanToCompletion) output = _stderrTask.Result;
            }

            StatusText.Text = "scrcpy exited before mirroring started." +
                (string.IsNullOrWhiteSpace(output) ? "" : "\n\n" + TrimOutput(output));
        }
        else
        {
            StatusText.Text = "Timed out waiting for the mirror window. Try Launch again.";
        }
    }

    private async Task EmbedMirrorAsync(IntPtr hwnd)
    {
        _scrcpyHwnd = hwnd;

        // Strip scrcpy's window chrome and turn it into a child of our pane.
        long style = Native.GetWindowLongPtrW(hwnd, Native.GWL_STYLE).ToInt64();
        style &= ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU
                   | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
        style |= Native.WS_CHILD | Native.WS_VISIBLE;
        Native.SetWindowLongPtrW(hwnd, Native.GWL_STYLE, new IntPtr(style));

        MirrorSetup.Visibility = Visibility.Collapsed;
        MirrorPanel.Visibility = Visibility.Visible;

        // The HwndHost only creates its HWND once it has been laid out.
        for (var i = 0; i < 100 && MirrorHost.ChildHwnd == IntPtr.Zero; i++)
            await Task.Delay(50);

        if (MirrorHost.ChildHwnd == IntPtr.Zero)
        {
            StatusText.Text = "Mirror started but could not be embedded — look for a separate scrcpy window.";
            return;
        }

        Native.SetParent(hwnd, MirrorHost.ChildHwnd);
        ResizeMirror();
        Native.ShowWindow(hwnd, Native.SW_SHOW);

        StartLivenessWatch();
        StartSizeSync();
        StatusText.Text = "Mirror embedded — use the media buttons above to control playback from the laptop.";
    }

    private void ResizeMirror()
    {
        if (_scrcpyHwnd == IntPtr.Zero || MirrorHost.ChildHwnd == IntPtr.Zero) return;
        if (!Native.GetClientRect(MirrorHost.ChildHwnd, out var client)) return;

        var hostWidth = client.Right - client.Left;
        var hostHeight = client.Bottom - client.Top;
        if (hostWidth <= 0 || hostHeight <= 0) return;

        // The phone window's own size defines the screen aspect (handles rotation).
        if (!Native.GetWindowRect(_scrcpyHwnd, out var window)) return;
        var videoWidth = window.Right - window.Left;
        var videoHeight = window.Bottom - window.Top;
        if (videoWidth <= 0 || videoHeight <= 0) return;

        // Aspect-fit like a video player, centred with black bars.
        var scale = Math.Min((double)hostWidth / videoWidth, (double)hostHeight / videoHeight);
        var width = (int)(videoWidth * scale);
        var height = (int)(videoHeight * scale);

        Native.MoveWindow(_scrcpyHwnd, (hostWidth - width) / 2, (hostHeight - height) / 2, width, height, true);
    }

    private void StartSizeSync()
    {
        _sizeTimer?.Stop();
        _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _sizeTimer.Tick += (_, _) => ResizeMirror();
        _sizeTimer.Start();
    }

    private void StartLivenessWatch()
    {
        _livenessTimer?.Stop();
        _livenessTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _livenessTimer.Tick += (_, _) =>
        {
            if (_scrcpyProcess is { HasExited: true })
            {
                _livenessTimer?.Stop();
                ResetMirrorUi("Mirror ended (phone disconnected or scrcpy closed).");
            }
        };
        _livenessTimer.Start();
    }

    // ── Media controls (adb key events — work regardless of window focus) ──

    private async void OnPlayPauseKey(object sender, RoutedEventArgs e) => await SendKeyEvent(85);
    private async void OnVolDown(object sender, RoutedEventArgs e) => await SendKeyEvent(25);
    private async void OnVolUp(object sender, RoutedEventArgs e) => await SendKeyEvent(24);
    private async void OnBackKey(object sender, RoutedEventArgs e) => await SendKeyEvent(4);
    private async void OnHomeKey(object sender, RoutedEventArgs e) => await SendKeyEvent(3);

    private async Task SendKeyEvent(int keyCode)
    {
        if (_adb == null || _scrcpyProcess is not { HasExited: false }) return;
        try
        {
            await ScrcpyService.RunAdbAsync(_adb, $"shell input keyevent {keyCode}");
        }
        catch
        {
            // Phone busy or disconnected — the liveness watch will catch the exit.
        }
    }

    // ── Stop / teardown ──────────────────────────────────────────────────

    private void OnStopMirror(object sender, RoutedEventArgs e) => StopMirror();

    private void OnBackToSetup(object sender, RoutedEventArgs e)
    {
        // Keep the mirror running; just show the setup controls again.
        MirrorSetup.Visibility = Visibility.Visible;
    }

    private void StopMirror()
    {
        if (_scrcpyHwnd != IntPtr.Zero)
            Native.PostMessageW(_scrcpyHwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        var process = _scrcpyProcess;
        if (process is { HasExited: false })
        {
            var killer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            killer.Tick += (_, _) =>
            {
                killer.Stop();
                try { if (!process.HasExited) process.Kill(true); } catch { /* already gone */ }
            };
            killer.Start();
        }

        _livenessTimer?.Stop();
        _sizeTimer?.Stop();
        ResetMirrorUi("Mirror stopped.");
    }

    private void ResetMirrorUi(string message)
    {
        _scrcpyHwnd = IntPtr.Zero;
        _scrcpyProcess = null;
        _stderrTask = null;

        _livenessTimer?.Stop();
        _sizeTimer?.Stop();

        MirrorPanel.Visibility = Visibility.Collapsed;
        MirrorSetup.Visibility = Visibility.Visible;
        StatusText.Text = message;

        if (_adb != null) _ = RefreshAsync();
    }

    public void ShutdownMirror()
    {
        try
        {
            if (_scrcpyHwnd != IntPtr.Zero)
                Native.PostMessageW(_scrcpyHwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* window already gone */ }

        try
        {
            if (_scrcpyProcess is { HasExited: false }) _scrcpyProcess.Kill(true);
        }
        catch { /* already gone */ }
    }

    // ── Path / Wi-Fi helpers ─────────────────────────────────────────────

    private static string TrimOutput(string output)
    {
        output = output.Trim();
        return output.Length <= 800 ? output : output[^800..];
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
