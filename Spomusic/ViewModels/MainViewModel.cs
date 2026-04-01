using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spomusic.Models;
using Spomusic.Services;
using System.Collections.ObjectModel;

namespace Spomusic.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IMusicService _musicService;
        private readonly MusicScannerService _scannerService;
        private readonly DatabaseService _databaseService;

        [ObservableProperty] private ObservableCollection<SongItem> _songs = new();
        [ObservableProperty] private ObservableCollection<SongItem> _filteredSongs = new();
        [ObservableProperty] private ObservableCollection<SongItem> _recommendedSongs = new();
        [ObservableProperty] private ObservableCollection<SongItem> _continueListeningSongs = new();
        [ObservableProperty] private SongItem? _currentSong;
        [ObservableProperty] private bool _isPlaying;
        [ObservableProperty] private double _currentPosition;
        [ObservableProperty] private double _duration;
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private string _searchScope = "Todo";
        [ObservableProperty] private bool _showOnlyFavorites;
        [ObservableProperty] private bool _isShuffle;
        [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.All;
        [ObservableProperty] private string? _currentLyricLine;
        [ObservableProperty] private int _currentLyricIndex = -1;
        [ObservableProperty] private double _currentLyricWordProgress;
        [ObservableProperty] private FormattedString? _currentLyricFormatted;
        [ObservableProperty] private ObservableCollection<LyricLine> _fullLyrics = new();
        [ObservableProperty] private bool _isLyricsFullScreen;
        [ObservableProperty] private bool _isScanning;
        [ObservableProperty] private bool _isDiscoverPanelOpen;
        [ObservableProperty] private bool _isQueuePanelOpen;
        [ObservableProperty] private bool _isFocusMode;
        [ObservableProperty] private int _lyricLeadMs = 650;
        [ObservableProperty] private bool _isReducedMotion;
        [ObservableProperty] private bool _isPartyMode;
        [ObservableProperty] private string _partySessionCode = string.Empty;
        [ObservableProperty] private bool _isTimingSyncMode;
        [ObservableProperty] private string _downloadBatchStatus = string.Empty;

        [ObservableProperty] private double _lyricsFontSize = 24;
        [ObservableProperty] private bool _isHighContrastLyrics;
        [ObservableProperty] private ObservableCollection<SongItem> _queueSongs = new();
        [ObservableProperty] private SongItem? _draggingQueueSong;
        [ObservableProperty] private string _lyricSyncQuality = "Baja";
        [ObservableProperty] private string _lyricSyncQualityColor = "#FF8A80";

        public bool IsNotFocusMode => !IsFocusMode;

        public event Action<int>? RequestScrollToLyric;

        public MainViewModel(IMusicService musicService, MusicScannerService scannerService, DatabaseService databaseService)
        {
            _musicService = musicService;
            _scannerService = scannerService;
            _databaseService = databaseService;
            IsReducedMotion = DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Version.Major <= 9;
            LyricLeadMs = _musicService.LyricLeadMs;

            _musicService.OnSongChanged += song =>
            {
                CurrentSong = song;
                Duration = song.Duration.TotalSeconds;
                FullLyrics = new ObservableCollection<LyricLine>(_musicService.CurrentLyrics);
                BuildWordProgressFormattedLyric();
                UpdateLyricSyncQuality();
                _ = LoadKaraokePresetForCurrentSongAsync();
                _ = LoadRecommendationsAsync();
                _ = LoadContinueListeningAsync();
            };

            _musicService.OnPlaybackStatusChanged += status => IsPlaying = status;
            _musicService.OnPositionChanged += pos =>
            {
                CurrentPosition = pos.TotalSeconds;
                CurrentLyricLine = _musicService.CurrentLyricLine;
                CurrentLyricIndex = _musicService.CurrentLyricIndex;
                CurrentLyricWordProgress = _musicService.CurrentLyricWordProgress;
                BuildWordProgressFormattedLyric();
                UpdateLyricSyncQuality();

                if (FullLyrics.Count == 0 && _musicService.CurrentLyrics.Count > 0)
                    FullLyrics = new ObservableCollection<LyricLine>(_musicService.CurrentLyrics);
            };

            _musicService.OnLyricIndexChanged += index => RequestScrollToLyric?.Invoke(index);
            _musicService.OnLyricProgressChanged += progress =>
            {
                CurrentLyricWordProgress = progress;
                BuildWordProgressFormattedLyric();
            };

            _musicService.OnQueueChanged += queue =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    QueueSongs = new ObservableCollection<SongItem>(queue);
                });
            };

            Task.Run(async () =>
            {
                await LoadSongsAsync();
                await LoadRecommendationsAsync();
                await LoadContinueListeningAsync();
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    QueueSongs = new ObservableCollection<SongItem>(_musicService.GetQueueSnapshot());
                });
            });
        }

        [RelayCommand]
        public async Task LoadSongsAsync()
        {
            if (IsScanning) return;
            IsScanning = true;
            try
            {
                var scannedSongs = await _scannerService.ScanSongsAsync();
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Songs = new ObservableCollection<SongItem>(scannedSongs);
                    ApplySearchFilter();
                    if (scannedSongs.Count > 0)
                    {
                        _musicService.SetPlaylist(scannedSongs.ToList());
                        QueueSongs = new ObservableCollection<SongItem>(_musicService.GetQueueSnapshot());
                    }
                });
                await LoadContinueListeningAsync();
            }
            finally
            {
                IsScanning = false;
            }
        }

        [RelayCommand]
        public async Task DownloadLyrics()
        {
            if (CurrentSong == null) return;
            await _musicService.SaveLyricsOfflineAsync(CurrentSong);
            await App.Current!.Windows[0].Page!.DisplayAlert("Éxito", "Letra descargada para modo offline.", "OK");
        }

        [RelayCommand]
        public void SetSearchScope(string scope)
        {
            SearchScope = scope;
            ApplySearchFilter();
        }

        [RelayCommand]
        public void ToggleFavoritesOnly()
        {
            ShowOnlyFavorites = !ShowOnlyFavorites;
            ApplySearchFilter();
        }

        [RelayCommand]
        public void ToggleDiscoverPanel() => IsDiscoverPanelOpen = !IsDiscoverPanelOpen;

        [RelayCommand]
        public void ToggleQueuePanel() => IsQueuePanelOpen = !IsQueuePanelOpen;

        [RelayCommand]
        public void ToggleFocusMode() => IsFocusMode = !IsFocusMode;

        partial void OnSearchTextChanged(string value) => ApplySearchFilter();
        partial void OnShowOnlyFavoritesChanged(bool value) => ApplySearchFilter();
        partial void OnSearchScopeChanged(string value) => ApplySearchFilter();

        [RelayCommand]
        public void IncreaseLyricsFontSize() => LyricsFontSize = Math.Min(52, LyricsFontSize + 2);

        [RelayCommand]
        public void DecreaseLyricsFontSize() => LyricsFontSize = Math.Max(20, LyricsFontSize - 2);

        [RelayCommand]
        public void ToggleHighContrastLyrics() => IsHighContrastLyrics = !IsHighContrastLyrics;

        [RelayCommand]
        public async Task ReportLyricTiming()
        {
            if (CurrentSong == null) return;
            await _musicService.RegisterTimingTapAsync();
            await App.Current!.Windows[0].Page!.DisplayAlert("Timing reportado", "Se guardó un ajuste local de sincronía para esta canción.", "OK");
        }

        [RelayCommand]
        public async Task DeleteDownloadedLyrics()
        {
            if (CurrentSong == null) return;
            await _musicService.DeleteDownloadedLyricsAsync(CurrentSong);
            FullLyrics = new ObservableCollection<LyricLine>(_musicService.CurrentLyrics);
            UpdateLyricSyncQuality();
            await App.Current!.Windows[0].Page!.DisplayAlert("Letras eliminadas", "Se borró la letra descargada de esta canción.", "OK");
        }

        [RelayCommand]
        public void TogglePerformanceProfile() => IsReducedMotion = !IsReducedMotion;

        [RelayCommand]
        public void TogglePartyMode() => IsPartyMode = !IsPartyMode;

        [RelayCommand]
        public void GeneratePartyCode()
        {
            PartySessionCode = $"SP-{Random.Shared.Next(100000, 999999)}";
        }

        [RelayCommand]
        public void ToggleTimingSyncMode() => IsTimingSyncMode = !IsTimingSyncMode;

        [RelayCommand]
        public async Task TapSync()
        {
            if (!IsTimingSyncMode) return;
            await _musicService.RegisterTimingTapAsync();
        }

        [RelayCommand]
        public async Task DownloadPlaylistLyricsOffline()
        {
            if (Songs.Count == 0) return;
            DownloadBatchStatus = "Descargando letras para modo viaje...";
            await _musicService.DownloadLyricsBatchAsync(Songs);
            DownloadBatchStatus = "Letras descargadas para modo viaje.";
            await App.Current!.Windows[0].Page!.DisplayAlert("Modo viaje", DownloadBatchStatus, "OK");
        }

        [RelayCommand]
        public void PlaySong(SongItem song) => _musicService.Play(song);

        [RelayCommand]
        public void StartQueueDrag(SongItem song) => DraggingQueueSong = song;

        [RelayCommand]
        public void DropQueueSong(SongItem targetSong)
        {
            if (DraggingQueueSong == null) return;
            var from = QueueSongs.IndexOf(DraggingQueueSong);
            var to = QueueSongs.IndexOf(targetSong);
            if (from < 0 || to < 0 || from == to) return;

            _musicService.MoveQueueItem(from, to);
            QueueSongs = new ObservableCollection<SongItem>(_musicService.GetQueueSnapshot());
            DraggingQueueSong = null;
        }

        [RelayCommand]
        public void TogglePlayPause()
        {
            if (_musicService.IsPlaying) _musicService.Pause();
            else _musicService.Resume();
        }

        [RelayCommand] public void Next() => _musicService.Next();
        [RelayCommand] public void Previous() => _musicService.Previous();

        [RelayCommand]
        public void ToggleShuffle()
        {
            IsShuffle = !IsShuffle;
            _musicService.IsShuffle = IsShuffle;
        }

        [RelayCommand]
        public void ToggleRepeat()
        {
            RepeatMode = RepeatMode == RepeatMode.All ? RepeatMode.One : (RepeatMode == RepeatMode.One ? RepeatMode.None : RepeatMode.All);
            _musicService.RepeatMode = RepeatMode;
        }

        [RelayCommand] public async Task ShareSong() => await _musicService.ShareCurrentSong();
        [RelayCommand] public void ToggleLyricsFullScreen() => IsLyricsFullScreen = !IsLyricsFullScreen;

        [RelayCommand]
        public async Task ToggleFavorite(SongItem song)
        {
            song.IsFavorite = !song.IsFavorite;
            await _databaseService.UpdateSongAsync(song);
            await _databaseService.RecordPlaybackEventAsync($"{song.Title}_{song.Artist}", "favorite");
            OnPropertyChanged(nameof(Songs));
            ApplySearchFilter();
            await LoadRecommendationsAsync();
        }

        public void Seek(double value) => _musicService.SeekTo(TimeSpan.FromSeconds(value));
        partial void OnLyricLeadMsChanged(int value)
        {
            _musicService.LyricLeadMs = value;
            _ = SaveCurrentKaraokePresetAsync();
        }

        partial void OnLyricsFontSizeChanged(double value) => _ = SaveCurrentKaraokePresetAsync();
        partial void OnIsHighContrastLyricsChanged(bool value) => _ = SaveCurrentKaraokePresetAsync();
        partial void OnIsFocusModeChanged(bool value) => OnPropertyChanged(nameof(IsNotFocusMode));

        private void ApplySearchFilter()
        {
            IEnumerable<SongItem> query = Songs;

            if (ShowOnlyFavorites)
                query = query.Where(s => s.IsFavorite);

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = query.Where(song => SearchScope switch
                {
                    "Artista" => Contains(song.Artist, SearchText),
                    "Álbum" => Contains(song.Album, SearchText),
                    "Género" => Contains(song.Genre, SearchText),
                    _ => Contains(song.Title, SearchText) || Contains(song.Artist, SearchText) || Contains(song.Album, SearchText) || Contains(song.Genre, SearchText)
                });
            }

            FilteredSongs = new ObservableCollection<SongItem>(query);
        }

        private static bool Contains(string value, string text)
            => value?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false;

        private void BuildWordProgressFormattedLyric()
        {
            var line = CurrentLyricLine;
            if (string.IsNullOrWhiteSpace(line))
            {
                CurrentLyricFormatted = null;
                return;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 1)
            {
                CurrentLyricFormatted = new FormattedString
                {
                    Spans =
                    {
                        new Span
                        {
                            Text = line,
                            FontAttributes = FontAttributes.Bold,
                            TextColor = IsHighContrastLyrics ? Colors.White : Color.FromArgb("#F4FFF8")
                        }
                    }
                };
                return;
            }

            var progressWordCount = Math.Clamp((int)Math.Ceiling(Math.Clamp(CurrentLyricWordProgress, 0, 1) * words.Length), 0, words.Length);
            var formatted = new FormattedString();

            for (int i = 0; i < words.Length; i++)
            {
                formatted.Spans.Add(new Span
                {
                    Text = i == words.Length - 1 ? words[i] : words[i] + " ",
                    FontAttributes = FontAttributes.Bold,
                    TextColor = i < progressWordCount
                        ? (IsHighContrastLyrics ? Color.FromArgb("#FFF165") : Color.FromArgb("#FFE85E"))
                        : (IsHighContrastLyrics ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#CFF1DD"))
                });
            }

            CurrentLyricFormatted = formatted;
        }

        private async Task LoadKaraokePresetForCurrentSongAsync()
        {
            if (CurrentSong == null) return;
            var preset = await _databaseService.GetKaraokePresetAsync($"{CurrentSong.Title}_{CurrentSong.Artist}");
            if (preset == null) return;

            LyricLeadMs = Math.Clamp(preset.LeadMs, 0, 1200);
            LyricsFontSize = Math.Clamp(preset.FontSize, 20, 52);
            IsHighContrastLyrics = preset.HighContrast;
        }

        private async Task SaveCurrentKaraokePresetAsync()
        {
            if (CurrentSong == null) return;

            await _databaseService.SaveKaraokePresetAsync(
                $"{CurrentSong.Title}_{CurrentSong.Artist}",
                LyricLeadMs,
                LyricsFontSize,
                IsHighContrastLyrics);
        }

        private void UpdateLyricSyncQuality()
        {
            if (_musicService.CurrentLyrics.Count == 0)
            {
                LyricSyncQuality = "Baja";
                LyricSyncQualityColor = "#FF8A80";
                return;
            }

            if (_musicService.CurrentLyrics.Count < 3)
            {
                LyricSyncQuality = "Media";
                LyricSyncQualityColor = "#FFD54F";
                return;
            }

            var deltas = new List<double>();
            for (var i = 1; i < _musicService.CurrentLyrics.Count; i++)
            {
                deltas.Add((_musicService.CurrentLyrics[i].Time - _musicService.CurrentLyrics[i - 1].Time).TotalSeconds);
            }

            var avg = deltas.Average();
            var variance = deltas.Average(v => Math.Abs(v - avg));
            if (variance < 0.12 && avg > 2.4 && avg < 3.4)
            {
                LyricSyncQuality = "Media";
                LyricSyncQualityColor = "#FFD54F";
                return;
            }

            LyricSyncQuality = "Alta";
            LyricSyncQualityColor = "#7CFF9D";
        }

        private async Task LoadRecommendationsAsync()
        {
            try
            {
                var scoreMap = await _databaseService.GetTopSongScoresAsync(days: 35, limit: 15);
                if (Songs.Count == 0)
                {
                    RecommendedSongs = new ObservableCollection<SongItem>();
                    return;
                }

                var ordered = Songs
                    .OrderByDescending(song => scoreMap.TryGetValue($"{song.Title}_{song.Artist}", out var score) ? score : 0)
                    .ThenByDescending(song => song.IsFavorite)
                    .Take(10)
                    .ToList();

                RecommendedSongs = new ObservableCollection<SongItem>(ordered);
            }
            catch
            {
                // Non-blocking UX feature.
            }
        }

        private async Task LoadContinueListeningAsync()
        {
            try
            {
                if (Songs.Count == 0)
                {
                    ContinueListeningSongs = new ObservableCollection<SongItem>();
                    return;
                }

                var stateByPath = await _databaseService.GetRecentResumeStatesAsync(limit: 12);
                var list = Songs
                    .Where(song => stateByPath.ContainsKey(song.Path))
                    .OrderByDescending(song => stateByPath[song.Path])
                    .Take(10)
                    .ToList();
                ContinueListeningSongs = new ObservableCollection<SongItem>(list);
            }
            catch
            {
                // Non-blocking UX feature.
            }
        }
    }
}
