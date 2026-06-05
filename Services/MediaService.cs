using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using GsmtcManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using GsmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Lintel.Services;

public sealed class MediaSnapshot
{
    public bool HasMedia;
    public string Title = "";
    public string Artist = "";
    public bool IsPlaying;
    public ImageSource? Cover;
    public TimeSpan Position;
    public TimeSpan Duration;
}

/// <summary>Wraps the Windows now-playing (System Media Transport Controls) session.</summary>
public sealed class MediaService
{
    private readonly Dispatcher _dispatcher;
    private GsmtcManager? _manager;
    private GsmtcSession? _session;
    private readonly DispatcherTimer _poll;

    private ImageSource? _cover;
    private string _coverKey = "";

    public MediaSnapshot Current { get; private set; } = new();
    public event Action? Changed;

    public MediaService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Refresh();
    }

    public async void Start()
    {
        try
        {
            _manager = await GsmtcManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(Hook);
            Hook();
            _poll.Start();
        }
        catch { /* media APIs unavailable */ }
    }

    private void Hook()
    {
        if (_session != null)
        {
            try
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
            }
            catch { }
        }
        _session = _manager?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
            _session.TimelinePropertiesChanged += OnSessionChanged;
        }
        Refresh();
    }

    private void OnSessionChanged(GsmtcSession sender, object args) => Refresh();

    private async void Refresh()
    {
        var snap = new MediaSnapshot();
        var s = _session;
        if (s != null)
        {
            try
            {
                var info = s.GetPlaybackInfo();
                snap.IsPlaying = info.PlaybackStatus == PlaybackStatus.Playing;

                var tl = s.GetTimelineProperties();
                snap.Position = tl.Position;
                snap.Duration = tl.EndTime;

                var props = await s.TryGetMediaPropertiesAsync();
                snap.Title = props.Title ?? "";
                snap.Artist = props.Artist ?? "";
                snap.HasMedia = !string.IsNullOrWhiteSpace(snap.Title);

                if (props.Thumbnail != null && snap.HasMedia)
                {
                    string key = snap.Title + "|" + snap.Artist;
                    if (key != _coverKey)
                    {
                        _cover = await LoadThumb(props.Thumbnail);
                        _coverKey = key;
                    }
                    snap.Cover = _cover;
                }
                else { _coverKey = ""; _cover = null; }
            }
            catch { }
        }

        _ = _dispatcher.BeginInvoke(() => { Current = snap; Changed?.Invoke(); });
    }

    private static async Task<ImageSource?> LoadThumb(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public async void TogglePlay() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }
}
