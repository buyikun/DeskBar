using System;
using System.Collections.Generic;
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
    public string FriendlyAppName { get; init; } = "";
    public bool IsPlaying { get; init; }
    public ImageSource? Thumbnail { get; init; }
    public bool CanPlayPause { get; init; } = true;
    public bool CanSkipNext { get; init; } = true;
    public bool CanSkipPrev { get; init; } = true;
    public TimeSpan Position { get; init; } = TimeSpan.Zero;
    public TimeSpan Duration { get; init; } = TimeSpan.Zero;
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
    private System.Windows.Threading.DispatcherTimer? _lyricTimer;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private TimeSpan _currentPosition = TimeSpan.Zero;
    private TimeSpan _currentDuration = TimeSpan.Zero;
    private DateTimeOffset _lastTimelineUpdate = DateTimeOffset.MinValue;
    /// <summary>本地收到 timeline 快照的时刻；用它外推比用播放器上报的 LastUpdatedTime 可靠。</summary>
    private DateTimeOffset _positionSampleTime = DateTimeOffset.MinValue;
    private TrackLyrics? _currentTrackLyrics;
    private double _currentLyricProgress;

    public event Action<MediaTrackInfo?>? MediaInfoChanged;
    public event Action<string?>? LyricUpdated;
    /// <summary>当前这句歌词的演唱进度 0..1，用来驱动胶囊里的横向滚动。</summary>
    public event Action<double>? LyricProgressChanged;
    /// <summary>
    /// 每次进度推进都带上「当前行 + 播放位置」，供胶囊算逐字高亮宽度与滚动锚点。
    /// 只有进度不够：同一行内位置在变，已唱像素宽度也要跟着变。
    /// </summary>
    public event Action<LyricLine?, TimeSpan>? LyricTick;

    public MediaTrackInfo? CurrentInfo { get; private set; }
    public string? CurrentLyric { get; private set; }
    public double CurrentLyricProgress => _currentLyricProgress;

    public MediaSessionService(AppSettings settings)
    {
        _settings = settings;
        // 恢复上次手动固定的来源。没有就保持自动选择。
        _pinnedAppId = string.IsNullOrEmpty(settings.MusicCapsulePinnedSource)
            ? null
            : settings.MusicCapsulePinnedSource;
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
            StartTimers();
        }
        catch (Exception ex)
        {
            Log.Error($"GSMTC init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 设置改动后重新评估。白名单变化后不能只刷新属性：
    /// 当前来源可能已被白名单排除，要重新走一遍选源才能切到另一个被允许的来源。
    /// </summary>
    public Task ReevaluateAsync() => UpdateActiveSessionAsync();

    private void StartTimers()
    {
        // 3秒轮询会话状态与有效性
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pollTimer.Tick += async (_, _) =>
        {
            if (!_disposed) await UpdateActiveSessionAsync();
        };
        _pollTimer.Start();

        // 100毫秒推算播放进度、匹配歌词行，并推进句内进度（横向滚动依赖它够平滑）
        _lyricTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _lyricTimer.Tick += (_, _) =>
        {
            if (_disposed) return;
            CheckLyricProgress();
        };
        _lyricTimer.Start();
    }

    private void CheckLyricProgress()
    {
        if (CurrentInfo == null || !CurrentInfo.IsPlaying || _currentTrackLyrics == null)
        {
            if (CurrentLyric != null)
            {
                CurrentLyric = null;
                LyricUpdated?.Invoke(null);
            }
            SetLyricProgress(0);
            LyricTick?.Invoke(null, TimeSpan.Zero);
            return;
        }

        var estimatedPos = GetEstimatedPosition(true);
        var line = _currentTrackLyrics.GetCurrentLine(estimatedPos);

        if (line?.Text != CurrentLyric)
        {
            CurrentLyric = line?.Text;
            LyricUpdated?.Invoke(CurrentLyric);
        }

        SetLyricProgress(line?.GetProgress(estimatedPos) ?? 0);
        LyricTick?.Invoke(line, estimatedPos);
    }

    private void SetLyricProgress(double progress)
    {
        if (Math.Abs(progress - _currentLyricProgress) < 0.002) return;
        _currentLyricProgress = progress;
        LyricProgressChanged?.Invoke(progress);
    }

    private void UpdateTimeline(GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline)
    {
        if (timeline == null) return;
        _currentPosition = timeline.Position;
        _currentDuration = timeline.EndTime - timeline.StartTime;
        _lastTimelineUpdate = timeline.LastUpdatedTime;
        _positionSampleTime = DateTimeOffset.UtcNow;
    }

    public TimeSpan GetEstimatedPosition(bool isPlaying)
    {
        if (!isPlaying || _positionSampleTime == DateTimeOffset.MinValue)
            return _currentPosition;

        // 基准必须是"本地收到快照的时刻"。播放器上报的 LastUpdatedTime 经常长期不刷新，
        // 拿它当基准会让 elapsed 越滚越大、歌词整体超前——这正是跳句不准的原因。
        var elapsed = DateTimeOffset.UtcNow - _positionSampleTime;

        // 超过 30 秒没有新的 timeline 事件，说明播放器已停或暂停，不要再外推。
        if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(30))
        {
            var estimated = _currentPosition + elapsed;
            if (_currentDuration > TimeSpan.Zero && estimated > _currentDuration)
                return _currentDuration;
            return estimated;
        }
        return _currentPosition;
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

    private string? _pinnedAppId;
    public string? PinnedAppId => _pinnedAppId;

    /// <summary>每个来源第一次出现时的序号，用于「后启动优先」判据。</summary>
    private readonly Dictionary<string, long> _firstSeen = new(StringComparer.OrdinalIgnoreCase);
    private long _monotonic;
    /// <summary>当前正在显示的来源 AppId（空表示没有）。</summary>
    private string _currentAppId = "";
    /// <summary>当前来源最后一次出现在会话列表里的时刻；配合 DepartGrace 实现「暂停不切源」。</summary>
    private DateTime _currentLastSeenUtc = DateTime.MinValue;
    /// <summary>上一次选源日志的摘要，用于抑制重复日志。</summary>
    private string _lastPickLog = "";

    public void PinSource(string? appId)
    {
        SetPinnedAppId(appId, persist: true);
        _ = UpdateActiveSessionAsync();
    }

    /// <summary>
    /// 记住 / 忘记固定来源。以前这个字段只在内存里，重启就丢，
    /// 用户每次开机都得重新点一遍「媒体来源」。
    /// </summary>
    private void SetPinnedAppId(string? appId, bool persist)
    {
        _pinnedAppId = string.IsNullOrEmpty(appId) ? null : appId;
        if (!persist || _settings == null) return;

        string stored = _pinnedAppId ?? "";
        if (string.Equals(_settings.MusicCapsulePinnedSource, stored, StringComparison.OrdinalIgnoreCase)) return;

        _settings.MusicCapsulePinnedSource = stored;
        try { _settings.Save(); }
        catch (Exception ex) { Log.Warn($"persist pinned source failed: {ex.Message}"); }
    }

    public static string GetFriendlySourceName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "媒体播放器";

        string lower = appId.ToLowerInvariant();
        if (lower.Contains("cloudmusic") || lower.Contains("netease")) return "网易云音乐";
        if (lower.Contains("qqmusic") || lower.Contains("qq音乐")) return "QQ音乐";
        if (lower.Contains("kugou") || lower.Contains("酷狗")) return "酷狗音乐";
        if (lower.Contains("kuwo") || lower.Contains("kwmusic") || lower.Contains("酷我")) return "酷我音乐";
        if (lower.Contains("sodamusic") || lower.Contains("汽水") || lower.Contains("luna")) return "汽水音乐";
        if (lower.Contains("applemusic") || lower.Contains("amlibraryagent")) return "Apple Music";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("foobar2000")) return "Foobar2000";
        if (lower.Contains("potplayer")) return "PotPlayer";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("mpc-hc")) return "MPC-HC";
        if (lower.Contains("msedge") || lower.Contains("microsoftedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("bilibili") || lower.Contains("哔哩")) return "哔哩哔哩";
        if (lower.Contains("douyin") || lower.Contains("抖音")) return "抖音";
        if (lower.Contains("wmplayer") || lower.Contains("mediaplayer")) return "媒体播放器";

        try
        {
            if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileNameWithoutExtension(appId);
            }
            if (appId.Contains('!'))
            {
                var parts = appId.Split('!');
                return parts[0];
            }
        }
        catch { }

        return appId;
    }

    /// <summary>
    /// 当前所有「像内容来源」的会话 AppId（只做系统级排除，不套白名单）。
    /// 设置页用它反过来告诉用户「你填的白名单把谁挡在外面了」。
    /// </summary>
    public IReadOnlyList<string> GetRawSources() => CollectSources(applyWhitelist: false);

    public IReadOnlyList<string> GetAvailableSources() => CollectSources(applyWhitelist: true);

    private List<string> CollectSources(bool applyWhitelist)
    {
        var list = new List<string>();
        if (_sessionManager == null) return list;

        try
        {
            var sessions = _sessionManager.GetSessions();
            if (sessions == null) return list;

            foreach (var s in sessions)
            {
                var id = s.SourceAppUserModelId;
                // 过滤掉抖音、模拟器、系统合成器等「不是内容来源」的会话，
                // 否则右键菜单里会列出一堆看不懂的项。
                if (string.IsNullOrEmpty(id)) continue;
                if (MediaSourceRouter.IsExcluded(id)) continue;
                // 白名单外的来源不进菜单：列出来也点不动（选了会被 RefreshMediaPropertiesAsync 清空），
                // 只会让用户以为「点了没反应」。
                if (applyWhitelist && !IsAppAllowed(id)) continue;
                if (list.Contains(id)) continue;
                list.Add(id);
            }
        }
        catch { }

        return list;
    }

    /// <summary>
    /// 自动选源。移植 WaterSprite 的两条规则：
    /// ① `source-router.cjs` 的 rank 排序（取代原来的「按 GSMTC 顺序取第一个」）；
    /// ② `media-state.cjs:17-21` 的「暂停不切源」（暂停 / 开新标签不该把胶囊跳走）。
    /// </summary>
    private async Task UpdateActiveSessionAsync()
    {
        if (_disposed || _sessionManager == null) return;

        GlobalSystemMediaTransportControlsSession? targetSession = null;
        try
        {
            var sessions = _sessionManager.GetSessions();

            // 记录每个来源第一次出现的时间，供「后启动优先」判据使用
            foreach (var s in sessions ?? new List<GlobalSystemMediaTransportControlsSession>())
            {
                string? id = s.SourceAppUserModelId;
                if (string.IsNullOrEmpty(id)) continue;
                if (!_firstSeen.ContainsKey(id)) _firstSeen[id] = _monotonic++;
            }

            // 本轮会话列表里还剩哪些 AppId（用于判断当前源是否消失）
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions ?? new List<GlobalSystemMediaTransportControlsSession>())
            {
                string? id = s.SourceAppUserModelId;
                if (!string.IsNullOrEmpty(id)) present.Add(id);
            }

            if (!string.IsNullOrEmpty(_currentAppId) && present.Contains(_currentAppId))
                _currentLastSeenUtc = DateTime.UtcNow;

            // 如果用户手动指定了固定来源，且该来源还在，强行跟随固定来源
            if (!string.IsNullOrEmpty(_pinnedAppId) && sessions != null)
            {
                foreach (var s in sessions)
                {
                    if (!string.Equals(s.SourceAppUserModelId, _pinnedAppId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 固定来源也必须过白名单。之前这里漏了检查，而 RefreshMediaPropertiesAsync
                    // 里有检查，结果是：点了一个白名单外的来源 → 先被采纳、随即被清空，
                    // 胶囊消失但菜单照样打勾，属于最难排查的那种不一致。
                    if (!IsAppAllowed(s.SourceAppUserModelId))
                    {
                        Log.Warn($"pinned source '{_pinnedAppId}' blocked by whitelist, falling back to auto");
                        SetPinnedAppId(null, persist: true);
                        break;
                    }

                    targetSession = s;
                    break;
                }

                // 如果被固定的来源已经关闭/不存在，自动退回自动选择。
                // 这里连盘上的偏好一起清掉：以前只清内存，重启后又被恢复，
                // 于是「关掉播放器后选了别的来源」这件事每次开机都会被推翻一次。
                if (targetSession == null)
                {
                    SetPinnedAppId(null, persist: true);
                }
            }

            // 自动模式：交给路由器打分，不再「取第一个能用的」
            if (targetSession == null)
            {
                targetSession = PickSessionByScore(sessions, present);
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
                _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                _currentAppId = _currentSession.SourceAppUserModelId ?? "";
                _currentLastSeenUtc = DateTime.UtcNow;
            }
            else
            {
                _currentAppId = "";
            }
        }

        await RefreshMediaPropertiesAsync();
    }

    /// <summary>
    /// 给候选会话打分挑一个。判据全部来自 WaterSprite `source-router.cjs`：
    /// rank（音乐 3 / 浏览器 2 / 直播 1 / 其他 0）→ 后启动优先 → 前台进程优先 → 当前源优先。
    /// 返回 null 表示「保持现状」。
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSessionByScore(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession>? sessions,
        HashSet<string> present)
    {
        if (sessions == null || sessions.Count == 0) return null;

        string? foregroundKey = MediaSourcePicker.TryGetForegroundAppKey();
        var candidates = new List<MediaSourcePicker.Candidate>(sessions.Count);
        var byId = new Dictionary<string, GlobalSystemMediaTransportControlsSession>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sessions)
        {
            string id = s.SourceAppUserModelId ?? "";

            // 过滤：抖音 / 模拟器 / 系统进程
            if (MediaSourceRouter.IsExcluded(id)) continue;

            // 用户白名单仍然生效（设置里填了 MusicCapsuleWhitelist 时）
            if (!IsAppAllowed(id)) continue;

            bool playing = false;
            try
            {
                var pb = s.GetPlaybackInfo();
                playing = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch { }

            var candidate = new MediaSourcePicker.Candidate
            {
                AppId = id,
                IsPlaying = playing,
                Rank = MediaSourceRouter.Rank(id),
                FirstSeenTicks = _firstSeen.TryGetValue(id, out long seen) ? seen : 0,
                IsForeground = foregroundKey != null
                    && id.Contains(foregroundKey, StringComparison.OrdinalIgnoreCase),
                IsCurrent = !string.IsNullOrEmpty(_currentAppId)
                    && string.Equals(id, _currentAppId, StringComparison.OrdinalIgnoreCase)
            };

            candidates.Add(candidate);
            if (!byId.ContainsKey(id)) byId[id] = s;
        }

        if (candidates.Count == 0) return null;

        var chosen = MediaSourcePicker.Choose(
            candidates,
            _currentAppId,
            _currentAppId != null && present.Contains(_currentAppId),
            DateTime.UtcNow,
            _currentLastSeenUtc);

        // 只在候选或结果发生变化时记一行，避免 3 秒一次的刷屏
        string summary = string.Join(", ",
            candidates.Select(c => $"{c.AppId}(r{c.Rank}{(c.IsPlaying ? ",playing" : "")}{(c.IsCurrent ? ",cur" : "")})"));
        if (summary != _lastPickLog)
        {
            _lastPickLog = summary;
            Log.Debug($"source pick: {candidates.Count} candidates [{summary}] -> " +
                      (chosen == null ? "keep current" : chosen.AppId));
        }

        if (chosen == null) return null; // 保持现状

        return byId.TryGetValue(chosen.AppId, out var result) ? result : null;
    }

    private void UnsubscribeCurrentSession()
    {
        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
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

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            var timeline = sender.GetTimelineProperties();
            UpdateTimeline(timeline);
            CheckLyricProgress();
        }
        catch { }
    }

    /// <summary>
    /// 该来源是否通过白名单。具体解析规则见 <see cref="MediaWhitelist"/>：
    /// 分隔符兼容全角逗号 / 顿号 / 空白，匹配时同时比对 appId 与友好名。
    /// </summary>
    private bool IsAppAllowed(string? appId)
        => MediaWhitelist.IsAllowed(appId, _settings.MusicCapsuleWhitelist);

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
            _currentTrackLyrics = null;
            CurrentLyric = null;
            MediaInfoChanged?.Invoke(null);
            LyricUpdated?.Invoke(null);
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
                _currentTrackLyrics = null;
                CurrentLyric = null;
                MediaInfoChanged?.Invoke(null);
                LyricUpdated?.Invoke(null);
                return;
            }

            var appId = session.SourceAppUserModelId ?? "";
            if (!IsAppAllowed(appId))
            {
                _lastTrackId = null;
                CurrentInfo = null;
                _currentTrackLyrics = null;
                CurrentLyric = null;
                MediaInfoChanged?.Invoke(null);
                LyricUpdated?.Invoke(null);
                return;
            }

            string title = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title.Trim();
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? "未知歌手" : props.Artist.Trim();

            // 更新时间轴属性
            try
            {
                var timeline = session.GetTimelineProperties();
                UpdateTimeline(timeline);
            }
            catch { }

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

            // 2. 异步检索/加载歌词与网络高质量封面补全
            if (!string.IsNullOrWhiteSpace(title) && title != "未知曲目")
            {
                // 按音源分流：QQ 音乐只读 QQ 缓存、网易云只走网易云接口，
                // 避免跨平台拿到别的版本的歌词（时间轴对不上）。
                var sourceKind = MediaSourceDetector.Detect(appId);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var lyrics = await LyricService.GetLyricsAsync(title, artist, _currentDuration, sourceKind);
                        _currentTrackLyrics = lyrics;
                        CheckLyricProgress();

                        // 如果之前 SMTC 没有封面，而歌词服务拿到了高清封面图
                        if (!isRealThumbnail && lyrics?.CoverBytes != null && lyrics.CoverBytes.Length > 0)
                        {
                            var netBitmap = LoadBitmapFromBytes(lyrics.CoverBytes);
                            if (netBitmap != null && CurrentInfo != null && CurrentInfo.Title == title)
                            {
                                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                                {
                                    CurrentInfo = new MediaTrackInfo
                                    {
                                        Title = CurrentInfo.Title,
                                        Artist = CurrentInfo.Artist,
                                        Album = CurrentInfo.Album,
                                        AppId = CurrentInfo.AppId,
                                        FriendlyAppName = CurrentInfo.FriendlyAppName,
                                        IsPlaying = CurrentInfo.IsPlaying,
                                        Thumbnail = netBitmap,
                                        CanPlayPause = CurrentInfo.CanPlayPause,
                                        CanSkipNext = CurrentInfo.CanSkipNext,
                                        CanSkipPrev = CurrentInfo.CanSkipPrev,
                                        Position = CurrentInfo.Position,
                                        Duration = CurrentInfo.Duration
                                    };
                                    MediaInfoChanged?.Invoke(CurrentInfo);
                                });
                            }
                        }
                    }
                    catch { }
                });
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
                FriendlyAppName = GetFriendlySourceName(appId),
                IsPlaying = isPlaying,
                Thumbnail = thumbnailImage,
                CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? true,
                CanSkipNext = controls?.IsNextEnabled ?? true,
                CanSkipPrev = controls?.IsPreviousEnabled ?? true,
                Position = _currentPosition,
                Duration = _currentDuration
            };

            Log.Debug($"media info: title='{info.Title}' artist='{info.Artist}' app='{info.FriendlyAppName}' playing={isPlaying} thumb={thumbnailImage != null}");
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
        try { _lyricTimer?.Stop(); _lyricTimer = null; } catch { }

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
