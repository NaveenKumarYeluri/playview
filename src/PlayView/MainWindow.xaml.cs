using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _sliderDragging;

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

        TransportBar.Visibility = id == "local" ? Visibility.Visible : Visibility.Collapsed;
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
        var mp = PlayerView.Player;
        if (mp == null) return;

        PlayPauseBtn.Content = mp.State == VLCState.Playing ? "⏸" : "▶";

        if (_sliderDragging) return;

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

    private void OnProgressDown(object sender, MouseButtonEventArgs e) => _sliderDragging = true;

    private void OnProgressUp(object sender, MouseButtonEventArgs e)
    {
        _sliderDragging = false;
        if (PlayerView.Player is { } mp && mp.Media != null)
            mp.Time = (long)ProgressSlider.Value;
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PlayerView != null && PlayerView.Player != null)
            PlayerView.Player.Volume = (int)VolumeSlider.Value;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => PlayerView.TogglePlayPause();
    private void OnStop(object sender, RoutedEventArgs e) => PlayerView.Stop();
    private void OnBack10(object sender, RoutedEventArgs e) => PlayerView.SeekRelative(-10_000);
    private void OnFwd10(object sender, RoutedEventArgs e) => PlayerView.SeekRelative(10_000);

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

    private void OnTogglePlaylist(object sender, RoutedEventArgs e) => PlayerView.TogglePlaylist();

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

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState;
            _fullscreen = true;

            MenuBar.Visibility = Visibility.Collapsed;
            TransportBar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);

            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _fullscreen = false;

            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _prevState;

            MenuBar.Visibility = Visibility.Visible;
            TransportBar.Visibility = _activeId == "local" ? Visibility.Visible : Visibility.Collapsed;
            SidebarColumn.Width = _settings.ShowSidebar ? new GridLength(180) : new GridLength(0);
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

        if (_activeId != "local") return;

        switch (e.Key)
        {
            case Key.Space:
                PlayerView.TogglePlayPause();
                e.Handled = true;
                break;
            case Key.F:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Left:
                PlayerView.SeekRelative(-10_000);
                e.Handled = true;
                break;
            case Key.Right:
                PlayerView.SeekRelative(10_000);
                e.Handled = true;
                break;
        }
    }

    // ── Caption buttons ─────────────────────────────────────────────────────

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
