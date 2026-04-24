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
        private CancellationTokenSource? _searchCts;
        private bool _hasInitialized;

        [ObservableProperty] private ObservableCollection<SongItem> _songs = new();
        [ObservableProperty] private ObservableCollection<SongItem> _filteredSongs = new();
        [ObservableProperty] private ObservableCollection<SongItem> _recommendedSongs = new();
        [ObservableProperty] private ObservableCollection<SongItem> _continueListeningSongs = new();
        [ObservableProperty] private ObservableCollection<ScanFolderItem> _scanFolders = new();
        [ObservableProperty] private ObservableCollection<Playlist> _playlists = new();
        [ObservableProperty] private ObservableCollection<SongItem> _activePlaylistSongs = new();
        [ObservableProperty] private SongItem? _currentSong;
        [ObservableProperty] private Playlist? _activePlaylist;
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
        [ObservableProperty] private bool _isFolderPanelOpen;
        [ObservableProperty] private bool _isPlaylistPanelOpen;
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
        [ObservableProperty] private int _currentLyricOffsetMs = 0;
        [ObservableProperty] private string _syncStatus = string.Empty;
        private bool _autoSyncAttempted = false;
        private List<int> _tapSamples = new();

        [ObservableProperty] private bool _isRecordingTaps;
        [ObservableProperty] private int _tapsRequired = 3;
        [ObservableProperty] private int _tapsCollected;
        [ObservableProperty] private int _provisionalOffsetMs;
        [ObservableProperty] private string _sleepTimerStatus = string.Empty;

        public bool IsNotFocusMode => !IsFocusMode;
        public bool HasSleepTimer => !string.IsNullOrWhiteSpace(SleepTimerStatus);
        public int TotalSongs => Songs.Count;
        public int FavoriteSongsCount => Songs.Count(s => s.IsFavorite);
        public int ArtistCount => Songs
            .Select(s => s.Artist)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        public string ActiveFilterLabel => ShowOnlyFavorites ? $"{SearchScope} + Favoritos" : SearchScope;
        public string LibrarySummary => IsScanning
            ? "Sincronizando tu biblioteca local."
            : TotalSongs == 0
                ? "Explora tu música descargada y construye una biblioteca propia."
                : $"{FilteredSongs.Count} de {TotalSongs} canciones visibles en {ActiveFilterLabel}.";
        public bool HasLibraryContent => TotalSongs > 0;
        public bool HasScanFolders => ScanFolders.Count > 0;
        public bool HasPlaylists => Playlists.Count > 0;
        public string PlaylistSummary => ActivePlaylist == null
            ? $"{Playlists.Count} listas creadas"
            : $"{ActivePlaylistSongs.Count} canciones en {ActivePlaylist.Name}";

        public event Action<int>? RequestScrollToLyric;

        public MainViewModel(IMusicService musicService, MusicScannerService scannerService, DatabaseService databaseService)
        {
            _musicService = musicService;
            _scannerService = scannerService;
            _databaseService = databaseService;
            IsReducedMotion = DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Version.Major <= 9;
            LyricLeadMs = _musicService.LyricLeadMs;
            IsShuffle = _musicService.IsShuffle;

            RepeatMode = _musicService.RepeatMode;

            _musicService.OnSongChanged += song =>
            {
                CurrentSong = song;
                if (song == null)
                {
                    CurrentPosition = 0;
                    Duration = 0;
                    CurrentLyricLine = null;
                    CurrentLyricIndex = -1;
                    CurrentLyricWordProgress = 0;
                    CurrentLyricFormatted = null;
                    FullLyrics = new ObservableCollection<LyricLine>();
                    return;
                }

                Duration = song.Duration.TotalSeconds;
                FullLyrics = new ObservableCollection<LyricLine>(_musicService.CurrentLyrics);
                BuildWordProgressFormattedLyric();
                UpdateLyricSyncQuality();
                // Reset auto-sync flag when song changes
                _autoSyncAttempted = false;
                // Intentamos auto-sync automáticamente para letras de calidad baja/media
                _ = TryAutoSyncIfNeededAsync();
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
                CurrentLyricOffsetMs = _musicService.CurrentLyricOffsetMs;

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

            _musicService.OnSleepTimerChanged += remaining =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SleepTimerStatus = remaining.HasValue
                        ? $"Dormir en {remaining.Value:mm\\:ss}"
                        : string.Empty;
                    OnPropertyChanged(nameof(HasSleepTimer));
                });
            };

        }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            if (_hasInitialized) return;
            _hasInitialized = true;

            var cachedSongs = await _databaseService.GetSongsAsync();
            var savedFolders = await _databaseService.GetScanFoldersAsync();
            var availableFolders = await _scannerService.GetAvailableMusicFoldersAsync();
            var playlists = await _databaseService.GetPlaylistsAsync();
            var folderOptions = _scannerService.BuildFolderOptions(availableFolders, savedFolders);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Songs = new ObservableCollection<SongItem>(cachedSongs);
                ScanFolders = new ObservableCollection<ScanFolderItem>(folderOptions);
                Playlists = new ObservableCollection<Playlist>(playlists);
                ApplySearchFilterCore();
                if (cachedSongs.Count > 0)
                {
                    _musicService.SetPlaylist(cachedSongs.ToList());
                    QueueSongs = new ObservableCollection<SongItem>(_musicService.GetQueueSnapshot());
                }
            });

            await LoadRecommendationsAsync();
            await LoadContinueListeningAsync();
            await LoadSongsAsync();
        }

        [RelayCommand]
        public async Task LoadSongsAsync()
        {
            if (IsScanning) return;
            IsScanning = true;
            try
            {
                var selectedFolders = ScanFolders.Where(x => x.IsSelected).Select(x => x.Path).ToList();
                var availableFolders = await _scannerService.GetAvailableMusicFoldersAsync();
                var scannedSongs = await _scannerService.ScanSongsAsync(selectedFolders);
                var folderOptions = _scannerService.BuildFolderOptions(availableFolders, ScanFolders);
                await _databaseService.ReplaceSongsAsync(scannedSongs);
                await _databaseService.UpsertScanFoldersAsync(folderOptions);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Songs = new ObservableCollection<SongItem>(scannedSongs);
                    ScanFolders = new ObservableCollection<ScanFolderItem>(folderOptions);
                    ApplySearchFilterCore();
                    if (scannedSongs.Count > 0)
                    {
                        _musicService.SetPlaylist(scannedSongs.ToList());
                        QueueSongs = new ObservableCollection<SongItem>(_musicService.GetQueueSnapshot());
                    }
                });

                await LoadPlaylistsAsync();
                await LoadContinueListeningAsync();
                await LoadRecommendationsAsync();
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
            ApplySearchFilterCore();
        }

        [RelayCommand]
        public void ToggleFavoritesOnly()
        {
            ShowOnlyFavorites = !ShowOnlyFavorites;
            ApplySearchFilterCore();
        }

        [RelayCommand]
        public void ToggleDiscoverPanel() => IsDiscoverPanelOpen = !IsDiscoverPanelOpen;

        [RelayCommand]
        public void ToggleQueuePanel() => IsQueuePanelOpen = !IsQueuePanelOpen;

        [RelayCommand]
        public void ToggleFolderPanel() => IsFolderPanelOpen = !IsFolderPanelOpen;

        [RelayCommand]
        public void TogglePlaylistPanel() => IsPlaylistPanelOpen = !IsPlaylistPanelOpen;

        [RelayCommand]
        public void ToggleFocusMode() => IsFocusMode = !IsFocusMode;

        partial void OnSongsChanged(ObservableCollection<SongItem> value) => NotifyLibraryInsightsChanged();
        partial void OnFilteredSongsChanged(ObservableCollection<SongItem> value) => NotifyLibraryInsightsChanged();
        partial void OnScanFoldersChanged(ObservableCollection<ScanFolderItem> value) => NotifyLibraryInsightsChanged();
        partial void OnPlaylistsChanged(ObservableCollection<Playlist> value) => NotifyLibraryInsightsChanged();
        partial void OnActivePlaylistChanged(Playlist? value) => NotifyLibraryInsightsChanged();
        partial void OnActivePlaylistSongsChanged(ObservableCollection<SongItem> value) => NotifyLibraryInsightsChanged();
        partial void OnIsScanningChanged(bool value) => NotifyLibraryInsightsChanged();
        partial void OnSearchTextChanged(string value)
        {
            DebounceFilter();
            NotifyLibraryInsightsChanged();
        }
        partial void OnShowOnlyFavoritesChanged(bool value)
        {
            ApplySearchFilterCore();
            NotifyLibraryInsightsChanged();
        }
        partial void OnSearchScopeChanged(string value)
        {
            ApplySearchFilterCore();
            NotifyLibraryInsightsChanged();
        }

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
            if (IsRecordingTaps)
            {
                // Si estamos en modo registro, acumular la muestra y no persistir de inmediato.
                await _musicService.RegisterTimingTapAsync();
                // Registrar la muestra localmente (el servicio ya guarda un promedio interno; aquí
                // mantenemos muestras para dar opción a confirmar/rechazar si el usuario lo desea)
                _tapSamples.Add(_musicService.CurrentLyricOffsetMs);
                TapsCollected = _tapSamples.Count;
                ProvisionalOffsetMs = _tapSamples.Count > 0 ? (int)Math.Round(_tapSamples.Average()) : 0;

                if (TapsCollected >= TapsRequired)
                {
                    // Auto-confirmar: aplicar promedio final y persistir
                    var final = (int)Math.Round(_tapSamples.Average());
                    await _musicService.SetLyricOffsetAsync(final);
                    ProvisionalOffsetMs = final;
                    IsRecordingTaps = false;
                    _tapSamples.Clear();
                    TapsCollected = 0;
                    SyncStatus = $"Offset guardado: {final} ms";
                    await Task.Delay(1200);
                    SyncStatus = string.Empty;
                }
                else
                {
                    SyncStatus = $"Tap registrado ({TapsCollected}/{TapsRequired}) - provisional {ProvisionalOffsetMs} ms";
                }
                return;
            }

            // Comportamiento original: un solo tap persistente inmediato
            await _musicService.RegisterTimingTapAsync();
            // Mostrar feedback no bloqueante en la UI en lugar de un modal
            SyncStatus = $"Tap registrado: {_musicService.CurrentLyricOffsetMs} ms";
            CurrentLyricOffsetMs = _musicService.CurrentLyricOffsetMs;
            UpdateLyricSyncQuality();
            await Task.Delay(900);
            SyncStatus = string.Empty;
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
        public void StartTapRecording()
        {
            if (CurrentSong == null) return;
            _tapSamples.Clear();
            TapsCollected = 0;
            ProvisionalOffsetMs = 0;
            IsRecordingTaps = true;
            SyncStatus = "Modo registro: toca 3 veces al iniciarse las líneas";
        }

        [RelayCommand]
        public async Task CancelTapRecording()
        {
            _tapSamples.Clear();
            TapsCollected = 0;
            ProvisionalOffsetMs = 0;
            IsRecordingTaps = false;
            SyncStatus = "Registro cancelado";
            await Task.Delay(900);
            SyncStatus = string.Empty;
        }

        [RelayCommand]
        public async Task TapSync()
        {
            if (!IsTimingSyncMode) return;
            // Intentamos primero sincronización automática y luego registramos un tap como respaldo
            try
            {
                await _musicService.AutoSyncLyricsAsync();
            }
            catch
            {
                // No bloquear la UX si falla
            }

            try
            {
                await _musicService.RegisterTimingTapAsync();
            }
            catch
            {
                // Ignorar errores de tap
            }
            // Actualizar la vista con el nuevo offset
            CurrentLyricOffsetMs = _musicService.CurrentLyricOffsetMs;
            UpdateLyricSyncQuality();
        }

        [RelayCommand]
        public async Task AutoSyncNow()
        {
            if (CurrentSong == null) return;
            SyncStatus = "Auto-sync...";
            try
            {
                await _musicService.AutoSyncLyricsAsync();
                CurrentLyricOffsetMs = _musicService.CurrentLyricOffsetMs;
                SyncStatus = $"Auto-sync aplicado ({CurrentLyricOffsetMs} ms)";
            }
            catch
            {
                SyncStatus = "Auto-sync fallido";
            }
            UpdateLyricSyncQuality();
            await Task.Delay(1200);
            SyncStatus = string.Empty;
        }

        [RelayCommand]
        public async Task AdjustOffset(int delta)
        {
            if (CurrentSong == null) return;
            var newOffset = CurrentLyricOffsetMs + delta;
            await _musicService.SetLyricOffsetAsync(newOffset);
            CurrentLyricOffsetMs = _musicService.CurrentLyricOffsetMs;
            SyncStatus = $"Offset: {CurrentLyricOffsetMs} ms";
            UpdateLyricSyncQuality();
        }

        [RelayCommand]
        public async Task ResetOffset()
        {
            if (CurrentSong == null) return;
            await _musicService.SetLyricOffsetAsync(0);
            CurrentLyricOffsetMs = _musicService.CurrentLyricOffsetMs;
            SyncStatus = "Offset reiniciado";
            UpdateLyricSyncQuality();
            await Task.Delay(900);
            SyncStatus = string.Empty;
        }

        [RelayCommand]
        public async Task ApplyOffset(int offsetMs)
        {
            if (CurrentSong == null) return;
            await _musicService.SetLyricOffsetAsync(offsetMs);
            CurrentLyricOffsetMs = _musicService.CurrentLyricOffsetMs;
            UpdateLyricSyncQuality();
            await App.Current!.Windows[0].Page!.DisplayAlert("Offset aplicado", $"Offset guardado: {offsetMs} ms", "OK");
        }

        private async Task TryAutoSyncIfNeededAsync()
        {
            if (CurrentSong == null) return;
            if (_autoSyncAttempted) return;
            if (LyricSyncQuality == "Alta") return;

            _autoSyncAttempted = true;
            SyncStatus = "Intentando sincronización automática...";
            try
            {
                await _musicService.AutoSyncLyricsAsync();
                CurrentLyricOffsetMs = _musicService.CurrentLyricOffsetMs;
                SyncStatus = "Sincronización automática aplicada";
            }
            catch
            {
                SyncStatus = "Sincronización automática fallida";
            }

            UpdateLyricSyncQuality();
            await Task.Delay(1200);
            SyncStatus = string.Empty;
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

        [RelayCommand]
        public void StopPlayback() => _musicService.Stop();

        [RelayCommand] public void Next() => _musicService.Next();
        [RelayCommand] public void Previous() => _musicService.Previous();

        [RelayCommand]
        public void ToggleShuffle()
        {
            IsShuffle = !IsShuffle;
            _musicService.IsShuffle = IsShuffle;
            QueueSongs = new ObservableCollection<SongItem>(_musicService.GetQueueSnapshot());
        }

        [RelayCommand]
        public void PlayRandomWithShuffle()
        {
            if (QueueSongs.Count == 0 && Songs.Count == 0 && ActivePlaylistSongs.Count == 0)
                return;

            IsShuffle = true;
            _musicService.IsShuffle = true;
            _musicService.PlayRandom();
            QueueSongs = new ObservableCollection<SongItem>(_musicService.GetQueueSnapshot());
        }

        [RelayCommand]
        public void ToggleRepeat()
        {
            RepeatMode = RepeatMode == RepeatMode.All ? RepeatMode.One : (RepeatMode == RepeatMode.One ? RepeatMode.None : RepeatMode.All);
            _musicService.RepeatMode = RepeatMode;
        }

        public void SetSleepTimer(int minutes)
        {
            if (minutes <= 0)
                return;

            _musicService.SetSleepTimer(TimeSpan.FromMinutes(minutes));
        }

        public void CancelSleepTimer()
        {
            _musicService.CancelSleepTimer();
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
            ApplySearchFilterCore();
            NotifyLibraryInsightsChanged();
            await LoadRecommendationsAsync();
        }

        [RelayCommand]
        public async Task SaveFolderSelectionAsync()
        {
            await _databaseService.SaveSelectedScanFoldersAsync(ScanFolders);
            IsFolderPanelOpen = false;
            await LoadSongsAsync();
        }

        [RelayCommand]
        public async Task LoadPlaylistsAsync()
        {
            var playlists = await _databaseService.GetPlaylistsAsync();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Playlists = new ObservableCollection<Playlist>(playlists);
            });
        }

        public async Task CreatePlaylistAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            await _databaseService.CreatePlaylistAsync(name);
            await LoadPlaylistsAsync();
        }

        public async Task AddSongToPlaylistAsync(SongItem song, Playlist playlist)
        {
            if (song.Id == 0 || playlist.Id == 0) return;
            await _databaseService.AddSongToPlaylistAsync(playlist.Id, song.Id);
            if (ActivePlaylist?.Id == playlist.Id)
                await OpenPlaylistAsync(playlist);
        }

        public async Task OpenPlaylistAsync(Playlist playlist)
        {
            var songs = await _databaseService.GetSongsForPlaylistAsync(playlist.Id);
            ActivePlaylist = playlist;
            ActivePlaylistSongs = new ObservableCollection<SongItem>(songs);
        }

        public void PlayPlaylist(Playlist playlist)
        {
            var songs = ActivePlaylistSongs;
            if (ActivePlaylist?.Id != playlist.Id || songs.Count == 0)
                return;

            _musicService.SetPlaylist(songs.ToList());
            QueueSongs = new ObservableCollection<SongItem>(_musicService.GetQueueSnapshot());
            _musicService.Play(songs[0]);
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

        private void DebounceFilter()
        {
            _searchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _searchCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(140, cts.Token);
                    if (cts.IsCancellationRequested) return;
                    ApplySearchFilterCore();
                }
                catch (TaskCanceledException)
                {
                }
            });
        }

        private void ApplySearchFilterCore()
        {
            var allSongs = Songs.ToList();
            var text = Normalize(SearchText);
            var scope = SearchScope;
            var onlyFavorites = ShowOnlyFavorites;

            _ = Task.Run(() =>
            {
                IEnumerable<SongItem> query = allSongs;

                if (onlyFavorites)
                    query = query.Where(s => s.IsFavorite);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    query = query.Where(song => scope switch
                    {
                        "Artista" => Normalize(song.Artist).Contains(text, StringComparison.Ordinal),
                        "Álbum" => Normalize(song.Album).Contains(text, StringComparison.Ordinal),
                        "Género" => Normalize(song.Genre).Contains(text, StringComparison.Ordinal),
                        _ => (song.SearchIndex.Length == 0 ? BuildSearchIndex(song) : song.SearchIndex).Contains(text, StringComparison.Ordinal)
                    });
                }

                var result = query.ToList();
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    FilteredSongs = new ObservableCollection<SongItem>(result);
                });
            });
        }

        private static bool Contains(string value, string text)
            => value?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false;

        private static string Normalize(string? value)
            => value?.Trim().ToUpperInvariant() ?? string.Empty;

        private static string BuildSearchIndex(SongItem song)
            => Normalize($"{song.Title} {song.Artist} {song.Album} {song.Genre} {song.FolderPath}");

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

        private void NotifyLibraryInsightsChanged()
        {
            OnPropertyChanged(nameof(TotalSongs));
            OnPropertyChanged(nameof(FavoriteSongsCount));
            OnPropertyChanged(nameof(ArtistCount));
            OnPropertyChanged(nameof(ActiveFilterLabel));
            OnPropertyChanged(nameof(LibrarySummary));
            OnPropertyChanged(nameof(HasLibraryContent));
            OnPropertyChanged(nameof(HasScanFolders));
            OnPropertyChanged(nameof(HasPlaylists));
            OnPropertyChanged(nameof(PlaylistSummary));
        }
    }
}
