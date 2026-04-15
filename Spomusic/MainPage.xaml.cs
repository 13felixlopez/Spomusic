using Spomusic.ViewModels;
using Spomusic.Models;

namespace Spomusic
{
    public partial class MainPage : ContentPage
    {
        private MainViewModel ViewModel => (MainViewModel)BindingContext;
        private int _lastScrolledLyricIndex = -1;
        private CancellationTokenSource? _lyricsScrollCts;

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
            
            // Auto-scroll lyrics logic
            ViewModel.RequestScrollToLyric += (index) => {
                MainThread.BeginInvokeOnMainThread(() => {
                    if (LyricsCollectionView.ItemsSource != null && index >= 0)
                    {
                        _ = ScrollLyricsSmoothAsync(index);
                    }
                });
            };
        }

        private async void OnMiniPlayerTapped(object sender, TappedEventArgs e)
        {
            FullPlayerOverlay.IsVisible = true;
            if (ViewModel.IsReducedMotion)
            {
                FullPlayerOverlay.TranslationY = 0;
                return;
            }

            await FullPlayerOverlay.TranslateTo(0, 0, 420, Easing.CubicOut);
        }

        private async void OnCloseFullPlayerClicked(object sender, EventArgs e)
        {
            if (ViewModel.IsReducedMotion)
            {
                FullPlayerOverlay.TranslationY = 1000;
            }
            else
            {
                await FullPlayerOverlay.TranslateTo(0, 1000, 380, Easing.CubicIn);
            }
            FullPlayerOverlay.IsVisible = false;
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

                LyricsCollectionView.ScrollTo(index, position: ScrollToPosition.MakeVisible, animate: !ViewModel.IsReducedMotion);
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
            await CheckAndRequestPermissions();
        }

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

        private void OnCloseAppClicked(object sender, TappedEventArgs e)
        {
#if ANDROID
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#else
            var window = Application.Current?.Windows.FirstOrDefault();
            if (window != null)
                Application.Current!.CloseWindow(window);
#endif
        }
    }
}
