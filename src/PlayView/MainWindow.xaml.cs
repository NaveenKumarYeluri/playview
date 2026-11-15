using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using PlayView.Services;
using PlayView.Views;

namespace PlayView;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, OttPane> _ottPanes = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private string _activeId = "local";
    private bool _fullscreen;
    private WindowState _prevState = WindowState.Normal;
    private WindowStyle _prevStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _prevResize = ResizeMode.CanResize;
    private double _prevLeft;
    private double _prevTop;
    private double _prevWidth;
    private double _prevHeight;
    private DispatcherTimer? _fullscreenTimer;
    private bool _sliderDragging;
    private long _lastSeekTimestamp;
    private readonly List<IntPtr> _suppressedHudWindows = new();

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;
        MirrorView.Initialize(_settings);
        MenuSidebar.IsChecked = _settings.ShowSidebar;
        if (!_settings.ShowSidebar) SidebarColumn.Width = new GridLength(0);

        BuildSidebar();
        SelectPane("local");

        _uiTimer.Tick += (_, _) => UpdateTransport();
        _uiTimer.Start();

        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            PlayerView.Shutdown();
            MirrorView.ShutdownMirror();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProcHook);
        }
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO && _fullscreen)
        {
            var mmi = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
            var hMonitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
                if (Native.GetMonitorInfoW(hMonitor, ref mi))
                {
                    mmi.ptMaxPosition.X = mi.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = mi.rcMonitor.Top;
                    mmi.ptMaxSize.X = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    mmi.ptMaxSize.Y = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
                    mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    // ── Sidebar ─────────────────────────────────────────────────────────────

    private void BuildSidebar()
    {
        AddSideItem("Local Player", "local", "▶");
        AddSideSeparator();
        foreach (var service in _settings.Services.Where(s => s.Enabled))
            AddSideItem(service.Name, service.Id, service.Name[..1].ToUpper());
        AddSideSeparator();
        AddSideItem("Phone Mirror", "mirror", "📱");
    }

    private void AddSideItem(string label, string id, string glyph)
    {
        var button = new RadioButton
        {
            Style = (Style)FindResource("SideItem"),
            Content = glyph + "  " + label,
            Tag = id,
            GroupName = "panes",
        };
        button.Click += (_, _) => SelectPane(id);
        Sidebar.Children.Add(button);
    }

    private void AddSideSeparator() => Sidebar.Children.Add(new Separator());

    // ── Pane switching ──────────────────────────────────────────────────────

    private void SelectPane(string id)
    {
        foreach (var button in Sidebar.Children.OfType<RadioButton>())
            button.IsChecked = (string?)button.Tag == id;

        // LibVLCSharp.WPF renders video through a floating overlay window that stays
        // on top of everything inside the app while the VideoView remains in the tree,
        // even when hidden — it swallows mouse clicks over the whole content area.
        // So the player pane is fully detached when another pane is active.
        if (id == "local")
        {
            if (!PaneGrid.Children.Contains(PlayerView)) PaneGrid.Children.Add(PlayerView);
            PlayerView.Visibility = Visibility.Visible;
            MirrorView.Visibility = Visibility.Collapsed;
            OttHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            PaneGrid.Children.Remove(PlayerView);
            MirrorView.Visibility = id == "mirror" ? Visibility.Visible : Visibility.Collapsed;

            if (id == "mirror")
            {
                OttHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                var pane = GetOrCreateOttPane(id);
                OttHost.Content = pane;
                OttHost.Visibility = Visibility.Visible;
                _ = pane.EnsureInitializedAsync();
            }
        }

        TransportBar.Visibility = (id == "local" || id == "mirror") ? Visibility.Visible : Visibility.Collapsed;
        PhoneControls.Visibility = id == "mirror" ? Visibility.Visible : Visibility.Collapsed;
        _activeId = id;
    }

    private OttPane GetOrCreateOttPane(string id)
    {
        if (_ottPanes.TryGetValue(id, out var existing)) return existing;

        var service = _settings.Services.First(s => s.Id == id);
        var pane = new OttPane(service);
        _ottPanes[id] = pane;
        return pane;
    }

    // ── Transport bar updates ───────────────────────────────────────────────

    private void UpdateTransport()
    {
        var accentBrush = (Brush)FindResource("Brush.Accent");
        var textBrush = (Brush)FindResource("Brush.Text");

        if (_activeId == "mirror")
        {
            bool isPlaying = MirrorView.IsPlaying;
            PlayPauseBtn.Content = isPlaying ? "⏸" : "▶";
            PlayPauseBtn.ToolTip = isPlaying ? "Pause (Space)" : "Play (Space)";
            PlayPauseBtn.Foreground = isPlaying ? accentBrush : textBrush;
            TimeText.Text = MirrorView.IsStreaming ? "LIVE" : "READY";
            StatusLabel.Text = MirrorView.StreamStatusText;
            ProgressSlider.IsEnabled = false;
            ProgressSlider.Value = 1;
            ProgressSlider.Maximum = 1;
            return;
        }

        ProgressSlider.IsEnabled = true;
        StatusLabel.Text = PlayerView.CurrentMediaTitle;
        var mp = PlayerView.Player;
        if (mp == null) return;

        bool localPlaying = mp.State == VLCState.Playing;
        PlayPauseBtn.Content = localPlaying ? "⏸" : "▶";
        PlayPauseBtn.ToolTip = localPlaying ? "Pause (Space)" : "Play (Space)";
        PlayPauseBtn.Foreground = localPlaying ? accentBrush : textBrush;

        if (_sliderDragging) return;

        // Skip timer overwrite for 600ms after user seeks so the seek completes smoothly without snapping back
        if (_lastSeekTimestamp > 0 && (Stopwatch.GetTimestamp() - _lastSeekTimestamp) < Stopwatch.Frequency * 0.6)
            return;
        _lastSeekTimestamp = 0;

        var length = Math.Max(0, mp.Length);
        if (length > 0 && Math.Abs(ProgressSlider.Maximum - length) > 0.5)
            ProgressSlider.Maximum = length;

        ProgressSlider.Value = Math.Clamp(mp.Time, 0, ProgressSlider.Maximum);
        TimeText.Text = $"{FormatTime(mp.Time)} / {FormatTime(length)}";
    }

    private static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var time = TimeSpan.FromMilliseconds(ms);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    private void OnProgressDown(object sender, MouseButtonEventArgs e)
    {
        _sliderDragging = true;
        ProgressSlider.CaptureMouse();
        SeekToMousePosition(e);
    }

    private void OnProgressMove(object sender, MouseEventArgs e)
    {
        if (_sliderDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            SeekToMousePosition(e);
        }
    }

    private void SeekToMousePosition(MouseEventArgs e)
    {
        if (ProgressSlider.ActualWidth <= 0 || ProgressSlider.Maximum <= 0) return;
        var pos = e.GetPosition(ProgressSlider);
        double ratio = Math.Clamp(pos.X / ProgressSlider.ActualWidth, 0.0, 1.0);
        ProgressSlider.Value = ratio * ProgressSlider.Maximum;
        TimeText.Text = $"{FormatTime((long)ProgressSlider.Value)} / {FormatTime((long)ProgressSlider.Maximum)}";
    }

    private void OnProgressUp(object sender, MouseButtonEventArgs e)
    {
        if (_sliderDragging)
        {
            _sliderDragging = false;
            ProgressSlider.ReleaseMouseCapture();
            if (PlayerView.Player is { } mp && mp.Media != null)
            {
                mp.Time = (long)ProgressSlider.Value;
                _lastSeekTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    private void OnProgressMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_activeId == "local")
        {
            int deltaSec = e.Delta > 0 ? 5 : -5;
            PlayerView.SeekRelative(deltaSec * 1000);
            e.Handled = true;
        }
    }

    private int _savedVolume = 85;
    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0)
        {
            _savedVolume = (int)Math.Round(VolumeSlider.Value);
            VolumeSlider.Value = 0;
            MuteBtn.Content = "🔇";
            MuteBtn.ToolTip = "Unmute (M)";
        }
        else
        {
            VolumeSlider.Value = _savedVolume > 0 ? _savedVolume : 85;
            MuteBtn.Content = "🔊";
            MuteBtn.ToolTip = "Mute (M)";
        }
    }

    private async void OnPhoneBack(object sender, RoutedEventArgs e) => await MirrorView.PhoneBackAsync();
    private async void OnPhoneHome(object sender, RoutedEventArgs e) => await MirrorView.PhoneHomeAsync();
    private async void OnPhoneTapCenter(object sender, RoutedEventArgs e) => await MirrorView.PhoneTapCenterAsync();
    private void OnMirrorSetup(object sender, RoutedEventArgs e) => MirrorView.ReturnToSetup();

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int vol = (int)Math.Round(VolumeSlider.Value);
        if (PlayerView?.Player != null)
            PlayerView.Player.Volume = vol;
        if (_activeId == "mirror" && MirrorView != null)
            MirrorView.SetVolume(vol);
        if (MuteBtn != null)
        {
            MuteBtn.Content = vol == 0 ? "🔇" : "🔊";
            MuteBtn.ToolTip = vol == 0 ? "Unmute (M)" : "Mute (M)";
        }
        if (VolumeSlider != null)
            VolumeSlider.ToolTip = $"Volume ({vol}%)";
    }

    private void OnVolumeMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double step = e.Delta > 0 ? 5 : -5;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + step, VolumeSlider.Minimum, VolumeSlider.Maximum);
        e.Handled = true;
    }

    private void OnPlay(object sender, RoutedEventArgs e) => OnPlayPause(sender, e);
    private void OnPause(object sender, RoutedEventArgs e) => OnPlayPause(sender, e);

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_activeId == "mirror") MirrorView.TogglePlayPause();
        else PlayerView.TogglePlayPause();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_activeId == "mirror") MirrorView.Stop();
        else PlayerView.Stop();
    }

    private void OnBack10(object sender, RoutedEventArgs e)
    {
        if (_activeId == "mirror") MirrorView.SeekRelative(-10_000);
        else PlayerView.SeekRelative(-10_000);
    }

    private void OnFwd10(object sender, RoutedEventArgs e)
    {
        if (_activeId == "mirror") MirrorView.SeekRelative(10_000);
        else PlayerView.SeekRelative(10_000);
    }

    // ── Media open / drop ───────────────────────────────────────────────────

    private void OnOpenFile(object? sender, RoutedEventArgs? e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open media",
            Filter = "Media files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.mp3;*.aac;*.flac;*.m3u;*.m3u8|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
            PlayerView.OpenFiles(dialog.FileNames);
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs? e)
    {
        var dialog = new OpenFolderDialog { Title = "Open folder of media" };
        if (dialog.ShowDialog(this) != true) return;

        var files = Directory.EnumerateFiles(dialog.FolderName)
            .Where(f => PlayerPane.IsMediaFile(f))
            .Order()
            .ToList();

        if (files.Count == 0)
        {
            MessageBox.Show(this, "No media files found in that folder.", "PlayView",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PlayerView.OpenFiles(files);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (_activeId != "local") return;
        PlayerView.OpenFiles(files);
    }

    // ── Menu commands ───────────────────────────────────────────────────────

    public void OnTogglePlaylist(object? sender = null, RoutedEventArgs? e = null)
    {
        if (_activeId != "local")
            SelectPane("local");
        PlayerView.TogglePlaylist();
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        _settings.ShowSidebar = MenuSidebar.IsChecked;
        _settings.Save();
        SidebarColumn.Width = _settings.ShowSidebar ? new GridLength(180) : new GridLength(0);
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.DataDir}\"") { UseShellExecute = true });
    }

    private void OnReloadSettings(object sender, RoutedEventArgs e)
    {
        foreach (var pane in _ottPanes.Values) pane.Dispose();
        _ottPanes.Clear();
        OttHost.Content = null;
        Sidebar.Children.Clear();

        var fresh = AppSettings.Load();
        _settings.ScrcpyPath = fresh.ScrcpyPath;
        _settings.ShowSidebar = fresh.ShowSidebar;
        _settings.Services = fresh.Services;

        BuildSidebar();
        SelectPane("local");
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "PlayView 0.1.0\n\n" +
            "A personal media hub: VLC-style local playback, official OTT web players, " +
            "and Android phone mirroring via scrcpy.\n\n" +
            "Local engine: LibVLC. Web panes: WebView2. Mirroring: scrcpy (GPL, separate process).",
            "About PlayView", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Fullscreen ──────────────────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point { public int X; public int Y; }

    private Win32Point _lastMousePos;
    private int _mouseIdleTicks;

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    public void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            _prevLeft = Left;
            _prevTop = Top;
            _prevWidth = Width;
            _prevHeight = Height;
            _fullscreen = true;

            // 1. Hide TitleBar, MenuBar, Sidebar
            TitleBarRow.Height = new GridLength(0);
            TitleBarGrid.Visibility = Visibility.Collapsed;
            MenuBarRow.Height = new GridLength(0);
            MenuBar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);

            // 2. Detach WindowChrome so it doesn't intercept or clip to taskbar work area
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);

            // 3. Obtain monitor bounds for the window's current monitor
            var hwnd = new WindowInteropHelper(this).Handle;
            var hMonitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfoW(hMonitor, ref mi);

            var dpi = VisualTreeHelper.GetDpi(this);
            double monX = mi.rcMonitor.Left / dpi.DpiScaleX;
            double monY = mi.rcMonitor.Top / dpi.DpiScaleY;
            double monW = (mi.rcMonitor.Right - mi.rcMonitor.Left) / dpi.DpiScaleX;
            double monH = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / dpi.DpiScaleY;

            // 4. True edge-to-edge fullscreen as a topmost normal window.
            // Keeping WindowState = Normal prevents Windows Explorer / DWM from clipping to the taskbar!
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;

            Left = monX;
            Top = monY;
            Width = monW;
            Height = monH;

            // Force topmost and exact physical monitor coordinates via Win32 SetWindowPos
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
                mi.rcMonitor.Left, mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcMonitor.Left,
                mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);

            SuppressNetworkMonitor();
            StartFullscreenAutoHide();
        }
        else
        {
            _fullscreen = false;
            StopFullscreenAutoHide();
            RestoreNetworkMonitor();

            // 1. Restore style and window state
            Topmost = false;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = _prevState;

            if (_prevState == WindowState.Normal)
            {
                Left = _prevLeft;
                Top = _prevTop;
                Width = _prevWidth;
                Height = _prevHeight;
            }

            // 2. Re-attach WindowChrome
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, WindowChromeInst);
            WindowChromeInst.CaptionHeight = 34;
            WindowChromeInst.ResizeBorderThickness = new Thickness(6);

            // 3. Restore TitleBar, MenuBar, Sidebar
            TitleBarRow.Height = new GridLength(34);
            TitleBarGrid.Visibility = Visibility.Visible;
            MenuBarRow.Height = GridLength.Auto;
            MenuBar.Visibility = Visibility.Visible;
            SidebarColumn.Width = _settings.ShowSidebar ? new GridLength(180) : new GridLength(0);
            TransportBar.Visibility = (_activeId == "local" || _activeId == "mirror") ? Visibility.Visible : Visibility.Collapsed;

            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
        }
    }

    private void StartFullscreenAutoHide()
    {
        _fullscreenTimer?.Stop();
        _mouseIdleTicks = 0;
        GetCursorPos(out _lastMousePos);

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _fullscreenTimer.Tick += (_, _) =>
        {
            if (!_fullscreen) return;

            if (GetCursorPos(out var currentPos))
            {
                bool moved = Math.Abs(currentPos.X - _lastMousePos.X) > 2 || Math.Abs(currentPos.Y - _lastMousePos.Y) > 2;
                if (moved)
                {
                    _lastMousePos = currentPos;
                    _mouseIdleTicks = 0;
                    if (TransportBar.Visibility != Visibility.Visible)
                        TransportBar.Visibility = Visibility.Visible;
                    return;
                }

                // If cursor is physically over TransportBar, keep controls visible
                if (TransportBar.IsVisible)
                {
                    try
                    {
                        var origin = TransportBar.PointToScreen(new Point(0, 0));
                        var dpi = VisualTreeHelper.GetDpi(this);
                        var barRect = new Rect(origin.X, origin.Y,
                            TransportBar.ActualWidth * dpi.DpiScaleX,
                            TransportBar.ActualHeight * dpi.DpiScaleY);
                        if (barRect.Contains(new Point(currentPos.X, currentPos.Y)))
                        {
                            _mouseIdleTicks = 0;
                            return;
                        }
                    }
                    catch { }
                }
            }

            _mouseIdleTicks++;
            if (_mouseIdleTicks % 5 == 0)
            {
                SuppressNetworkMonitor();
                var curHwnd = new WindowInteropHelper(this).Handle;
                Native.SetWindowPos(curHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            // 25 ticks of 100ms = 2.5s of stillness
            if (_mouseIdleTicks >= 25 && !_sliderDragging)
            {
                if (TransportBar.Visibility != Visibility.Collapsed)
                    TransportBar.Visibility = Visibility.Collapsed;
            }
        };
        _fullscreenTimer.Start();
    }

    private void SuppressNetworkMonitor()
    {
        try
        {
            Native.EnumWindows((h, _) =>
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                string? procName = null;
                try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }

                var sbTitle = new System.Text.StringBuilder(256);
                Native.GetWindowTextW(h, sbTitle, 256);
                string title = sbTitle.ToString();

                var sbClass = new System.Text.StringBuilder(256);
                Native.GetClassNameW(h, sbClass, 256);
                string cls = sbClass.ToString();

                bool isNetMon = (procName != null && procName.Contains("NetworkMonitor", StringComparison.OrdinalIgnoreCase))
                                || title == "HUD"
                                || (cls.Contains("WinUI") && title.Contains("Network", StringComparison.OrdinalIgnoreCase));

                if (isNetMon)
                {
                    if (!_suppressedHudWindows.Contains(h))
                        _suppressedHudWindows.Add(h);

                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                    Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
                    Native.SetLayeredWindowAttributes(h, 0, 0, Native.LWA_ALPHA);
                    Native.ShowWindow(h, Native.SW_HIDE);
                    Native.SetWindowPos(h, Native.HWND_NOTOPMOST, -10000, -10000, 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    private void RestoreNetworkMonitor()
    {
        try
        {
            foreach (var h in _suppressedHudWindows)
            {
                try
                {
                    Native.SetLayeredWindowAttributes(h, 0, 255, Native.LWA_ALPHA);
                    Native.ShowWindow(h, Native.SW_SHOWNOACTIVATE);
                }
                catch { }
            }
            _suppressedHudWindows.Clear();
        }
        catch { }
    }

    private void StopFullscreenAutoHide()
    {
        _fullscreenTimer?.Stop();
        _fullscreenTimer = null;
        TransportBar.Visibility = (_activeId == "local" || _activeId == "mirror") ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_fullscreen && (_activeId == "local" || _activeId == "mirror"))
        {
            _mouseIdleTicks = 0;
            if (TransportBar.Visibility != Visibility.Visible)
                TransportBar.Visibility = Visibility.Visible;
        }
    }

    // ── Keyboard shortcuts (VLC-style) ──────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_fullscreen && e.Key == Key.Escape)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.M)
        {
            OnToggleMute(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OnOpenFile(null, null);
            e.Handled = true;
            return;
        }

        if (e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.O)
        {
            OnOpenFolder(null, null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            if (_activeId == "mirror") MirrorView.TogglePlayPause();
            else if (_activeId == "local") PlayerView.TogglePlayPause();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            if (_activeId == "mirror") MirrorView.SeekRelative(-10_000);
            else if (_activeId == "local") PlayerView.SeekRelative(-10_000);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            if (_activeId == "mirror") MirrorView.SeekRelative(10_000);
            else if (_activeId == "local") PlayerView.SeekRelative(10_000);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.L && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            OnTogglePlaylist();
            e.Handled = true;
            return;
        }
    }

    // ── Caption buttons ─────────────────────────────────────────────────────

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
