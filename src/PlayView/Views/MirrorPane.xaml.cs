using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    private string? _streamUrl;
    private int _streamRestarts;
    private bool _bufferLow = true;
    private Media? _streamMedia;          // kept alive so Statistics stay readable
    private DispatcherTimer? _streamAliveTimer;
    private ulong _lastDemuxBytes;
    private int _stallTicks;
    private bool _voutNudged;
    // VLC renders straight into this borderless window's HWND — no WPF
    // airspace tricks, no LibVLCSharp overlay windows.
    private Window? _streamWindow;
    private IntPtr _streamHwnd;

    public MirrorPane()
    {
        InitializeComponent();

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

    private static readonly object LogLock = new();
    // VLC fires bursts of identical lines (buffering, control-query spam);
    // writing every one floods the file and the disk — dedupe instead.
    private static string _logLast = "";
    private static DateTime _logLastTime = DateTime.MinValue;

    private static void AppendMirrorLog(string line)
    {
        try
        {
            lock (LogLock)
            {
                if (line == _logLast && (DateTime.Now - _logLastTime).TotalMilliseconds < 1000)
                    return;
                _logLast = line;
                _logLastTime = DateTime.Now;

                Directory.CreateDirectory(AppSettings.DataDir);
                File.AppendAllText(Path.Combine(AppSettings.DataDir, "mirror.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    // ── Visibility plumbing ──────────────────────────────────────────────

    /// <summary>The floating window to glue over the pane right now:
    /// the stream video window while streaming, otherwise the scrcpy window.</summary>
    private IntPtr FloatHwnd => _streaming ? _streamHwnd : _scrcpyHwnd;

    private void OnPaneVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var visible = (bool)e.NewValue;

        if (_scrcpyHwnd != IntPtr.Zero)
            Native.ShowWindow(_scrcpyHwnd, visible ? Native.SW_SHOWNA : Native.SW_HIDE);

        // The stream's own video window: hide when the pane hides, re-show and
        // re-glue when it comes back.
        if (_streaming)
        {
            if (visible) ShowStreamWindow();
            else _streamWindow?.Hide();
        }
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

        if (!await ProbePhoneAsync("127.0.0.1", "USB"))
            return;
        StartStream($"http://127.0.0.1:{StreamPort}");
    }

    private async void OnStreamStartWifi(object sender, RoutedEventArgs e)
    {
        var ip = StreamIpBox.Text.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            StatusMessage = "Enter the phone's IP (shown in the PlayView Stream app) for Wi-Fi streaming.";
            return;
        }

        if (!await ProbePhoneAsync(ip, "Wi-Fi"))
            return;
        StartStream($"http://{ip}:{StreamPort}");
    }

    /// <summary>Pre-flight that proves the phone is actually serving its stream:
    /// TCP-connect is not enough (adb's forward accepts on the laptop even when
    /// nothing listens on the phone), so we send a GET and require a real
    /// HTTP response. Gives a precise message instead of a silent VLC failure.</summary>
    private async Task<bool> ProbePhoneAsync(string host, string mode)
    {
        StatusMessage = $"Testing the {mode} connection to the phone…";
        AppendMirrorLog($"probe {host}:{StreamPort} ({mode})");

        try
        {
            // The phone must answer the HTTP GET — that's the real test.
            // (adb's forward accepts TCP on the laptop even when nothing
            // listens on the phone, so connect alone proves nothing.)
            var ok = await Task.Run(async () =>
            {
                using var tcp = new TcpClient();
                var connect = tcp.ConnectAsync(host, StreamPort);
                if (await Task.WhenAny(connect, Task.Delay(3000)) != connect || !tcp.Connected)
                    throw new IOException("no connection");

                var stream = tcp.GetStream();
                stream.ReadTimeout = 4000;
                var req = System.Text.Encoding.ASCII.GetBytes(
                    $"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
                stream.Write(req, 0, req.Length);

                var head = new byte[128];
                int read;
                try { read = stream.Read(head, 0, head.Length); }
                catch (IOException) { throw new IOException("no HTTP answer in 4 s"); }
                var answer = System.Text.Encoding.ASCII.GetString(head, 0, Math.Max(read, 0));
                AppendMirrorLog("probe answer: " + answer.Replace("\r\n", " | "));
                return read > 0 && answer.StartsWith("HTTP/1.1 200", StringComparison.Ordinal);
            });

            AppendMirrorLog($"probe {host}: " + (ok ? "streaming (got HTTP 200)" : "bad response"));
            if (ok) return true;
        }
        catch (Exception ex)
        {
            AppendMirrorLog($"probe {host}: {ex.Message}");
        }

        StatusMessage = mode == "USB"
            ? "The phone isn't serving its stream through the USB tunnel. On the phone, open PlayView Stream " +
              "and tap \"Start streaming\" again (accept the capture prompt) — the status there must say " +
              "\"Streaming\". Then click Start (USB)."
            : $"Can't reach {host}. Make sure PlayView Stream says \"Streaming\" on the phone and both " +
              "devices are on the same Wi-Fi network, then click Start (Wi-Fi) again.";
        AppendMirrorLog($"probe {host}: not streaming");
        return false;
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
            _streamLibVlc ??= CreateStreamLibVlc();
            _streamPlayer ??= CreateStreamPlayer();

            MirrorSetup.Visibility = Visibility.Collapsed;
            MirrorPanel.Visibility = Visibility.Visible;
            MirrorTitleText.Text = "Streaming — connecting…";

            // Set before showing the window — the position/focus glue keys off it.
            _streaming = true;

            // VLC sizes its video to the window's size when the output is
            // created — a pane that is still laying out would give a tiny
            // picture forever. Wait for a real size first.
            for (var i = 0; i < 100 && MirrorHost.ActualWidth < 10; i++)
                await Task.Delay(20);
            if (MirrorHost.ActualWidth < 10)
                throw new InvalidOperationException("the mirror pane never got its layout");

            ShowStreamWindow();

            for (var i = 0; i < 50 && _streamHwnd == IntPtr.Zero; i++)
                await Task.Delay(20);
            if (_streamHwnd == IntPtr.Zero)
                throw new InvalidOperationException("video window never appeared");

            PositionFloatingMirror(force: true);
            _streamPlayer.Hwnd = _streamHwnd;

            _streamUrl = url;
            PlayStreamMedia();
            StartStreamAliveWatch();

            StatusMessage = "Streaming from the phone through VLC's engine.";

            // If VLC is still silent after 10 s, say so instead of leaving the
            // user staring at a black pane.
            _ = Task.Delay(10000).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                if (_streaming && _streamPlayer is { State: not VLCState.Playing })
                {
                    StatusMessage = "No picture yet — VLC is waiting for the phone's stream. " +
                        "Make sure the phone app says \"Streaming\" (Start streaming), then try again.";
                    AppendMirrorLog("stream: no data after 10 s (state: " + _streamPlayer.State + ")");
                }
                else if (_streaming)
                {
                    AppendMirrorLog($"stream: 10 s check — state {_streamPlayer.State}, " +
                        $"video outputs: {_streamPlayer.VoutCount}");
                }
            }));
        }
        catch (Exception ex)
        {
            AppendMirrorLog($"stream failed: {ex.Message}");
            EndStreamUi("Could not start the stream: " + ex.Message +
                        "\n\nMake sure the PlayView Stream app is running on the phone (Start streaming) " +
                        "and the phone is connected (USB) or on the same Wi-Fi network.");
        }
    }

    /// <summary>VLC instance for the phone stream, with its own log mirrored
    /// into mirror.log — decoder/display problems are named there.</summary>
    private LibVLC CreateStreamLibVlc()
    {
        var lib = new LibVLC("--no-video-title-show", "--verbose=2");
        lib.Log += (_, e) =>
        {
            if (e.Level >= LogLevel.Notice)
            {
                AppendMirrorLog("vlc: " + e.Message);
            }
            else
            {
                // Debug lines are filtered to the display pipeline — that's
                // where resize/swapchain problems show up. Everything else
                // would drown the log (Lua probes, per-packet demux spam).
                var m = e.Message;
                if (m.Contains("vout") || m.Contains("Vout") || m.Contains("d3d11") ||
                    m.Contains("Direct3D") || m.Contains("swapchain") || m.Contains("resize") ||
                    m.Contains("Detected") || m.Contains("sub-window"))
                    AppendMirrorLog("vlc·: " + m);
            }
        };
        return lib;
    }

    private MediaPlayer CreateStreamPlayer()
    {
        var player = new MediaPlayer(_streamLibVlc!) { Volume = 85 };
        player.Playing += (_, _) =>
        {
            AppendMirrorLog("stream: VLC is playing");
            _streamRestarts = 0; // a stable connection resets the retry budget
        };
        player.Buffering += (_, e) =>
        {
            // Log state transitions only — VLC fires this dozens of times
            // per second while the cache fluctuates.
            var low = e.Cache < 100;
            if (low == _bufferLow) return;
            _bufferLow = low;
            if (low) AppendMirrorLog("stream: buffering");
        };
        player.EndReached += (_, _) => Dispatcher.InvokeAsync(() => OnStreamEnded());
        player.EncounteredError += (_, _) => Dispatcher.InvokeAsync(() => OnStreamEnded());
        return player;
    }

    /// <summary>End of stream (phone closed, hiccup, decoder error): retry once
    /// or twice before giving up — the phone restarts its session per client,
    /// so an immediate reconnect usually recovers.
    /// NOTE: never call player.Stop() from inside an EndReached callback —
    /// VLC deadlocks waiting for its own input thread (frozen UI).</summary>
    private void OnStreamEnded()
    {
        if (!_streaming) return;

        if (_streamRestarts < 2)
        {
            _streamRestarts++;
            AppendMirrorLog($"stream: ended — auto-restart #{_streamRestarts}");
            try
            {
                PlayStreamMedia();
                return;
            }
            catch (Exception ex)
            {
                AppendMirrorLog("stream: restart failed — " + ex.Message);
            }
        }

        ShowStreamNotice("Stream ended — the phone stopped streaming or disconnected. " +
                         "Start streaming on the phone again, then click Start (USB/Wi-Fi).");
    }

    private void PlayStreamMedia()
    {
        // Keep our own reference — Statistics (bytes read) are read every
        // second by the liveness watch below.
        _streamMedia = new Media(_streamLibVlc!, new Uri(_streamUrl!));
        // A live stream whose data trickles in (static screen) needs a real
        // buffer — VLC's default is far too tight for it.
        _streamMedia.AddOption(":network-caching=1500");
        _lastDemuxBytes = 0;
        _stallTicks = 0;
        _voutNudged = false;
        _streamPlayer!.Play(_streamMedia);
    }

    /// <summary>Watches the stream's input: the phone always sends something
    /// (keepalive packets every 250 ms even on a static screen), so if VLC's
    /// demuxer stops reading bytes while claiming to play, the connection is
    /// dead — VLC 3.0 itself never notices a closed socket.</summary>
    private void StartStreamAliveWatch()
    {
        _streamAliveTimer?.Stop();
        _lastDemuxBytes = 0;
        _stallTicks = 0;
        _streamAliveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _streamAliveTimer.Tick += (_, _) =>
        {
            if (!_streaming) return;

        // Once the video output exists, nudge the window by one pixel so VLC
        // re-reads the real client size and re-places the video. This heals
        // the rare race where the vout opened against a stale window size and
        // rendered the picture tiny; the 400 ms size-sync timer restores the
        // exact size a moment later.
        if (!_voutNudged && _streamPlayer.VoutCount > 0)
        {
            _voutNudged = true;
            if (Native.GetWindowRect(_streamHwnd, out var rect))
            {
                AppendMirrorLog($"stream: vout up ({_streamPlayer.VoutCount}) — re-place nudge");
                Native.SetWindowPos(_streamHwnd, IntPtr.Zero, 0, 0,
                    rect.Right - rect.Left, rect.Bottom - rect.Top - 1,
                    Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
        }

        ulong bytes = 0;
        try
        {
            var st = _streamMedia?.Statistics;
            if (st.HasValue) bytes = Convert.ToUInt64(st.Value.DemuxReadBytes);
        }
        catch { }

        if (bytes == _lastDemuxBytes && _streamPlayer is { State: VLCState.Playing })
        {
            _stallTicks++;
            if (_stallTicks == 5)
                AppendMirrorLog("stream: no new bytes for 5 s…");
            if (_stallTicks >= 10)
            {
                AppendMirrorLog("stream: input stalled 10 s — treating as ended");
                _stallTicks = 0;
                OnStreamEnded();
            }
        }
        else
        {
            _stallTicks = 0;
            _lastDemuxBytes = bytes;
        }
    };
        _streamAliveTimer.Start();
    }

    /// <summary>Reports a stream problem without yanking the user back to setup —
    /// the pane stays visible and the message is readable.</summary>
    private void ShowStreamNotice(string message)
    {
        StatusMessage = message;
        AppendMirrorLog("stream: " + message);
        MirrorTitleText.Text = "PlayView stream";
        _streamWindow?.Hide();
    }

    /// <summary>VLC's video home: a borderless top-level window, positioned over
    /// the pane with the same glue as the scrcpy fallback. VLC renders directly
    /// into its HWND — no WPF airspace issues possible.</summary>
    private void EnsureStreamWindow()
    {
        if (_streamWindow != null) return;

        _streamWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Background = System.Windows.Media.Brushes.Black,
            Title = "PlayViewStream",
        };
    }

    private void ShowStreamWindow()
    {
        EnsureStreamWindow();
        var win = _streamWindow!;
        if (!win.IsVisible)
        {
            win.Show();
            _streamHwnd = new WindowInteropHelper(win).EnsureHandle();
            // Topmost band, like the scrcpy fallback window, so the pane can
            // never bury it behind our chrome.
            Native.SetWindowPos(_streamHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        PositionFloatingMirror(force: true);
    }

    private void OnStreamStop(object sender, RoutedEventArgs e)
    {
        try { if (_streamPlayer is { IsPlaying: true }) _streamPlayer.Stop(); }
        catch { /* already stopped */ }
        EndStreamUi("Stream stopped.");
    }

    private void EndStreamUi(string message)
    {
        if (!_streaming && (_streamWindow == null || !_streamWindow.IsVisible))
        {
            StatusMessage = message;
            return;
        }

        _streaming = false;
        _streamAliveTimer?.Stop();
        // Stop can block on VLC's threads — do it off the UI thread so the
        // pane never freezes while VLC winds down.
        var player = _streamPlayer;
        if (player != null)
            Task.Run(() => { try { if (player.IsPlaying) player.Stop(); } catch { } });

        _streamWindow?.Hide();

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
            // Back in our app: re-glue the floating window over the pane.
            var hwnd = FloatHwnd;
            if (hwnd == IntPtr.Zero) return;
            Native.ShowWindow(hwnd, Native.SW_SHOWNA);
            PositionFloatingMirror(force: true);
        };
        win.Deactivated += (_, _) =>
        {
            // Another app got focus: hide the floating window so it never
            // floats over it — unless the user clicked it (playing with the phone).
            if (!_embedded && !_streaming) return;
            var hwnd = FloatHwnd;
            if (hwnd == IntPtr.Zero || Native.GetForegroundWindow() == hwnd) return;
            Native.ShowWindow(hwnd, Native.SW_HIDE);
        };
        win.StateChanged += (_, _) =>
        {
            var hwnd = FloatHwnd;
            if (hwnd == IntPtr.Zero || win == null) return;

            if (win.WindowState == WindowState.Minimized)
                Native.ShowWindow(hwnd, Native.SW_HIDE);
            else
            {
                Native.ShowWindow(hwnd, Native.SW_SHOWNA);
                RepositionSoon();
            }
        };
    }

    /// <summary>Repositions the floating window after any move/resize, batched per layout pass.</summary>
    private void RepositionSoon()
    {
        if (FloatHwnd == IntPtr.Zero) return;
        Dispatcher.InvokeAsync(() => PositionFloatingMirror(), DispatcherPriority.Loaded);
    }

    private void PositionFloatingMirror(bool force = false)
    {
        var hwnd = FloatHwnd;
        if (hwnd == IntPtr.Zero) return;

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

        // Clamp to the visible work area — a maximized WPF window extends past
        // the screen edges, and our topmost window would cover the taskbar.
        var wa = SystemParameters.WorkArea;
        var waLeft = (int)(wa.Left * dpi);
        var waTop = (int)(wa.Top * dpi);
        var waRight = (int)((wa.Left + wa.Width) * dpi);
        var waBottom = (int)((wa.Top + wa.Height) * dpi);
        x = Math.Max(x, waLeft);
        y = Math.Max(y, waTop);
        panePixelW = Math.Min(x + panePixelW, waRight) - x;
        panePixelH = Math.Min(y + panePixelH, waBottom) - y;
        if (panePixelW < 10 || panePixelH < 10) return;

        // Skip no-op moves — needless repaints are what leave ghost trails behind.
        if (!force && panePixelW == _lastAppliedW && panePixelH == _lastAppliedH
            && x == _lastAppliedX && y == _lastAppliedY)
        {
            MirrorTitleText.Text = _streaming
                ? "Streaming — live"
                : $"Mirroring — window {panePixelW}×{panePixelH} (scrcpy fits the video itself)";
            return;
        }

        // Full pane rect, in the topmost band so our window can never bury it —
        // the pane's own chrome (top bar) stays below its area anyway.
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, panePixelW, panePixelH,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _lastAppliedW = panePixelW;
        _lastAppliedH = panePixelH;
        _lastAppliedX = x;
        _lastAppliedY = y;

        // Live diagnostics so issues are visible in the pane itself.
        MirrorTitleText.Text = _streaming
            ? $"Streaming — live ({panePixelW}×{panePixelH})"
            : $"Mirroring — window {panePixelW}×{panePixelH} at ({x},{y})";
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

    private void OnStopMirror(object sender, RoutedEventArgs e)
    {
        // During streaming the ⏹ must end the STREAM (hide the floating video
        // window too) — the old code only killed scrcpy and left the stream
        // window floating over the whole app, blocking every click.
        if (_streaming)
        {
            EndStreamUi("Stream stopped.");
            return;
        }

        StatusMessage = "Stopping mirror…";
        StopMirrorCore();
        _ = KillDeviceServerAsync();
        ResetMirrorUi("Mirror stopped.");
    }

    private void OnBackToSetup(object sender, RoutedEventArgs e)
    {
        if (_streaming)
        {
            // Hide the video window so the setup controls are clickable.
            _streamWindow?.Hide();

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
            ShowStreamWindow();
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

        try
        {
            if (_streamWindow != null)
            {
                _streamWindow.Close();
                _streamWindow = null;
            }
            _streamHwnd = IntPtr.Zero;
        }
        catch { /* window already gone */ }

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
