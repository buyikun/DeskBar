using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DeskBar.Services;

internal sealed class MediaTrackInfo
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string AppId { get; init; } = "";
    public bool IsPlaying { get; init; }
    public ImageSource? Thumbnail { get; init; }
    public bool CanPlayPause { get; init; } = true;
    public bool CanSkipNext { get; init; } = true;
    public bool CanSkipPrev { get; init; } = true;
}

internal sealed class MediaSessionService : IDisposable
{
    private readonly AppSettings _settings;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private readonly object _lock = new();
    private volatile bool _disposed;
    private string? _lastTrackId;
    private System.Windows.Threading.DispatcherTimer? _pollTimer;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public event Action<MediaTrackInfo?>? MediaInfoChanged;

    public MediaTrackInfo? CurrentInfo { get; private set; }

    public MediaSessionService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager == null)
            {
                Log.Warn("GSMTC session manager is not available on this platform.");
                return;
            }

            _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
            _sessionManager.SessionsChanged += OnSessionsChanged;

            Log.Info("GSMTC MediaSessionService initialized.");
            await UpdateActiveSessionAsync();
            StartPollTimer();
        }
        catch (Exception ex)
        {
            Log.Error($"GSMTC init failed: {ex.Message}");
        }
    }

    public Task ReevaluateAsync() => RefreshMediaPropertiesAsync();

    private void StartPollTimer()
    {
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pollTimer.Tick += async (_, _) =>
        {
            if (!_disposed) await UpdateActiveSessionAsync();
        };
        _pollTimer.Start();
    }

    private void StopPollTimer()
    {
        try { _pollTimer?.Stop(); } catch { }
        _pollTimer = null;
    }

    private async void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (_disposed) return;
        await UpdateActiveSessionAsync();
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        if (_disposed) return;
        await UpdateActiveSessionAsync();
    }

    private async Task UpdateActiveSessionAsync()
    {
        if (_disposed || _sessionManager == null) return;

        GlobalSystemMediaTransportControlsSession? targetSession = null;
        try
        {
            targetSession = _sessionManager.GetCurrentSession();

            var sessions = _sessionManager.GetSessions();
            if (targetSession == null && sessions != null && sessions.Count > 0)
            {
                foreach (var s in sessions)
                {
                    var appId = s.SourceAppUserModelId ?? "";
                    if (IsAppAllowed(appId))
                    {
                        targetSession = s;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"GSMTC session query error: {ex.Message}");
        }

        lock (_lock)
        {
            if (ReferenceEquals(_currentSession, targetSession))
            {
                _ = RefreshMediaPropertiesAsync();
                return;
            }

            UnsubscribeCurrentSession();

            _currentSession = targetSession;
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
        }

        await RefreshMediaPropertiesAsync();
    }

    private void UnsubscribeCurrentSession()
    {
        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch { }
            _currentSession = null;
        }
    }

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (_disposed) return;
        await RefreshMediaPropertiesAsync();
    }

    private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (_disposed) return;
        await RefreshMediaPropertiesAsync();
    }

    private bool IsAppAllowed(string appId)
    {
        if (string.IsNullOrWhiteSpace(_settings.MusicCapsuleWhitelist))
            return true;

        var list = _settings.MusicCapsuleWhitelist.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in list)
        {
            if (appId.Contains(item.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task RefreshMediaPropertiesAsync()
    {
        if (_disposed) return;

        GlobalSystemMediaTransportControlsSession? session;
        lock (_lock)
        {
            session = _currentSession;
        }

        if (session == null)
        {
            _lastTrackId = null;
            CurrentInfo = null;
            MediaInfoChanged?.Invoke(null);
            return;
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null)
            {
                _lastTrackId = null;
                CurrentInfo = null;
                MediaInfoChanged?.Invoke(null);
                return;
            }

            var appId = session.SourceAppUserModelId ?? "";
            if (!IsAppAllowed(appId))
            {
                _lastTrackId = null;
                CurrentInfo = null;
                MediaInfoChanged?.Invoke(null);
                return;
            }

            string title = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title.Trim();
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? "未知歌手" : props.Artist.Trim();

            ImageSource? thumbnailImage = null;
            bool isRealThumbnail = false;

            // 1. 如果 GSMTC 声明有封面，尽力多次重试解码本地流
            if (props.Thumbnail != null)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using var streamRef = await props.Thumbnail.OpenReadAsync();
                        if (streamRef != null && streamRef.Size > 0)
                        {
                            using var reader = new DataReader(streamRef.GetInputStreamAt(0));
                            await reader.LoadAsync((uint)streamRef.Size);
                            byte[] bytes = new byte[streamRef.Size];
                            reader.ReadBytes(bytes);

                            if (bytes.Length > 0)
                            {
                                var ms = new MemoryStream(bytes);
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.StreamSource = ms;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                thumbnailImage = bitmap;
                                isRealThumbnail = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (attempt < 2)
                        {
                            await Task.Delay(100 * (attempt + 1));
                        }
                        else
                        {
                            Log.Debug($"GSMTC Thumbnail stream decode failed: {ex.Message}");
                        }
                    }
                }
            }

            // 2. 如果本地流无法读取，使用 QQ 音乐官方公开接口在线提取高清封面
            if (!isRealThumbnail && !string.IsNullOrWhiteSpace(title) && title != "未知曲目")
            {
                var netCover = await TryFetchCoverFromNetworkAsync(title, artist);
                if (netCover != null)
                {
                    thumbnailImage = netCover;
                    isRealThumbnail = true;
                }
            }

            // 3. 终极兜底：播放器 app icon
            thumbnailImage ??= AppIconHelper.GetIcon(appId);

            var trackId = $"{appId}|{title}|{artist}|{isPlaying}|{isRealThumbnail}";
            if (trackId == _lastTrackId && CurrentInfo != null)
            {
                return;
            }

            var controls = playbackInfo?.Controls;
            var info = new MediaTrackInfo
            {
                Title = title,
                Artist = artist,
                Album = props.AlbumTitle ?? "",
                AppId = appId,
                IsPlaying = isPlaying,
                Thumbnail = thumbnailImage,
                CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? true,
                CanSkipNext = controls?.IsNextEnabled ?? true,
                CanSkipPrev = controls?.IsPreviousEnabled ?? true
            };

            Log.Debug($"media info: title='{info.Title}' artist='{info.Artist}' playing={isPlaying} thumb={thumbnailImage != null} real={isRealThumbnail} app={appId}");
            _lastTrackId = trackId;
            CurrentInfo = info;
            MediaInfoChanged?.Invoke(info);
        }
        catch (Exception ex)
        {
            Log.Debug($"RefreshMediaPropertiesAsync exception: {ex.Message}");
            CurrentInfo = null;
            MediaInfoChanged?.Invoke(null);
        }
    }

    private static async Task<ImageSource?> TryFetchCoverFromNetworkAsync(string title, string artist)
    {
        try
        {
            string cleanTitle = title.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Trim();
            string cleanArtist = artist.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Trim();

            // QQ音乐 API 抽取 albummid
            string qqUrl = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w=" + Uri.EscapeDataString(cleanTitle + " " + cleanArtist) + "&n=1&format=json";
            var qqResp = await _httpClient.GetStringAsync(qqUrl);
            using var doc = JsonDocument.Parse(qqResp);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("song", out var song) &&
                song.TryGetProperty("list", out var list) &&
                list.GetArrayLength() > 0)
            {
                var first = list[0];
                if (first.TryGetProperty("albummid", out var albummidEl))
                {
                    string albummid = albummidEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(albummid))
                    {
                        string imgUrl = "https://y.gtimg.cn/music/photo_new/T002R300x300M000" + albummid + ".jpg";
                        var bytes = await _httpClient.GetByteArrayAsync(imgUrl);
                        if (bytes != null && bytes.Length > 1000)
                        {
                            return LoadBitmapFromBytes(bytes);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Network cover fetch error for {title}: {ex.Message}");
        }

        return null;
    }

    private static BitmapImage? LoadBitmapFromBytes(byte[] bytes)
    {
        try
        {
            var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    public async Task<bool> TogglePlayPauseAsync()
    {
        try
        {
            if (_currentSession != null)
            {
                return await _currentSession.TryTogglePlayPauseAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"TogglePlayPauseAsync failed: {ex.Message}");
        }
        return false;
    }

    public async Task<bool> SkipNextAsync()
    {
        try
        {
            if (_currentSession != null)
            {
                return await _currentSession.TrySkipNextAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"SkipNextAsync failed: {ex.Message}");
        }
        return false;
    }

    public async Task<bool> SkipPreviousAsync()
    {
        try
        {
            if (_currentSession != null)
            {
                return await _currentSession.TrySkipPreviousAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"SkipPreviousAsync failed: {ex.Message}");
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _pollTimer?.Stop(); _pollTimer = null; } catch { }

        lock (_lock)
        {
            UnsubscribeCurrentSession();

            if (_sessionManager != null)
            {
                try
                {
                    _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
                    _sessionManager.SessionsChanged -= OnSessionsChanged;
                }
                catch { }
                _sessionManager = null;
            }
        }
        Log.Info("GSMTC MediaSessionService disposed.");
    }
}
