using Spomusic.ViewModels;
using Spomusic.Models;
using System.ComponentModel;
using Microsoft.Maui.Devices;

namespace Spomusic
{
    public partial class MainPage : ContentPage
    {
        private MainViewModel ViewModel => (MainViewModel)BindingContext;
        private int _lastScrolledLyricIndex = -1;
        private CancellationTokenSource? _lyricsScrollCts;
        private CancellationTokenSource? _metadataMarqueeCts;
        private double _lastAdaptiveWidth = -1;
        private bool _isLibraryHeroCollapsed;
        private bool _isLibraryHeroAnimating;
        private double _libraryHeroExpandedHeight = -1;
        private double _libraryHeroCollapsedHeight = 118;
        private const string MarqueeSeparator = "     •     ";

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
            
            // Logica de auto-scroll para letras
            ViewModel.RequestScrollToLyric += (index) => {
                MainThread.BeginInvokeOnMainThread(() => {
                    if (LyricsCollectionView.ItemsSource != null && index >= 0)
                    {
                        _ = ScrollLyricsSmoothAsync(index);
                    }
                });
            };

            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Mantiene estable el reproductor principal en distintos anchos
            // recalculando las superficies mas grandes desde el viewport real.
            SizeChanged += OnPageSizeChanged;
        }

        // Mostrar un pequeño menú modal (ActionSheet) con opciones de sincronización manual
        private async void OnSyncMenuClicked(object sender, EventArgs e)
        {
            if (ViewModel.CurrentSong == null) return;

            string[] choices;
            if (ViewModel.IsRecordingTaps)
                choices = new[] { "Cancelar registro" };
            else
                choices = new[] { "Tap (reportar ahora)", "Auto-sync ahora", "Modo registro (3 taps)" };

            var option = await DisplayActionSheet("Sincronización manual", "Cancelar", null, choices);
            if (option == "Tap (reportar ahora)")
            {
                // Registrar tap manual
                await ViewModel.ReportLyricTimingCommand.ExecuteAsync(null);
            }
            else if (option == "Auto-sync ahora")
            {
                await ViewModel.AutoSyncNowCommand.ExecuteAsync(null);
            }
            else if (option == "Modo registro (3 taps)")
            {
                ViewModel.StartTapRecordingCommand.Execute(null);
            }
            else if (option == "Cancelar registro")
            {
                ViewModel.CancelTapRecordingCommand.Execute(null);
            }
        }

        private async void OnMiniPlayerTapped(object sender, TappedEventArgs e)
        {
            await OpenFullPlayerAsync();
        }

        private async void OnCloseFullPlayerClicked(object sender, EventArgs e)
        {
            await CloseFullPlayerAsync();
        }

        private void OnSliderDragCompleted(object sender, EventArgs e)
        {
            var slider = (Slider)sender;
            ViewModel.Seek(slider.Value);
        }

        private void OnQueueItemDragStarting(object sender, DragStartingEventArgs e)
        {
            if (sender is BindableObject bindable && bindable.BindingContext is SongItem song)
            {
                ViewModel.StartQueueDragCommand.Execute(song);
                e.Data.Properties["SongPath"] = song.Path;
            }
        }

        private void OnQueueItemDrop(object sender, DropEventArgs e)
        {
            if (sender is not BindableObject bindable || bindable.BindingContext is not SongItem targetSong)
                return;

            if (e.Data.Properties.TryGetValue("SongPath", out var value) && value is string songPath)
            {
                var draggedSong = ViewModel.QueueSongs.FirstOrDefault(s => string.Equals(s.Path, songPath, StringComparison.OrdinalIgnoreCase));
                if (draggedSong != null)
                    ViewModel.StartQueueDragCommand.Execute(draggedSong);
            }

            ViewModel.DropQueueSongCommand.Execute(targetSong);
        }

        private async Task ScrollLyricsSmoothAsync(int index)
        {
            try
            {
                if (!ViewModel.IsLyricsFullScreen) return;
                if (index == _lastScrolledLyricIndex) return;

                _lyricsScrollCts?.Cancel();
                _lyricsScrollCts = new CancellationTokenSource();
                var token = _lyricsScrollCts.Token;

                await Task.Delay(ViewModel.IsReducedMotion ? 30 : 90, token);
                if (token.IsCancellationRequested) return;

                // Mantener la línea actual centrada verticalmente en pantalla completa
                LyricsCollectionView.ScrollTo(index, position: ScrollToPosition.Center, animate: !ViewModel.IsReducedMotion);
                _lastScrolledLyricIndex = index;
            }
            catch (TaskCanceledException)
            {
                // Expected during rapid updates.
            }
            catch
            {
                // Keep lyrics UI resilient.
            }
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            ApplyResponsiveLayout(Width);
            ApplyLibraryHeroVisualStateImmediate();
            await CheckAndRequestPermissions();
        }

        // Manejo del botón "Atrás" (hardware / navigation back).
        // Este override intercepta la tecla atrás y cierra overlays/paneles
        // (letras en fullscreen, reproductor completo, paneles laterales) en
        // lugar de permitir que la aplicación se cierre inmediatamente.
        // Devuelve true cuando el evento es consumido.
        protected override bool OnBackButtonPressed()
        {
            // 1) Si las letras están en fullscreen, deben cerrarse primero y
            // dejar visible el reproductor full que está debajo. Esto respeta
            // la jerarquía esperada por el usuario: cerrar la capa superior.
            if (ViewModel.IsLyricsFullScreen)
            {
                ViewModel.IsLyricsFullScreen = false;
                return true;
            }

            // 2) Si el reproductor a pantalla completa está abierto, ciérralo.
            // CloseFullPlayerAsync es async, por eso lo invocamos en el hilo principal
            // sin bloquear este método síncrono.
            if (FullPlayerOverlay.IsVisible)
            {
                MainThread.BeginInvokeOnMainThread(async () => await CloseFullPlayerAsync());
                return true; // Evento consumido: no se cierra la app.
            }

            // 3) Paneles laterales u overlays (orden según jerarquía visual).
            if (ViewModel.IsQueuePanelOpen)
            {
                ViewModel.IsQueuePanelOpen = false;
                return true;
            }

            if (ViewModel.IsPlaylistPanelOpen)
            {
                ViewModel.IsPlaylistPanelOpen = false;
                return true;
            }

            if (ViewModel.IsFolderPanelOpen)
            {
                ViewModel.IsFolderPanelOpen = false;
                return true;
            }

            if (ViewModel.IsDiscoverPanelOpen)
            {
                ViewModel.IsDiscoverPanelOpen = false;
                return true;
            }

            // 4) Ningún overlay/panel abierto: delegar al comportamiento por defecto.
            return base.OnBackButtonPressed();
        }

        private void OnPageSizeChanged(object? sender, EventArgs e)
        {
            ApplyResponsiveLayout(Width);
        }

        private void ApplyResponsiveLayout(double pageWidth)
        {
            if (pageWidth <= 0 || Math.Abs(pageWidth - _lastAdaptiveWidth) < 1)
                return;

            _lastAdaptiveWidth = pageWidth;

            // Estos limites preservan la portada dominante en telefonos angostos
            // sin estirar de mas las superficies fijas en portrait.
            var contentPadding = pageWidth < 380 ? 18 : 28;
            var artworkSize = Math.Clamp(pageWidth - (contentPadding * 2), 236, 360);
            var sidePanelWidth = Math.Clamp(pageWidth * 0.86, 280, 380);
            var compactPhone = pageWidth < 360;

            FullPlayerArtwork.WidthRequest = artworkSize;
            FullPlayerArtwork.HeightRequest = artworkSize;
            FullPlayerArtwork.Margin = new Thickness(contentPadding, 28, contentPadding, 0);

            FullPlayerMetadataRow.Margin = new Thickness(contentPadding, 18, Math.Max(18, contentPadding - 6), 0);

            FullPlayerTitleLabel.FontSize = compactPhone ? 22 : 26;
            FullPlayerArtistLabel.FontSize = compactPhone ? 16 : 18;

            DiscoverPanel.WidthRequest = sidePanelWidth;
            QueuePanel.WidthRequest = sidePanelWidth;
            _libraryHeroCollapsedHeight = compactPhone ? 104 : 118;

            if (!_isLibraryHeroCollapsed && HomeLibraryHeroCard.Height > 0)
            {
                _libraryHeroExpandedHeight = HomeLibraryHeroCard.Height;
                HomeLibraryHeroCard.HeightRequest = -1;
            }
        }

        private async void OnToggleLibraryHeroClicked(object sender, TappedEventArgs e)
        {
            if (_isLibraryHeroAnimating)
                return;

            _isLibraryHeroCollapsed = !_isLibraryHeroCollapsed;
            await ApplyLibraryHeroVisualStateAsync(animated: true);
        }

        private void ApplyLibraryHeroVisualStateImmediate()
        {
            if (HomeLibraryHeroCard.Height > 0 && !_isLibraryHeroCollapsed)
                _libraryHeroExpandedHeight = HomeLibraryHeroCard.Height;

            var expandedHeight = _libraryHeroExpandedHeight > 0
                ? _libraryHeroExpandedHeight
                : (_libraryHeroCollapsedHeight + 96);

            var targetHeight = _isLibraryHeroCollapsed ? _libraryHeroCollapsedHeight : expandedHeight;
            var targetPadding = _isLibraryHeroCollapsed ? new Thickness(18, 12) : new Thickness(18);
            var targetSpacing = _isLibraryHeroCollapsed ? 8d : 14d;
            var targetHeaderSpacing = _isLibraryHeroCollapsed ? 1d : 4d;
            var secondaryOpacity = _isLibraryHeroCollapsed ? 0d : 1d;

            HomeLibraryHeroCard.HeightRequest = _isLibraryHeroCollapsed ? targetHeight : -1;
            HomeLibraryHeroCard.Padding = targetPadding;
            HomeLibraryHeroContent.Spacing = targetSpacing;
            HomeLibraryHeroHeader.Spacing = targetHeaderSpacing;
            HomeLibraryHeroSummary.Opacity = secondaryOpacity;
            HomeLibraryHeroMetrics.Opacity = secondaryOpacity;
            HomeLibraryHeroActionRow.Opacity = secondaryOpacity;
            HomeLibraryHeroSummary.IsVisible = !_isLibraryHeroCollapsed;
            HomeLibraryHeroMetrics.IsVisible = !_isLibraryHeroCollapsed;
            HomeLibraryHeroActionRow.IsVisible = !_isLibraryHeroCollapsed;
            HomeLibraryHeroToggleIcon.Rotation = _isLibraryHeroCollapsed ? 0 : 180;
        }

        private async Task ApplyLibraryHeroVisualStateAsync(bool animated)
        {
            if (_isLibraryHeroAnimating)
                return;

            if (HomeLibraryHeroCard.Height > 0 && !_isLibraryHeroCollapsed)
                _libraryHeroExpandedHeight = HomeLibraryHeroCard.Height;

            var expandedHeight = _libraryHeroExpandedHeight > 0
                ? _libraryHeroExpandedHeight
                : (_libraryHeroCollapsedHeight + 96);

            var targetHeight = _isLibraryHeroCollapsed ? _libraryHeroCollapsedHeight : expandedHeight;
            var targetPadding = _isLibraryHeroCollapsed ? new Thickness(18, 12) : new Thickness(18);
            var targetSpacing = _isLibraryHeroCollapsed ? 8d : 14d;
            var targetHeaderSpacing = _isLibraryHeroCollapsed ? 1d : 4d;
            var secondaryOpacity = _isLibraryHeroCollapsed ? 0d : 1d;

            if (!animated)
            {
                ApplyLibraryHeroVisualStateImmediate();
                return;
            }

            _isLibraryHeroAnimating = true;
            try
            {
                if (!_isLibraryHeroCollapsed)
                {
                    HomeLibraryHeroSummary.IsVisible = true;
                    HomeLibraryHeroMetrics.IsVisible = true;
                    HomeLibraryHeroActionRow.IsVisible = true;
                }

                await Task.WhenAll(
                    AnimateDoubleAsync(
                        start: HomeLibraryHeroCard.HeightRequest > 0 ? HomeLibraryHeroCard.HeightRequest : expandedHeight,
                        end: targetHeight,
                        setter: value => HomeLibraryHeroCard.HeightRequest = value,
                        duration: 260,
                        easing: Easing.CubicOut),
                    AnimateThicknessAsync(
                        start: HomeLibraryHeroCard.Padding,
                        end: targetPadding,
                        setter: value => HomeLibraryHeroCard.Padding = value,
                        duration: 260,
                        easing: Easing.CubicOut),
                    AnimateDoubleAsync(
                        start: HomeLibraryHeroContent.Spacing,
                        end: targetSpacing,
                        setter: value => HomeLibraryHeroContent.Spacing = value,
                        duration: 260,
                        easing: Easing.CubicOut),
                    AnimateDoubleAsync(
                        start: HomeLibraryHeroHeader.Spacing,
                        end: targetHeaderSpacing,
                        setter: value => HomeLibraryHeroHeader.Spacing = value,
                        duration: 260,
                        easing: Easing.CubicOut),
                    HomeLibraryHeroSummary.FadeTo(secondaryOpacity, 220, Easing.CubicInOut),
                    HomeLibraryHeroMetrics.FadeTo(secondaryOpacity, 220, Easing.CubicInOut),
                    HomeLibraryHeroActionRow.FadeTo(secondaryOpacity, 220, Easing.CubicInOut));

                if (_isLibraryHeroCollapsed)
                {
                    HomeLibraryHeroSummary.IsVisible = false;
                    HomeLibraryHeroMetrics.IsVisible = false;
                    HomeLibraryHeroActionRow.IsVisible = false;
                }
                else
                {
                    // Cuando termina de expandirse, se libera la altura fija
                    // para que la tarjeta recupere su tamano natural.
                    HomeLibraryHeroCard.HeightRequest = -1;
                }

                HomeLibraryHeroToggleIcon.Rotation = _isLibraryHeroCollapsed ? 0 : 180;
            }
            finally
            {
                _isLibraryHeroAnimating = false;
            }
        }

        private Task AnimateDoubleAsync(double start, double end, Action<double> setter, uint duration = 180, Easing? easing = null)
        {
            var tcs = new TaskCompletionSource();
            var animation = new Animation(value => setter(value), start, end, easing ?? Easing.CubicInOut);
            animation.Commit(
                owner: this,
                name: $"double-{Guid.NewGuid():N}",
                rate: 16,
                length: duration,
                finished: (_, _) => tcs.TrySetResult());
            return tcs.Task;
        }

        private Task AnimateThicknessAsync(Thickness start, Thickness end, Action<Thickness> setter, uint duration = 180, Easing? easing = null)
        {
            var tcs = new TaskCompletionSource();
            var animation = new Animation(progress =>
            {
                setter(new Thickness(
                    Lerp(start.Left, end.Left, progress),
                    Lerp(start.Top, end.Top, progress),
                    Lerp(start.Right, end.Right, progress),
                    Lerp(start.Bottom, end.Bottom, progress)));
            }, 0, 1, easing ?? Easing.CubicInOut);

            animation.Commit(
                owner: this,
                name: $"thickness-{Guid.NewGuid():N}",
                rate: 16,
                length: duration,
                finished: (_, _) => tcs.TrySetResult());
            return tcs.Task;
        }

        private static double Lerp(double start, double end, double progress)
            => start + ((end - start) * progress);

        private async Task CheckAndRequestPermissions()
        {
            PermissionStatus status = PermissionStatus.Unknown;

            if (DeviceInfo.Platform == DevicePlatform.Android)
            {
                if (DeviceInfo.Version.Major >= 13)
                {
                    status = await Permissions.CheckStatusAsync<Permissions.Media>();
                    if (status != PermissionStatus.Granted)
                    {
                        status = await Permissions.RequestAsync<Permissions.Media>();
                    }
                }
                else
                {
                    status = await Permissions.CheckStatusAsync<Permissions.StorageRead>();
                    if (status != PermissionStatus.Granted)
                    {
                        status = await Permissions.RequestAsync<Permissions.StorageRead>();
                    }
                }
            }

            if (status == PermissionStatus.Granted || DeviceInfo.Platform != DevicePlatform.Android)
            {
                await ViewModel.InitializeAsync();
            }
            else if (status == PermissionStatus.Denied)
            {
                await DisplayAlert("Permiso Denegado", "Para mostrar tu música, Spomusic necesita acceso a tus archivos de audio. Por favor, concédelo en la configuración de la aplicación.", "OK");
            }
        }

        private async void OnCreatePlaylistClicked(object sender, EventArgs e)
        {
            var name = await DisplayPromptAsync("Nueva playlist", "Nombre de la lista:", "Crear", "Cancelar", maxLength: 40);
            if (string.IsNullOrWhiteSpace(name)) return;

            await ViewModel.CreatePlaylistAsync(name);
        }

        private async void OnSongPlaylistClicked(object sender, TappedEventArgs e)
        {
            if (sender is not BindableObject bindable || bindable.BindingContext is not SongItem song)
                return;

            if (ViewModel.Playlists.Count == 0)
            {
                var createNow = await DisplayAlert("Sin playlists", "Primero crea una playlist para guardar canciones.", "Crear", "Cancelar");
                if (!createNow) return;

                var playlistName = await DisplayPromptAsync("Nueva playlist", "Nombre de la lista:", "Crear", "Cancelar", maxLength: 40);
                if (string.IsNullOrWhiteSpace(playlistName)) return;

                await ViewModel.CreatePlaylistAsync(playlistName);
            }

            var options = ViewModel.Playlists.Select(x => x.Name).ToArray();
            var selectedName = await DisplayActionSheet($"Agregar \"{song.Title}\"", "Cancelar", null, options);
            if (string.IsNullOrWhiteSpace(selectedName) || selectedName == "Cancelar") return;

            var playlist = ViewModel.Playlists.FirstOrDefault(x => x.Name == selectedName);
            if (playlist == null) return;

            await ViewModel.AddSongToPlaylistAsync(song, playlist);
            await DisplayAlert("Playlist actualizada", $"La canción se agregó a \"{playlist.Name}\".", "OK");
        }

        private async void OnPlaylistSelected(object sender, TappedEventArgs e)
        {
            if (sender is not BindableObject bindable || bindable.BindingContext is not Playlist playlist)
                return;

            await ViewModel.OpenPlaylistAsync(playlist);
        }

        private void OnPlayActivePlaylistClicked(object sender, EventArgs e)
        {
            if (ViewModel.ActivePlaylist == null) return;
            ViewModel.PlayPlaylist(ViewModel.ActivePlaylist);
        }

        private void OnFocusSearchClicked(object sender, EventArgs e)
        {
            LibrarySearchBar.Focus();
        }

        private async void OnStartRandomClicked(object sender, TappedEventArgs e)
        {
            ViewModel.PlayRandomWithShuffleCommand.Execute(null);
            await OpenFullPlayerAsync();
        }

        private void OnCloseAppClicked(object sender, TappedEventArgs e)
        {
            ViewModel.StopPlayback();
#if ANDROID
            Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.FinishAffinity();
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#else
            var window = Application.Current?.Windows.FirstOrDefault();
            if (window != null)
                Application.Current!.CloseWindow(window);
#endif
        }

        private async void OnSleepTimerClicked(object sender, TappedEventArgs e)
        {
            var option = await DisplayActionSheet(
                "Dormir en",
                "Cancelar",
                null,
                "15 minutos",
                "30 minutos",
                "60 minutos",
                "Personalizado",
                "Desactivar");

            switch (option)
            {
                case "15 minutos":
                    ViewModel.SetSleepTimer(15);
                    break;
                case "30 minutos":
                    ViewModel.SetSleepTimer(30);
                    break;
                case "60 minutos":
                    ViewModel.SetSleepTimer(60);
                    break;
                case "Desactivar":
                    ViewModel.CancelSleepTimer();
                    break;
                case "Personalizado":
                    var input = await DisplayPromptAsync("Dormir en", "Minutos antes de detener la reproducción:", "Programar", "Cancelar", keyboard: Keyboard.Numeric);
                    if (int.TryParse(input, out var minutes) && minutes > 0)
                        ViewModel.SetSleepTimer(minutes);
                    break;
            }
        }

        private void OnQuickSleep15Clicked(object sender, TappedEventArgs e) => ViewModel.SetSleepTimer(15);
        private void OnQuickSleep30Clicked(object sender, TappedEventArgs e) => ViewModel.SetSleepTimer(30);
        private void OnQuickSleep60Clicked(object sender, TappedEventArgs e) => ViewModel.SetSleepTimer(60);
        private void OnCancelSleepTimerClicked(object sender, TappedEventArgs e) => ViewModel.CancelSleepTimer();

        /// <summary>
        /// Mantiene el overlay sincronizado con la sesión real de reproducción para evitar pantallas superpuestas vacías.
        /// </summary>
        private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Mantener la pantalla encendida cuando las letras están en fullscreen
            if (e.PropertyName == nameof(MainViewModel.IsLyricsFullScreen))
            {
                try
                {
                    DeviceDisplay.KeepScreenOn = ViewModel.IsLyricsFullScreen;
                }
                catch
                {
                    // ignorar si la plataforma no lo soporta
                }
            }

            if (e.PropertyName != nameof(MainViewModel.CurrentSong))
                return;

            if (ViewModel.CurrentSong == null)
            {
                await CloseFullPlayerAsync(immediate: true);
                return;
            }

            SyncMetadataLabels();
            RestartMetadataMarquee();
        }

        private async Task OpenFullPlayerAsync()
        {
            if (ViewModel.CurrentSong == null)
                return;

            FullPlayerOverlay.IsVisible = true;
            if (ViewModel.IsReducedMotion)
            {
                FullPlayerOverlay.TranslationY = 0;
            }
            else
            {
                await FullPlayerOverlay.TranslateTo(0, 0, 420, Easing.CubicOut);
            }

            SyncMetadataLabels();
            RestartMetadataMarquee();
        }

        private async Task CloseFullPlayerAsync(bool immediate = false)
        {
            _metadataMarqueeCts?.Cancel();

            if (!FullPlayerOverlay.IsVisible)
            {
                FullPlayerOverlay.TranslationY = 1000;
                return;
            }

            if (immediate || ViewModel.IsReducedMotion)
            {
                FullPlayerOverlay.TranslationY = 1000;
            }
            else
            {
                await FullPlayerOverlay.TranslateTo(0, 1000, 380, Easing.CubicIn);
            }

            FullPlayerOverlay.IsVisible = false;
        }

        /// <summary>
        /// Duplica el texto visible para que el scroll sea continuo y no haga el rebote agresivo del prototipo anterior.
        /// </summary>
        private void RestartMetadataMarquee()
        {
            _metadataMarqueeCts?.Cancel();
            _metadataMarqueeCts = new CancellationTokenSource();
            var token = _metadataMarqueeCts.Token;

            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await Task.Delay(180, token);
                if (token.IsCancellationRequested || !FullPlayerOverlay.IsVisible)
                    return;

                await Task.WhenAll(
                    AnimateScrollerAsync(FullPlayerTitleScroller, FullPlayerTitleLabel, token),
                    AnimateScrollerAsync(FullPlayerArtistScroller, FullPlayerArtistLabel, token));
            });
        }

        private void SyncMetadataLabels()
        {
            var title = ViewModel.CurrentSong?.Title ?? string.Empty;
            var artist = ViewModel.CurrentSong?.Artist ?? string.Empty;

            FullPlayerTitleLabel.Text = title;
            FullPlayerArtistLabel.Text = artist;
        }

        private async Task AnimateScrollerAsync(ScrollView scroller, Label label, CancellationToken token)
        {
            var sourceText = label.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                await scroller.ScrollToAsync(0, 0, false);
                return;
            }

            await scroller.ScrollToAsync(0, 0, false);
            await Task.Delay(220, token);

            if (label.Width <= scroller.Width || scroller.Width <= 0)
                return;

            label.Text = sourceText + MarqueeSeparator + sourceText;
            await Task.Delay(100, token);

            var cycleDistance = Math.Max(0, (label.Width / 2) - scroller.Width);
            if (cycleDistance <= 0)
                return;

            var offset = 0d;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(30, token);
                offset += 0.32;
                if (offset >= cycleDistance)
                    offset = 0;

                await scroller.ScrollToAsync(offset, 0, false);
            }
        }
    }
}
