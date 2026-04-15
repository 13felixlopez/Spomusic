using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Spomusic.Models;

#if ANDROID
using Android.Media;
using Android.Content;
using Microsoft.Maui.ApplicationModel;
#endif

namespace Spomusic.Services
{
    public class AndroidMusicService : IMusicService
    {
        private readonly DatabaseService _db;
        private readonly MusicScannerService _scanner;

#if ANDROID
        private MediaPlayer? _mediaPlayer;
        private List<SongItem> _playlist = new();
        private List<int> _shuffledIndices = new();
        private int _currentPlaylistIndex = -1;
        private System.Timers.Timer? _timer;
        private System.Timers.Timer? _retryTimer;
        private readonly SemaphoreSlim _playLock = new(1, 1);
        private readonly TimeSpan _crossfadeDuration = TimeSpan.FromMilliseconds(280);
        private int _lyricLeadMs = 650;
        private int _currentSongTimingOffsetMs = 0;
        private readonly List<int> _timingTapOffsets = new();
        private long _lastResumePersistTicks = 0;
        private long _lyricsRequestVersion = 0;

        public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;
        public SongItem? CurrentSong => _currentPlaylistIndex >= 0 && _currentPlaylistIndex < _playlist.Count ? (_isShuffle ? _playlist[_shuffledIndices[_currentPlaylistIndex]] : _playlist[_currentPlaylistIndex]) : null;

        private bool _isShuffle;
        public bool IsShuffle
        {
            get => _isShuffle;
            set
            {
                if (_isShuffle == value) return;
                _isShuffle = value;
                RegenerateIndices();
                NotifyQueueChanged();
            }
        }

        public RepeatMode RepeatMode { get; set; } = RepeatMode.All;
        public List<LyricLine> CurrentLyrics { get; private set; } = new();
        public string? CurrentLyricLine { get; private set; }
        public int CurrentLyricIndex { get; private set; } = -1;
        public double CurrentLyricWordProgress { get; private set; }
        public int LyricLeadMs
        {
            get => _lyricLeadMs;
            set => _lyricLeadMs = Math.Clamp(value, 0, 1200);
        }

        public TimeSpan CurrentPosition => TimeSpan.FromMilliseconds(_mediaPlayer?.CurrentPosition ?? 0);
        public TimeSpan Duration => TimeSpan.FromMilliseconds(_mediaPlayer?.Duration ?? 0);

        public event Action<SongItem>? OnSongChanged;
        public event Action<bool>? OnPlaybackStatusChanged;
        public event Action<TimeSpan>? OnPositionChanged;
        public event Action<int>? OnLyricIndexChanged;
        public event Action<double>? OnLyricProgressChanged;
        public event Action<IReadOnlyList<SongItem>>? OnQueueChanged;

        public AndroidMusicService(DatabaseService db, MusicScannerService scanner)
        {
            _db = db;
            _scanner = scanner;

            _timer = new System.Timers.Timer(220);
            _timer.Elapsed += (s, e) =>
            {
                var pos = CurrentPosition;
                UpdateCurrentLyric(pos);
                OnPositionChanged?.Invoke(pos);
                _ = PersistResumePositionAsync(pos);
            };

            _retryTimer = new System.Timers.Timer(90000);
            _retryTimer.Elapsed += async (s, e) => await ProcessLyricRetryQueueAsync();
            _retryTimer.Start();
        }

        private void AttachCompletion(MediaPlayer player)
        {
            player.Completion += async (s, e) => await HandleCompletionAsync();
        }

        private void RegenerateIndices()
        {
            var current = CurrentSong;
            _shuffledIndices = Enumerable.Range(0, _playlist.Count).ToList();
            if (_isShuffle)
            {
                var rng = new Random();
                _shuffledIndices = _shuffledIndices.OrderBy(_ => rng.Next()).ToList();
            }

            if (current != null)
                _currentPlaylistIndex = _shuffledIndices.IndexOf(_playlist.IndexOf(current));
        }

        public void SetPlaylist(List<SongItem> songs)
        {
            _playlist = songs;
            RegenerateIndices();
            NotifyQueueChanged();
        }

        public IReadOnlyList<SongItem> GetQueueSnapshot()
        {
            if (_playlist.Count == 0) return Array.Empty<SongItem>();
            if (!_isShuffle) return _playlist.ToList();
            return _shuffledIndices.Select(i => _playlist[i]).ToList();
        }

        public void MoveQueueItem(int fromIndex, int toIndex)
        {
            if (_playlist.Count == 0) return;
            if (fromIndex < 0 || toIndex < 0) return;
            if (fromIndex >= _playlist.Count || toIndex >= _playlist.Count) return;
            if (fromIndex == toIndex) return;

            if (_isShuffle)
            {
                var moving = _shuffledIndices[fromIndex];
                _shuffledIndices.RemoveAt(fromIndex);
                _shuffledIndices.Insert(toIndex, moving);

                if (_currentPlaylistIndex == fromIndex) _currentPlaylistIndex = toIndex;
                else if (fromIndex < _currentPlaylistIndex && toIndex >= _currentPlaylistIndex) _currentPlaylistIndex--;
                else if (fromIndex > _currentPlaylistIndex && toIndex <= _currentPlaylistIndex) _currentPlaylistIndex++;
            }
            else
            {
                var movingSong = _playlist[fromIndex];
                _playlist.RemoveAt(fromIndex);
                _playlist.Insert(toIndex, movingSong);

                if (_currentPlaylistIndex == fromIndex) _currentPlaylistIndex = toIndex;
                else if (fromIndex < _currentPlaylistIndex && toIndex >= _currentPlaylistIndex) _currentPlaylistIndex--;
                else if (fromIndex > _currentPlaylistIndex && toIndex <= _currentPlaylistIndex) _currentPlaylistIndex++;
            }

            NotifyQueueChanged();
        }

        public void Play(SongItem song)
        {
            int realIndex = _playlist.IndexOf(song);
            if (realIndex != -1) _currentPlaylistIndex = _shuffledIndices.IndexOf(realIndex);
            _ = PlayInternalAsync(song, allowCrossfade: true, isAutoTransition: false);
        }

        private async Task PlayInternalAsync(SongItem song, bool allowCrossfade, bool isAutoTransition)
        {
            await _playLock.WaitAsync();
            try
            {
                var nextPlayer = new MediaPlayer();
                nextPlayer.SetDataSource(song.Path);
                nextPlayer.Prepare();
                AttachCompletion(nextPlayer);

                var hadCurrentPlayer = _mediaPlayer != null;
                var oldPlayer = _mediaPlayer;

                if (song.AlbumArt == null)
                    song.AlbumArt = await _scanner.GetAlbumArtCachedAsync(song.Path);

                song.AccentColorHex = await _scanner.GetDominantColorCachedAsync(song.Path, song.AlbumArt);
                _currentSongTimingOffsetMs = await _db.GetLyricTimingOffsetAsync(BuildSongKey(song));
                _timingTapOffsets.Clear();

                CurrentLyricLine = "Buscando letra...";
                CurrentLyrics.Clear();
                CurrentLyricIndex = -1;
                CurrentLyricWordProgress = 0;
                var currentLyricsRequestVersion = Interlocked.Increment(ref _lyricsRequestVersion);

                if (allowCrossfade && hadCurrentPlayer && oldPlayer?.IsPlaying == true)
                {
                    nextPlayer.SetVolume(0f, 0f);
                    nextPlayer.Start();
                    _mediaPlayer = nextPlayer;
                    await CrossfadePlayersAsync(oldPlayer, nextPlayer);
                }
                else
                {
                    oldPlayer?.Stop();
                    oldPlayer?.Release();
                    _mediaPlayer = nextPlayer;
                    nextPlayer.Start();
                }

                var resumePositionSeconds = await _db.GetResumeStateAsync(song.Path);
                if (resumePositionSeconds > 3 && resumePositionSeconds < nextPlayer.Duration / 1000d - 5)
                {
                    nextPlayer.SeekTo((int)TimeSpan.FromSeconds(resumePositionSeconds).TotalMilliseconds);
                }

                _timer?.Start();
                OnSongChanged?.Invoke(song);
                OnPlaybackStatusChanged?.Invoke(true);
                OnPositionChanged?.Invoke(CurrentPosition);

                _ = FetchLyricsAsync(song, currentLyricsRequestVersion, applyToCurrentSong: true);
                _ = PrefetchNextSongAssetsAsync();
                _ = ProcessLyricRetryQueueAsync();
                _ = _db.RecordPlaybackEventAsync(BuildSongKey(song), isAutoTransition ? "next" : "play");
                UpdateNotification(song, true);
            }
            catch
            {
                Next();
            }
            finally
            {
                _playLock.Release();
            }
        }

        private async Task CrossfadePlayersAsync(MediaPlayer oldPlayer, MediaPlayer newPlayer)
        {
            const int steps = 9;
            var delay = TimeSpan.FromMilliseconds(_crossfadeDuration.TotalMilliseconds / steps);

            for (var i = 1; i <= steps; i++)
            {
                var t = i / (float)steps;
                newPlayer.SetVolume(t, t);
                oldPlayer.SetVolume(1f - t, 1f - t);
                await Task.Delay(delay);
            }

            try
            {
                oldPlayer.Stop();
            }
            catch { }

            oldPlayer.Release();
            newPlayer.SetVolume(1f, 1f);
        }

        private async Task HandleCompletionAsync()
        {
            if (RepeatMode == RepeatMode.One)
            {
                if (CurrentSong != null)
                {
                    _ = _db.RecordPlaybackEventAsync(BuildSongKey(CurrentSong), "repeat");
                    await PlayInternalAsync(CurrentSong, allowCrossfade: false, isAutoTransition: false);
                }
                return;
            }

            Next();
        }

        public void Pause()
        {
            _mediaPlayer?.Pause();
            _timer?.Stop();
            OnPlaybackStatusChanged?.Invoke(false);
            _ = PersistResumePositionAsync(CurrentPosition);
            if (CurrentSong != null) UpdateNotification(CurrentSong, false);
        }

        public void Resume()
        {
            _mediaPlayer?.Start();
            _timer?.Start();
            OnPlaybackStatusChanged?.Invoke(true);
            if (CurrentSong != null) UpdateNotification(CurrentSong, true);
        }

        public void Next()
        {
            if (_playlist.Count == 0) return;
            _currentPlaylistIndex++;
            if (_currentPlaylistIndex >= _playlist.Count)
            {
                if (RepeatMode == RepeatMode.All) _currentPlaylistIndex = 0;
                else
                {
                    Stop();
                    return;
                }
            }

            if (CurrentSong != null)
            {
                _ = _db.RecordPlaybackEventAsync(BuildSongKey(CurrentSong), "next");
                _ = PlayInternalAsync(CurrentSong, allowCrossfade: true, isAutoTransition: true);
            }
        }

        public void Previous()
        {
            if (_playlist.Count == 0) return;
            _currentPlaylistIndex--;
            if (_currentPlaylistIndex < 0)
            {
                if (RepeatMode == RepeatMode.All) _currentPlaylistIndex = _playlist.Count - 1;
                else _currentPlaylistIndex = 0;
            }

            if (CurrentSong != null)
            {
                _ = _db.RecordPlaybackEventAsync(BuildSongKey(CurrentSong), "previous");
                _ = PlayInternalAsync(CurrentSong, allowCrossfade: true, isAutoTransition: false);
            }
        }

        public void Stop()
        {
            _mediaPlayer?.Stop();
            _timer?.Stop();
            OnPlaybackStatusChanged?.Invoke(false);
            _ = PersistResumePositionAsync(CurrentPosition);
            if (CurrentSong != null) UpdateNotification(CurrentSong, false);
        }

        public void SeekTo(TimeSpan position) => _mediaPlayer?.SeekTo((int)position.TotalMilliseconds);

        public async Task ShareCurrentSong()
        {
            if (CurrentSong == null) return;
            await Share.RequestAsync(new ShareFileRequest { Title = "Compartir", File = new ShareFile(CurrentSong.Path) });
        }

        public async Task FetchLyricsAsync(SongItem song)
            => await FetchLyricsAsync(song, _lyricsRequestVersion, applyToCurrentSong: true);

        private async Task FetchLyricsAsync(SongItem song, long requestVersion, bool applyToCurrentSong)
        {
            string key = BuildSongKey(song);
            var cached = await _db.GetLyricsAsync(key, allowExpired: false);

            if (cached != null)
            {
                if (applyToCurrentSong) ApplyLyrics(song, requestVersion, cached.RawLrc);
                return;
            }

            var (lyrics, error) = await TryFetchLyricsFromApiAsync(song.Title, song.Artist);
            if (!string.IsNullOrWhiteSpace(lyrics))
            {
                await _db.SaveLyricsAsync(key, lyrics, isDownloaded: false, ttl: TimeSpan.FromDays(5));
                await _db.MarkLyricRetrySucceededAsync(key);
                if (applyToCurrentSong) ApplyLyrics(song, requestVersion, lyrics);
                return;
            }

            await _db.QueueLyricRetryAsync(song.Title, song.Artist, error ?? "Network failure");
            if (applyToCurrentSong)
                SetLyricsStatus(song, requestVersion, "Offline - reintentando letra en segundo plano.");
        }

        private async Task<(string? Lyrics, string? Error)> TryFetchLyricsFromApiAsync(string title, string artist)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
                var response = await client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                var synced = root.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
                var plain = root.TryGetProperty("plainLyrics", out var p) ? p.GetString() : null;
                return (!string.IsNullOrWhiteSpace(synced) ? synced : plain, null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        public async Task SaveLyricsOfflineAsync(SongItem song)
        {
            string key = BuildSongKey(song);
            var cached = await _db.GetLyricsAsync(key, allowExpired: true);
            if (cached != null && !string.IsNullOrEmpty(cached.RawLrc))
                await _db.SaveLyricsAsync(key, cached.RawLrc, true, ttl: TimeSpan.FromDays(3650));
        }

        public async Task DeleteDownloadedLyricsAsync(SongItem song)
        {
            var key = BuildSongKey(song);
            await _db.DeleteLyricsAsync(key);

            if (CurrentSong != null && string.Equals(CurrentSong.Path, song.Path, StringComparison.OrdinalIgnoreCase))
            {
                CurrentLyrics.Clear();
                CurrentLyricIndex = -1;
                CurrentLyricWordProgress = 0;
                CurrentLyricLine = "Letra eliminada. Se volverá a buscar en línea.";
                OnLyricProgressChanged?.Invoke(CurrentLyricWordProgress);
                OnPositionChanged?.Invoke(CurrentPosition);
                _ = FetchLyricsAsync(song, Interlocked.Increment(ref _lyricsRequestVersion), applyToCurrentSong: true);
            }
        }

        public async Task DownloadLyricsBatchAsync(IEnumerable<SongItem> songs)
        {
            foreach (var song in songs)
            {
                try
                {
                    var key = BuildSongKey(song);
                    var cached = await _db.GetLyricsAsync(key, allowExpired: true);
                    if (cached == null || string.IsNullOrWhiteSpace(cached.RawLrc))
                    {
                        var (lyrics, _) = await TryFetchLyricsFromApiAsync(song.Title, song.Artist);
                        if (!string.IsNullOrWhiteSpace(lyrics))
                            await _db.SaveLyricsAsync(key, lyrics, true, ttl: TimeSpan.FromDays(3650));
                    }
                    else
                    {
                        await _db.SaveLyricsAsync(key, cached.RawLrc, true, ttl: TimeSpan.FromDays(3650));
                    }
                }
                catch
                {
                    // Continue with next song.
                }
            }
        }

        public async Task RegisterTimingTapAsync()
        {
            if (CurrentSong == null || CurrentLyrics.Count == 0 || CurrentLyricIndex < 0 || CurrentLyricIndex >= CurrentLyrics.Count)
                return;

            var expected = CurrentLyrics[CurrentLyricIndex].Time.TotalMilliseconds;
            var actual = CurrentPosition.TotalMilliseconds;
            var delta = (int)Math.Round(actual - expected);
            _timingTapOffsets.Add(delta);

            if (_timingTapOffsets.Count > 5)
                _timingTapOffsets.RemoveAt(0);

            var avg = (int)Math.Round(_timingTapOffsets.Average());
            _currentSongTimingOffsetMs = avg;
            await _db.SetLyricTimingOffsetAsync(BuildSongKey(CurrentSong), avg);
        }

        private async Task ProcessLyricRetryQueueAsync()
        {
            try
            {
                var dueRetries = await _db.GetDueLyricRetriesAsync();
                foreach (var retry in dueRetries)
                {
                    var (lyrics, error) = await TryFetchLyricsFromApiAsync(retry.Title, retry.Artist);
                    if (!string.IsNullOrWhiteSpace(lyrics))
                    {
                        await _db.SaveLyricsAsync(retry.SongKey, lyrics, isDownloaded: false, ttl: TimeSpan.FromDays(5));
                        await _db.MarkLyricRetrySucceededAsync(retry.SongKey);

                        if (CurrentSong != null && string.Equals(retry.SongKey, BuildSongKey(CurrentSong), StringComparison.OrdinalIgnoreCase))
                            ApplyLyrics(CurrentSong, _lyricsRequestVersion, lyrics);
                        continue;
                    }

                    await _db.MarkLyricRetryFailedAsync(retry, error ?? "Retry failed");
                }
            }
            catch
            {
                // Silent by design: background queue shouldn't crash playback.
            }
        }

        private async Task PersistResumePositionAsync(TimeSpan pos)
        {
            var song = CurrentSong;
            if (song == null) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _lastResumePersistTicks < TimeSpan.FromSeconds(2).Ticks)
                return;

            _lastResumePersistTicks = nowTicks;
            await _db.SaveResumeStateAsync(song.Path, pos.TotalSeconds);
        }

        private async Task PrefetchNextSongAssetsAsync()
        {
            var nextSong = GetNextSongCandidate();
            if (nextSong == null) return;

            try
            {
                if (nextSong.AlbumArt == null)
                    nextSong.AlbumArt = await _scanner.GetAlbumArtCachedAsync(nextSong.Path);

                nextSong.AccentColorHex = await _scanner.GetDominantColorCachedAsync(nextSong.Path, nextSong.AlbumArt);

                var key = BuildSongKey(nextSong);
                var cached = await _db.GetLyricsAsync(key, allowExpired: false);
                if (cached == null)
                {
                    var (lyrics, error) = await TryFetchLyricsFromApiAsync(nextSong.Title, nextSong.Artist);
                    if (!string.IsNullOrWhiteSpace(lyrics))
                        await _db.SaveLyricsAsync(key, lyrics, false, ttl: TimeSpan.FromDays(5));
                    else
                        await _db.QueueLyricRetryAsync(nextSong.Title, nextSong.Artist, error ?? "Prefetch failed");
                }
            }
            catch
            {
                // Prefetch is best-effort.
            }
        }

        private SongItem? GetNextSongCandidate()
        {
            if (_playlist.Count == 0 || _currentPlaylistIndex < 0) return null;

            var nextIndex = _currentPlaylistIndex + 1;
            if (nextIndex >= _playlist.Count) nextIndex = RepeatMode == RepeatMode.All ? 0 : _playlist.Count - 1;
            if (nextIndex < 0 || nextIndex >= _playlist.Count) return null;

            var realIndex = _isShuffle ? _shuffledIndices[nextIndex] : nextIndex;
            if (realIndex < 0 || realIndex >= _playlist.Count) return null;
            return _playlist[realIndex];
        }

        private void ApplyLyrics(SongItem song, long requestVersion, string lyricsText)
        {
            if (!ShouldApplyLyricsResult(song, requestVersion))
                return;

            ParseLyrics(lyricsText);
            OnPositionChanged?.Invoke(CurrentPosition);
        }

        private bool ShouldApplyLyricsResult(SongItem song, long requestVersion)
        {
            if (requestVersion != Interlocked.Read(ref _lyricsRequestVersion))
                return false;

            var current = CurrentSong;
            return current != null && string.Equals(current.Path, song.Path, StringComparison.OrdinalIgnoreCase);
        }

        private void SetLyricsStatus(SongItem song, long requestVersion, string status)
        {
            if (!ShouldApplyLyricsResult(song, requestVersion))
                return;

            CurrentLyrics.Clear();
            CurrentLyricIndex = -1;
            CurrentLyricWordProgress = 0;
            CurrentLyricLine = status;
            OnLyricProgressChanged?.Invoke(CurrentLyricWordProgress);
            OnPositionChanged?.Invoke(CurrentPosition);
        }

        private void ParseLyrics(string lyricsText)
        {
            var lines = new List<LyricLine>();
            var regex = new Regex(@"\[(\d+):(\d+(?:\.\d+)?)\](.*)", RegexOptions.Compiled);
            int idx = 0;

            foreach (var line in lyricsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = regex.Match(line);
                if (!match.Success) continue;

                int min = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine
                {
                    Index = idx++,
                    Time = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec),
                    Text = match.Groups[3].Value.Trim()
                });
            }

            if (lines.Count == 0)
            {
                double offset = 0;
                foreach (var line in lyricsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var clean = line.Trim();
                    if (clean.Length == 0) continue;
                    lines.Add(new LyricLine
                    {
                        Index = idx++,
                        Time = TimeSpan.FromSeconds(offset),
                        Text = clean
                    });
                    offset += 2.9;
                }
            }

            CurrentLyrics = lines.OrderBy(l => l.Time).ToList();
            CurrentLyricIndex = -1;
            CurrentLyricWordProgress = 0;

            if (CurrentLyrics.Count == 0)
            {
                CurrentLyricLine = "Letra no disponible.";
                OnLyricProgressChanged?.Invoke(CurrentLyricWordProgress);
                return;
            }

            CurrentLyricLine = CurrentLyrics[0].Text;
            OnLyricIndexChanged?.Invoke(0);
            OnLyricProgressChanged?.Invoke(CurrentLyricWordProgress);
        }

        private void UpdateCurrentLyric(TimeSpan pos)
        {
            if (CurrentLyrics.Count == 0) return;

            var effectiveLeadMs = LyricLeadMs + _currentSongTimingOffsetMs;
            var lyricPos = pos + TimeSpan.FromMilliseconds(effectiveLeadMs);
            int index = CurrentLyrics.FindLastIndex(l => l.Time <= lyricPos);
            if (index < 0) return;

            if (index != CurrentLyricIndex)
            {
                CurrentLyricIndex = index;
                CurrentLyricLine = CurrentLyrics[index].Text;
                OnLyricIndexChanged?.Invoke(index);
            }

            var start = CurrentLyrics[index].Time;
            var end = index + 1 < CurrentLyrics.Count ? CurrentLyrics[index + 1].Time : start + TimeSpan.FromSeconds(3.2);
            var rangeMs = Math.Max(320, (end - start).TotalMilliseconds);
            var progress = Math.Clamp((lyricPos - start).TotalMilliseconds / rangeMs, 0, 1);

            if (Math.Abs(progress - CurrentLyricWordProgress) > 0.04)
            {
                CurrentLyricWordProgress = progress;
                OnLyricProgressChanged?.Invoke(progress);
            }
        }

        private void UpdateNotification(SongItem song, bool isPlaying)
        {
            var context = Platform.AppContext;
            var intent = new Intent(context, typeof(Spomusic.Platforms.Android.MusicForegroundService));
            intent.PutExtra("title", song.Title);
            intent.PutExtra("artist", song.Artist);
            intent.PutExtra("isPlaying", isPlaying);
            var art = song.AlbumArt ?? _scanner.GetAlbumArt(song.Path);
            if (art != null) intent.PutExtra("albumArt", art);
            context.StartForegroundService(intent);
            Spomusic.Platforms.Android.SpomusicAppWidget.UpdateNowPlaying(context, song.Title, song.Artist, isPlaying);
        }

        private void NotifyQueueChanged()
        {
            OnQueueChanged?.Invoke(GetQueueSnapshot());
        }

        private static string BuildSongKey(SongItem song) => $"{song.Title}_{song.Artist}";
#else
        public bool IsPlaying => false;
        public SongItem? CurrentSong => null;
        public bool IsShuffle { get; set; }
        public RepeatMode RepeatMode { get; set; }
        public List<LyricLine> CurrentLyrics => new();
        public string? CurrentLyricLine => null;
        public int CurrentLyricIndex => -1;
        public double CurrentLyricWordProgress => 0;
        public int LyricLeadMs { get; set; } = 650;
        public TimeSpan CurrentPosition => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public event Action<SongItem>? OnSongChanged;
        public event Action<bool>? OnPlaybackStatusChanged;
        public event Action<TimeSpan>? OnPositionChanged;
        public event Action<int>? OnLyricIndexChanged;
        public event Action<double>? OnLyricProgressChanged;
        public AndroidMusicService(DatabaseService db, MusicScannerService sc) { _db = db; _scanner = sc; }
        public void Play(SongItem song) { }
        public void Pause() { }
        public void Resume() { }
        public void Next() { }
        public void Previous() { }
        public void Stop() { }
        public void SeekTo(TimeSpan position) { }
        public void SetPlaylist(List<SongItem> songs) { }
        public Task ShareCurrentSong() => Task.CompletedTask;
        public Task FetchLyricsAsync(SongItem song) => Task.CompletedTask;
        public Task SaveLyricsOfflineAsync(SongItem song) => Task.CompletedTask;
        public Task DeleteDownloadedLyricsAsync(SongItem song) => Task.CompletedTask;
        public Task DownloadLyricsBatchAsync(IEnumerable<SongItem> songs) => Task.CompletedTask;
        public Task RegisterTimingTapAsync() => Task.CompletedTask;
        public void MoveQueueItem(int fromIndex, int toIndex) { }
        public IReadOnlyList<SongItem> GetQueueSnapshot() => Array.Empty<SongItem>();
        public event Action<IReadOnlyList<SongItem>>? OnQueueChanged;
#endif
    }
}
