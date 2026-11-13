using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
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
    private int _lastAppliedW;
    private int _lastAppliedH;
    private int _lastAppliedX;
    private int _lastAppliedY;
    private bool _embedded;
    private bool _windowHooked;

    // PlayView stream (LibVLC playback of the phone's MPEG-TS stream)
    private const int StreamPort = 48335;
    private LibVLC? _streamLibVlc;
    private MediaPlayer? _streamPlayer;
    private bool _streaming;

    public MirrorPane()
    {
        InitializeComponent();

        // The VLC VideoView hosts a floating overlay window that swallows clicks
        // while it is in the visual tree (same issue as the player pane) — so it
        // stays out of the tree until a stream actually starts.
        StreamGrid.Children.Remove(StreamVideo);

        Loaded += async (_, _) =>
        {
            HookWindowEvents();
            await RefreshAsync();
        };
        MirrorHost.SizeChanged += (_, _) => RepositionSoon();

        // The mirror lives in its own native window — WPF's Visibility
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
        var visible = (bool)e.NewValue;

        if (_scrcpyHwnd != IntPtr.Zero)
            Native.ShowWindow(_scrcpyHwnd, visible ? Native.SW_SHOWNA : Native.SW_HIDE);

        // VLC stream VideoView: detach when hidden (its overlay would swallow
        // clicks in whatever pane is active), re-attach when shown.
        if (visible)
        {
            if (_streaming && StreamVideo.Parent == null)
                StreamGrid.Children.Add(StreamVideo);
        }
        else if (StreamVideo.Parent is System.Windows.Controls.Panel panel)
            panel.Children.Remove(StreamVideo);
    }

    // ── PlayView stream (phone streams MPEG-TS, VLC plays it) ─────────────

    private async void OnStreamStartUsb(object sender, RoutedEventArgs e)
    {
        if (_adb == null)
        {
            StatusMessage = "Connect the phone over USB (with USB debugging) first — click Refresh.";
            return;
        }

        StatusMessage = "Setting up the USB stream tunnel…";
        try
        {
            var output = await ScrcpyService.RunAdbAsync(_adb, $"forward tcp:{StreamPort} tcp:{StreamPort}");
            AppendMirrorLog($"adb forward: {output}");
        }
        catch (Exception ex)
        {
            StatusMessage = "adb forward failed: " + ex.Message;
            return;
        }

        StartStream($"http://127.0.0.1:{StreamPort}");
    }

    private void OnStreamStartWifi(object sender, RoutedEventArgs e)
    {
        var ip = StreamIpBox.Text.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            StatusMessage = "Enter the phone's IP (shown in the PlayView Stream app) for Wi-Fi streaming.";
            return;
        }

        StartStream($"http://{ip}:{StreamPort}");
    }

    private async void StartStream(string url)
    {
        if (_streaming)
        {
            StatusMessage = "A stream is already playing — stop it first.";
            return;
        }
        if (_scrcpyProcess is { HasExited: false })
        {
            StatusMessage = "The scrcpy mirror is running — stop it before streaming.";
            return;
        }

        StatusMessage = "Connecting to the phone's stream…";
        AppendMirrorLog($"stream start: {url}");

        try
        {
            _streamLibVlc ??= new LibVLC("--no-video-title-show");
            _streamPlayer ??= CreateStreamPlayer();

            // Attach the VideoView first — it needs to be loaded to host video.
            MirrorSetup.Visibility = Visibility.Collapsed;
            MirrorPanel.Visibility = Visibility.Visible;
            StreamVideo.Visibility = Visibility.Visible;
            MirrorHost.Visibility = Visibility.Collapsed;
            MirrorTitleText.Text = "Streaming — connecting…";

            if (StreamVideo.Parent == null)
                StreamGrid.Children.Add(StreamVideo);

            for (var i = 0; i < 50 && !StreamVideo.IsLoaded; i++)
                await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            StreamVideo.MediaPlayer = _streamPlayer;

            using var media = new Media(_streamLibVlc, new Uri(url));
            media.AddOption(":network-caching=300");
            _streamPlayer.Play(media);

            _streaming = true;
            StatusMessage = "Streaming from the phone through VLC's engine.";
        }
        catch (Exception ex)
        {
            AppendMirrorLog($"stream failed: {ex.Message}");
            EndStreamUi("Could not start the stream: " + ex.Message +
                        "\n\nMake sure the PlayView Stream app is running on the phone (Start streaming) " +
                        "and the phone is connected (USB) or on the same Wi-Fi network.");
        }
    }

    private MediaPlayer CreateStreamPlayer()
    {
        var player = new MediaPlayer(_streamLibVlc!) { Volume = 85 };
        player.EndReached += (_, _) => Dispatcher.InvokeAsync(
            () => EndStreamUi("Stream ended — the phone stopped streaming or disconnected."));
        player.EncounteredError += (_, _) => Dispatcher.InvokeAsync(
            () => EndStreamUi("Stream error — check that the PlayView Stream app is still streaming."));
        return player;
    }

    private void OnStreamStop(object sender, RoutedEventArgs e)
    {
        try { if (_streamPlayer is { IsPlaying: true }) _streamPlayer.Stop(); }
        catch { /* already stopped */ }
        EndStreamUi("Stream stopped.");
    }

    private void EndStreamUi(string message)
    {
        if (!_streaming && StreamVideo.Parent == null)
        {
            StatusMessage = message;
            return;
        }

        _streaming = false;
        try { _streamPlayer?.Stop(); } catch { /* already stopped */ }

        if (StreamVideo.Parent is System.Windows.Controls.Panel panel)
            panel.Children.Remove(StreamVideo);
        StreamVideo.Visibility = Visibility.Collapsed;
        MirrorHost.Visibility = Visibility.Visible;

        MirrorPanel.Visibility = Visibility.Collapsed;
        MirrorSetup.Visibility = Visibility.Visible;
        ReturnBtn.Visibility = Visibility.Collapsed;
        MirrorTitleText.Text = "";
        StatusMessage = message;

        AppendMirrorLog("stream stopped");
        if (_adb != null) _ = RefreshAsync();
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
            if (_streaming)
            {
                StatusMessage = "A stream is playing — stop it before launching the mirror.";
                return;
            }
            if (_embedded)
            {
                StatusMessage = "A mirror session is already running — stop it first.";
                return;
            }

            // A previous session started but never embedded — clean it up and go again.
            try { _scrcpyProcess.Kill(true); } catch { /* already gone */ }
            _scrcpyProcess = null;
            await CleanupStaleSessionAsync();
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

        // Show the mirror panel first so the pane area has its real laid-out size,
        // letting scrcpy open exactly there — no post-creation resize (that is
        // what reintroduced letterbox artifacts).
        MirrorSetup.Visibility = Visibility.Collapsed;
        MirrorPanel.Visibility = Visibility.Visible;
        MirrorTitleText.Text = "Starting mirror…";

        for (var i = 0; i < 100 && MirrorHost.ActualWidth < 10; i++)
            await Task.Delay(50);

        // Open scrcpy's window already sitting on the pane area (physical pixels),
        // full pane size — scrcpy letterboxes the video inside, exactly like a
        // plain scrcpy window. No hiding, no post-creation surgery.
        var pane = GetPanePixelRect();
        var baseArgs = $"-s {selected.Serial} --window-title {WindowName} --window-borderless" +
                       $" --window-x {pane.X} --window-y {pane.Y}" +
                       $" --window-width {pane.W} --window-height {pane.H}";
        if (SoftwareRenderCheck.IsChecked == true)
            baseArgs += " --render-driver=software";
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
                AppendMirrorLog($"start failed: {ex.Message}");
                ResetMirrorUi("Failed to start scrcpy: " + ex.Message);
                return;
            }

            // Wait for scrcpy's window to appear, then embed it into this pane.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_scrcpyProcess.HasExited) break;

                var hwnd = Native.FindWindowW(null, WindowName);
                if (hwnd == IntPtr.Zero)
                    hwnd = Native.FindWindowW("SDL_app", null); // fallback: scrcpy's window class

                if (hwnd != IntPtr.Zero)
                {
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

        if (_scrcpyProcess is { HasExited: true })
        {
            ResetMirrorUi("scrcpy exited before mirroring started." +
                (string.IsNullOrWhiteSpace(lastError) ? "" : "\n\n" + TrimOutput(lastError)));
        }
        else
        {
            ResetMirrorUi("Timed out waiting for the mirror window. Try Launch again.");
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

        // scrcpy created its window already at the pane rect — we never hide,
        // restyle or resize it at embed time (that blacks out the D3D swapchain).
        MirrorSetup.Visibility = Visibility.Collapsed;
        MirrorPanel.Visibility = Visibility.Visible;
        _embedded = true;

        // Correct any drift between the estimate at launch and the real layout.
        for (var i = 0; i < 100 && MirrorHost.ActualWidth < 10; i++)
            await Task.Delay(50);

        if (MirrorHost.ActualWidth < 10)
        {
            AppendMirrorLog("embed failed: pane never got a size");
            StopMirrorCore();
            _ = KillDeviceServerAsync();
            ResetMirrorUi("Mirror could not be placed into the pane — press Launch again.");
            return;
        }

        PositionFloatingMirror();

        StartLivenessWatch();
        StartSizeSync();
        AppendMirrorLog($"embedded (floating): window {_lastAppliedW}x{_lastAppliedH} at ({_lastAppliedX},{_lastAppliedY})");
        StatusMessage = "Mirror embedded — use the media buttons above to control playback from the laptop.";
    }

    private void HookWindowEvents()
    {
        if (_windowHooked) return;
        var win = Window.GetWindow(this);
        if (win == null) return;

        _windowHooked = true;
        win.LocationChanged += (_, _) => RepositionSoon();
        win.SizeChanged += (_, _) => RepositionSoon();
        win.Activated += (_, _) =>
        {
            // Back in our app: re-glue the mirror over the pane, above everything.
            if (!_embedded || _scrcpyHwnd == IntPtr.Zero) return;
            Native.ShowWindow(_scrcpyHwnd, Native.SW_SHOWNA);
            PositionFloatingMirror(force: true);
        };
        win.Deactivated += (_, _) =>
        {
            // Another app got focus: hide the mirror so it never floats over it —
            // unless the user clicked the mirror itself (playing with the phone).
            if (!_embedded || _scrcpyHwnd == IntPtr.Zero) return;
            if (Native.GetForegroundWindow() != _scrcpyHwnd)
                Native.ShowWindow(_scrcpyHwnd, Native.SW_HIDE);
        };
        win.StateChanged += (_, _) =>
        {
            if (!_embedded || _scrcpyHwnd == IntPtr.Zero || win == null) return;

            if (win.WindowState == WindowState.Minimized)
                Native.ShowWindow(_scrcpyHwnd, Native.SW_HIDE);
            else
            {
                Native.ShowWindow(_scrcpyHwnd, Native.SW_SHOWNA);
                RepositionSoon();
            }
        };
    }

    /// <summary>Repositions the floating mirror after any move/resize, batched per layout pass.</summary>
    private void RepositionSoon()
    {
        if (_scrcpyHwnd == IntPtr.Zero) return;
        Dispatcher.InvokeAsync(() => PositionFloatingMirror(), DispatcherPriority.Loaded);
    }

    private void PositionFloatingMirror(bool force = false)
    {
        if (_scrcpyHwnd == IntPtr.Zero) return;

        var paneW = MirrorHost.ActualWidth;
        var paneH = MirrorHost.ActualHeight;
        if (paneW < 10 || paneH < 10) return;

        // Pane area in physical screen pixels (PointToScreen is DPI-aware).
        var topLeft = MirrorHost.PointToScreen(new Point(0, 0));
        var dpi = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
            dpi = source.CompositionTarget.TransformToDevice.M11;

        var panePixelW = (int)Math.Round(paneW * dpi);
        var panePixelH = (int)Math.Round(paneH * dpi);
        var x = (int)topLeft.X;
        var y = (int)topLeft.Y;

        // Skip no-op moves — needless repaints are what leave ghost trails behind.
        if (!force && panePixelW == _lastAppliedW && panePixelH == _lastAppliedH
            && x == _lastAppliedX && y == _lastAppliedY)
        {
            MirrorTitleText.Text = $"Mirroring — window {panePixelW}×{panePixelH} (scrcpy fits the video itself)";
            return;
        }

        // Full pane rect, in the topmost band so our window can never bury it —
        // the pane's own chrome (top bar) stays below its area anyway. scrcpy
        // letterboxes the video inside its own window, VLC-style.
        Native.SetWindowPos(_scrcpyHwnd, Native.HWND_TOPMOST, x, y, panePixelW, panePixelH,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _lastAppliedW = panePixelW;
        _lastAppliedH = panePixelH;
        _lastAppliedX = x;
        _lastAppliedY = y;

        // Live diagnostics so issues are visible in the pane itself.
        MirrorTitleText.Text = $"Mirroring — window {panePixelW}×{panePixelH} at ({x},{y})";
    }

    /// <summary>Pane area in physical screen pixels, for scrcpy's launch args.</summary>
    private (int X, int Y, int W, int H) GetPanePixelRect()
    {
        var dpi = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
            dpi = source.CompositionTarget.TransformToDevice.M11;

        var origin = MirrorHost.PointToScreen(new Point(0, 0));
        var w = Math.Max(320, (int)(MirrorHost.ActualWidth * dpi));
        var h = Math.Max(240, (int)(MirrorHost.ActualHeight * dpi));
        return ((int)origin.X, (int)origin.Y, w, h);
    }

    private void StartSizeSync()
    {
        _sizeTimer?.Stop();
        _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _sizeTimer.Tick += (_, _) => PositionFloatingMirror();
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

    private sealed record DeviceScreen(int W, int H, int Orientation);

    /// <summary>Real device resolution + current orientation, queried over adb.</summary>
    private async Task<DeviceScreen?> GetDeviceScreenAsync()
    {
        if (_adb == null) return null;
        try
        {
            var sizeOut = await ScrcpyService.RunAdbAsync(_adb, "shell wm size");
            var match = Regex.Match(sizeOut, @"(\d+)x(\d+)");
            if (!match.Success) return null;
            var w = int.Parse(match.Groups[1].Value);
            var h = int.Parse(match.Groups[2].Value);
            if (w <= 0 || h <= 0) return null;

            // SurfaceOrientation: 0 = portrait, 1/3 = landscape
            var orientOut = await ScrcpyService.RunAdbAsync(_adb, "shell dumpsys input | grep SurfaceOrientation");
            var orientMatch = Regex.Match(orientOut, @"SurfaceOrientation\s*:\s*(\d)");
            var orientation = orientMatch.Success ? int.Parse(orientMatch.Groups[1].Value) : 0;

            return new DeviceScreen(w, h, orientation);
        }
        catch
        {
            return null;
        }
    }

    private async void OnTapCenter(object sender, RoutedEventArgs e)
    {
        if (_adb == null || _scrcpyProcess is not { HasExited: false }) return;

        var screen = await GetDeviceScreenAsync();
        if (screen == null) return;

        int centerX, centerY;
        if (screen.Orientation % 2 == 0)
        {
            centerX = screen.W / 2;
            centerY = screen.H / 2;
        }
        else
        {
            centerX = screen.H / 2;
            centerY = screen.W / 2;
        }

        try
        {
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
        if (_streaming)
        {
            // Detach the video view so the setup controls are clickable.
            if (StreamVideo.Parent is System.Windows.Controls.Panel panel)
                panel.Children.Remove(StreamVideo);

            ReturnBtn.Visibility = Visibility.Visible;
            MirrorSetup.Visibility = Visibility.Visible;
            return;
        }

        // Keep the mirror running; just show the setup controls again.
        if (_scrcpyHwnd != IntPtr.Zero)
            Native.ShowWindow(_scrcpyHwnd, Native.SW_HIDE);

        ReturnBtn.Visibility = Visibility.Visible;
        MirrorSetup.Visibility = Visibility.Visible;
    }

    private void OnReturnToMirror(object sender, RoutedEventArgs e)
    {
        MirrorSetup.Visibility = Visibility.Collapsed;
        MirrorPanel.Visibility = Visibility.Visible;

        if (_streaming)
        {
            if (StreamVideo.Parent == null)
                StreamGrid.Children.Add(StreamVideo);
            StreamVideo.Visibility = Visibility.Visible;
            MirrorHost.Visibility = Visibility.Collapsed;
            return;
        }

        if (_scrcpyHwnd != IntPtr.Zero)
        {
            Native.ShowWindow(_scrcpyHwnd, Native.SW_SHOWNA);
            RepositionSoon();
        }
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
        _lastAppliedW = 0;
        _lastAppliedH = 0;
        _lastAppliedX = 0;
        _lastAppliedY = 0;
        _embedded = false;

        _livenessTimer?.Stop();
        _sizeTimer?.Stop();

        MirrorPanel.Visibility = Visibility.Collapsed;
        MirrorSetup.Visibility = Visibility.Visible;
        ReturnBtn.Visibility = Visibility.Collapsed;
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

        try
        {
            if (_streamPlayer != null)
            {
                _streaming = false;
                if (_streamPlayer.IsPlaying) _streamPlayer.Stop();
                _streamPlayer.Dispose();
                _streamPlayer = null;
            }
            _streamLibVlc?.Dispose();
            _streamLibVlc = null;
        }
        catch { /* player already gone */ }

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
