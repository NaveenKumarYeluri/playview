using System.Collections.ObjectModel;
using System.IO;
using LibVLCSharp.Shared;
using System.Windows;
using System.Windows.Controls;

namespace PlayView.Views;

public partial class PlayerPane : UserControl
{
    private readonly LibVLC _libVlc;
    private readonly ObservableCollection<string> _playlist = new();
    private int _index = -1;

    public MediaPlayer Player { get; }

    public PlayerPane()
    {
        InitializeComponent();

        _libVlc = new LibVLC("--no-video-title-show");
        Player = new MediaPlayer(_libVlc) { Volume = 85 };
        Video.MediaPlayer = Player;

        PlaylistList.ItemsSource = _playlist;

        Player.EndReached += (_, _) => Dispatcher.InvokeAsync(PlayNext);
        Player.Playing += (_, _) => Dispatcher.InvokeAsync(HideHint);
        Player.Stopped += (_, _) => Dispatcher.InvokeAsync(ShowHint);
        Player.EncounteredError += (_, _) => Dispatcher.InvokeAsync(ShowHint);
    }

    public void OpenFiles(IEnumerable<string> files)
    {
        var added = files.Where(File.Exists).ToList();
        if (added.Count == 0) return;

        var firstNew = _playlist.Count;
        foreach (var file in added) _playlist.Add(file);

        _index = firstNew;
        if (_playlist.Count > 1) PlaylistList.Visibility = Visibility.Visible;
        PlayCurrent();
    }

    private void PlayCurrent()
    {
        if (_index < 0 || _index >= _playlist.Count) return;

        var path = _playlist[_index];
        using var media = new Media(_libVlc, new Uri(path));
        Player.Play(media);

        HintBorder.Visibility = Visibility.Collapsed;
        PlaylistList.SelectedItem = path;
        PlaylistList.ScrollIntoView(path);
    }

    private void PlayNext()
    {
        if (_index + 1 < _playlist.Count)
        {
            _index++;
            PlayCurrent();
        }
        else
        {
            Player.Stop();
        }
    }

    public void Play()
    {
        if (Player.State == VLCState.Paused)
        {
            Player.Play();
        }
        else if (Player.State != VLCState.Playing)
        {
            if (_playlist.Count > 0 && (_index < 0 || _index >= _playlist.Count)) _index = 0;
            if (_index >= 0) PlayCurrent();
        }
    }

    public void Pause()
    {
        if (Player.State == VLCState.Playing)
            Player.Pause();
    }

    public void TogglePlayPause()
    {
        switch (Player.State)
        {
            case VLCState.Playing:
                Player.Pause();
                break;
            case VLCState.Paused:
                Player.Play();
                break;
            default:
                if (_playlist.Count > 0 && (_index < 0 || _index >= _playlist.Count)) _index = 0;
                if (_index >= 0) PlayCurrent();
                break;
        }
    }

    public void Stop()
    {
        Player.Stop();
    }

    public void SeekRelative(long deltaMs)
    {
        if (Player.State is not (VLCState.Playing or VLCState.Paused)) return;
        Player.Time = Math.Clamp(Player.Time + deltaMs, 0, Math.Max(0, Player.Length));
    }

    public void TogglePlaylist()
    {
        PlaylistList.Visibility = PlaylistList.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnVideoMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            (Window.GetWindow(this) as MainWindow)?.ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void HideHint() => HintBorder.Visibility = Visibility.Collapsed;
    private void ShowHint() => HintBorder.Visibility = Visibility.Visible;

    private void OnPlaylistDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlaylistList.SelectedItem is string path)
        {
            _index = _playlist.IndexOf(path);
            PlayCurrent();
        }
    }

    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mp3", ".aac", ".flac",
    };

    public static bool IsMediaFile(string path) =>
        MediaExtensions.Contains(Path.GetExtension(path));

    public string CurrentMediaTitle =>
        (_index >= 0 && _index < _playlist.Count) ? Path.GetFileName(_playlist[_index]) : "";

    public void Shutdown()
    {
        Player.Dispose();
        _libVlc.Dispose();
    }
}
