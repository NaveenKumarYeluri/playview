using System.Diagnostics;
using System.IO;
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
    private IntPtr _scrcpyHwnd;
    private DispatcherTimer? _livenessTimer;
    private DispatcherTimer? _sizeTimer;
    private double _phoneAspect;   // width / height, captured once at launch
    private int _lastAppliedW;
    private int _lastAppliedH;

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

    /// <summary>Updates the status line and scrolls it into view so failures are never missed.</summary>
    private string StatusMessage
    {
        get => StatusText.Text;
        set
        {
            StatusText.Text = value;
            Dispatcher.InvokeAsync(
                () => StatusText.BringIntoView(),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static void AppendMirrorLog(string line)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "mirror.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the app.
        }
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
            StatusMessage = "scrcpy not found. Install it (winget install scrcpy), " +
                              "or drop it into a tools folder next to PlayView.exe, or set the path below.";
            LaunchBtn.IsEnabled = false;
            RefreshBtn.IsEnabled = true;
            return;
        }

        ScrcpyPathBox.Text = _scrcpy;

        _adb = ScrcpyService.FindAdb(_scrcpy);
        if (_adb == null)
        {
            StatusMessage = "adb.exe not found next to scrcpy. Reinstall scrcpy (it bundles adb) or add adb to PATH.";
            LaunchBtn.IsEnabled = false;
            RefreshBtn.IsEnabled = true;
            return;
        }

        StatusMessage = "Checking devices…";
        var devices = await ScrcpyService.ListDevicesAsync(_adb);
        DeviceList.ItemsSource = devices;

        LaunchBtn.IsEnabled = devices.Count > 0;
        StatusMessage = devices.Count == 0
            ? "No devices found. Enable USB debugging on the phone, plug it in, and accept the debugging prompt."
            : $"{devices.Count} device(s) found. Select one and click Launch mirror.";

        RefreshBtn.IsEnabled = true;
    }

    // ── Launch + embed ───────────────────────────────────────────────────

    private async Task CleanupStaleSessionAsync()
    {
        var staleWindow = Native.FindWindowW(null, WindowName);
        if (staleWindow != IntPtr.Zero)
            Native.PostMessageW(staleWindow, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        try
        {
            foreach (var leftover in Process.GetProcessesByName("scrcpy"))
            {
                try { if (!leftover.HasExited) leftover.Kill(true); } catch { /* already gone */ }
                leftover.Dispose();
            }
        }
        catch { /* process list unavailable — not fatal */ }

        // Free the phone-side server from any previous session.
        await KillDeviceServerAsync();
    }

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (_scrcpy == null || _adb == null)
        {
            await RefreshAsync();
            if (_scrcpy == null) return;
        }

        if (_scrcpyProcess is { HasExited: false })
        {
            StatusMessage = "A mirror session is already running — stop it first.";
            return;
        }

        // Self-healing relaunch: close any leftover mirror window and kill stray
        // scrcpy processes so a stopped session never blocks the next one.
        StatusMessage = "Preparing…";
        await CleanupStaleSessionAsync();

        var selected = DeviceList.SelectedItem as ScrcpyDevice;
        if (selected == null && DeviceList.ItemsSource is List<ScrcpyDevice> devices && devices.Count == 1)
            selected = devices[0];
        if (selected == null)
        {
            StatusMessage = "Select a device (click Refresh) or connect your phone.";
            return;
        }
        if (selected.State != "device")
        {
            StatusMessage = "Device not ready: " + selected.Description;
            return;
        }

        LaunchBtn.IsEnabled = false;

        // Ask scrcpy itself for a window sized to our pane (DPI-aware device pixels)
        // so it opens full-size from the start instead of being resized after launch.
        var pane = GetPanePixelSize();
        var baseArgs = $"-s {selected.Serial} --window-title {WindowName} --window-borderless" +
                       $" --window-width {pane.Width} --window-height {pane.Height}";
        if (!string.IsNullOrWhiteSpace(ExtraArgsBox.Text)) baseArgs += " " + ExtraArgsBox.Text.Trim();

        string lastError = "";
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            StatusMessage = attempt == 1
                ? "Starting mirror on " + selected.Description + "…"
                : $"Retrying ({attempt}/{maxAttempts}) — the previous session is still releasing the phone…";

            Task<string>? stderrTask;
            try
            {
                _scrcpyProcess = Process.Start(new ProcessStartInfo(_scrcpy, baseArgs)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                stderrTask = _scrcpyProcess!.StandardError.ReadToEndAsync();
                AppendMirrorLog($"launch (attempt {attempt}): \"{_scrcpy}\" {baseArgs}");
            }
            catch (Exception ex)
            {
                StatusMessage = "Failed to start scrcpy: " + ex.Message;
                AppendMirrorLog($"start failed: {ex.Message}");
                LaunchBtn.IsEnabled = true;
                return;
            }

            // Wait for scrcpy's window to appear, then embed it into this pane.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_scrcpyProcess.HasExited) break;

                var hwnd = Native.FindWindowW(null, WindowName);
                if (hwnd != IntPtr.Zero)
                {
                    // Capture the phone screen aspect once, before any of our resizing.
                    if (Native.GetWindowRect(hwnd, out var wr) && wr.Right > wr.Left && wr.Bottom > wr.Top)
                        _phoneAspect = (double)(wr.Right - wr.Left) / (wr.Bottom - wr.Top);

                    await EmbedMirrorAsync(hwnd);
                    LaunchBtn.IsEnabled = true;
                    return;
                }

                await Task.Delay(250);
            }

            if (_scrcpyProcess.HasExited)
            {
                string output = "";
                await Task.WhenAny(stderrTask, Task.Delay(1000));
                if (stderrTask.Status == TaskStatus.RanToCompletion) output = stderrTask.Result;
                lastError = output;

                AppendMirrorLog($"exited on attempt {attempt} (code {_scrcpyProcess.ExitCode}): {output}");
            }
            else if (attempt >= maxAttempts)
            {
                try { _scrcpyProcess.Kill(true); } catch { /* already gone */ }
                AppendMirrorLog("timed out waiting for the mirror window");
            }
            else
            {
                AppendMirrorLog($"no window appeared on attempt {attempt}; retrying");
            }

            if (attempt < maxAttempts)
                await KillDeviceServerAsync();
        }

        LaunchBtn.IsEnabled = true;

        if (_scrcpyProcess is { HasExited: true })
        {
            StatusMessage = "scrcpy exited before mirroring started." +
                (string.IsNullOrWhiteSpace(lastError) ? "" : "\n\n" + TrimOutput(lastError));
        }
        else
        {
            StatusMessage = "Timed out waiting for the mirror window. Try Launch again.";
        }
    }

    /// <summary>Kills a leftover scrcpy server on the phone so the device is free for a new session.</summary>
    private async Task KillDeviceServerAsync()
    {
        if (_adb == null) return;
        try
        {
            // [c] keeps the pattern from matching this very shell command.
            await ScrcpyService.RunAdbAsync(_adb, "shell pkill -f scr[c]py");
        }
        catch
        {
            // Older Androids may lack pkill — the retry loop still gives the server time to exit.
        }
        await Task.Delay(800);
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
            StatusMessage = "Mirror started but could not be embedded — look for a separate scrcpy window.";
            return;
        }

        Native.SetParent(hwnd, MirrorHost.ChildHwnd);
        ResizeMirror();
        Native.ShowWindow(hwnd, Native.SW_SHOW);

        StartLivenessWatch();
        StartSizeSync();
        AppendMirrorLog($"embedded: phone {_lastAppliedW}x{_lastAppliedH}, aspect {_phoneAspect:F3}");
        StatusMessage = "Mirror embedded — use the media buttons above to control playback from the laptop.";
    }

    private void ResizeMirror()
    {
        if (_scrcpyHwnd == IntPtr.Zero || MirrorHost.ChildHwnd == IntPtr.Zero) return;
        if (!Native.GetClientRect(MirrorHost.ChildHwnd, out var client)) return;

        var hostWidth = client.Right - client.Left;
        var hostHeight = client.Bottom - client.Top;
        if (hostWidth <= 0 || hostHeight <= 0) return;

        // Detect self-resizes by scrcpy (e.g. phone rotation) and adopt the new aspect.
        if (Native.GetWindowRect(_scrcpyHwnd, out var current))
        {
            var curW = current.Right - current.Left;
            var curH = current.Bottom - current.Top;
            if (curW > 0 && curH > 0 && _lastAppliedW > 0)
            {
                var devW = Math.Abs(curW - _lastAppliedW) / (double)_lastAppliedW;
                var devH = Math.Abs(curH - _lastAppliedH) / (double)_lastAppliedH;
                if (devW > 0.25 || devH > 0.25)
                    _phoneAspect = (double)curW / curH;
            }
        }

        // Fit the captured phone aspect into the pane, centred, VLC letterbox style.
        var aspect = _phoneAspect;
        if (aspect <= 0) return;

        int width, height;
        if (hostWidth / (double)hostHeight > aspect)
        {
            height = hostHeight;
            width = (int)(hostHeight * aspect);
        }
        else
        {
            width = hostWidth;
            height = (int)(hostWidth / aspect);
        }

        var x = Math.Max(0, (hostWidth - width) / 2);
        var y = Math.Max(0, (hostHeight - height) / 2);
        Native.MoveWindow(_scrcpyHwnd, x, y, width, height, true);
        _lastAppliedW = width;
        _lastAppliedH = height;

        // Live diagnostics so issues are visible in the pane itself.
        MirrorTitleText.Text = $"Mirroring — phone screen {width}×{height} in pane {hostWidth}×{hostHeight}";
    }

    private Size GetPanePixelSize()
    {
        var dpi = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
            dpi = source.CompositionTarget.TransformToDevice.M11;

        var width = Math.Max(320, (int)((ActualWidth - 10) * dpi));
        var height = Math.Max(240, (int)((ActualHeight - 42) * dpi)); // minus top bar
        return new Size(width, height);
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

    private async void OnTapCenter(object sender, RoutedEventArgs e)
    {
        if (_adb == null || _scrcpyProcess is not { HasExited: false }) return;

        try
        {
            var sizeOut = await ScrcpyService.RunAdbAsync(_adb, "shell wm size");
            var match = System.Text.RegularExpressions.Regex.Match(sizeOut, @"(\d+)x(\d+)");
            if (!match.Success) return;
            var width = int.Parse(match.Groups[1].Value);
            var height = int.Parse(match.Groups[2].Value);

            // SurfaceOrientation: 0 = portrait, 1/3 = landscape
            var orientOut = await ScrcpyService.RunAdbAsync(_adb, "shell dumpsys input | grep SurfaceOrientation");
            var orientation = 0;
            var orientMatch = System.Text.RegularExpressions.Regex.Match(orientOut, @"SurfaceOrientation\s*:\s*(\d)");
            if (orientMatch.Success) orientation = int.Parse(orientMatch.Groups[1].Value);

            int centerX, centerY;
            if (orientation % 2 == 0)
            {
                centerX = width / 2;
                centerY = height / 2;
            }
            else
            {
                centerX = height / 2;
                centerY = width / 2;
            }

            await ScrcpyService.RunAdbAsync(_adb, $"shell input tap {centerX} {centerY}");
        }
        catch
        {
            // Phone busy or disconnected — the liveness watch will catch the exit.
        }
    }

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

    private async void OnStopMirror(object sender, RoutedEventArgs e)
    {
        StatusMessage = "Stopping mirror…";
        StopMirrorCore();
        await KillDeviceServerAsync();
        ResetMirrorUi("Mirror stopped.");
    }

    private void OnBackToSetup(object sender, RoutedEventArgs e)
    {
        // Keep the mirror running; just show the setup controls again.
        MirrorSetup.Visibility = Visibility.Visible;
    }

    private void StopMirrorCore()
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
    }

    private void ResetMirrorUi(string message)
    {
        _scrcpyHwnd = IntPtr.Zero;
        _scrcpyProcess = null;
        _stderrTask = null;
        _phoneAspect = 0;
        _lastAppliedW = 0;
        _lastAppliedH = 0;

        _livenessTimer?.Stop();
        _sizeTimer?.Stop();

        MirrorPanel.Visibility = Visibility.Collapsed;
        MirrorSetup.Visibility = Visibility.Visible;
        StatusMessage = message;

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

        _ = KillDeviceServerAsync();
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
        if (_adb == null) { StatusMessage = "Refresh devices first (scrcpy/adb must be found)."; return; }

        var output = await ScrcpyService.RunAdbAsync(_adb, "tcpip 5555");
        StatusMessage = $"Wi-Fi mode: {output}\nNow unplug USB, enter the phone's IP above and click Connect.";
    }

    private async void OnWifiConnect(object sender, RoutedEventArgs e)
    {
        if (_adb == null) { StatusMessage = "Refresh devices first (scrcpy/adb must be found)."; return; }

        var ip = PhoneIpBox.Text.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            StatusMessage = "Enter the phone's IP address first (e.g. 192.168.1.8).";
            return;
        }

        var target = ip.Contains(':') ? ip : $"{ip}:5555";
        StatusMessage = $"Connecting to {target}…";
        var output = await ScrcpyService.RunAdbAsync(_adb, $"connect {target}");
        StatusMessage = $"Connect: {output}";
        await RefreshAsync();
    }
}
