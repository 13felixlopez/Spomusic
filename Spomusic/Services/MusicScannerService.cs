using Spomusic.Models;
using TagLib;

#if ANDROID
using Android.Content;
using Android.Provider;
using Android.Graphics;
#endif

namespace Spomusic.Services
{
    public sealed class MusicScannerService
    {
        private readonly DatabaseService _databaseService;

        public MusicScannerService(DatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public async Task<List<SongItem>> ScanSongsAsync()
        {
            var songs = new List<SongItem>();

#if ANDROID
            songs = await ScanAndroidMediaStore();
            if (songs.Count > 0) return songs;
#endif
            return songs;
        }

#if ANDROID
        private async Task<List<SongItem>> ScanAndroidMediaStore()
        {
            var songs = new List<SongItem>();
            var context = Platform.AppContext;

            var projection = new[]
            {
                MediaStore.Audio.Media.InterfaceConsts.Id,
                MediaStore.Audio.Media.InterfaceConsts.Title,
                MediaStore.Audio.Media.InterfaceConsts.Artist,
                MediaStore.Audio.Media.InterfaceConsts.Album,
                MediaStore.Audio.Media.InterfaceConsts.Duration,
                MediaStore.Audio.Media.InterfaceConsts.Data
            };

            string selection = $"{MediaStore.Audio.Media.InterfaceConsts.IsMusic} != 0";
            var uri = MediaStore.Audio.Media.ExternalContentUri;
            if (uri == null) return songs;

            try
            {
                using var cursor = context.ContentResolver?.Query(uri, projection, selection, null, null);
                if (cursor == null) return songs;

                int titleColumn = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Title);
                int artistColumn = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Artist);
                int albumColumn = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Album);
                int durationColumn = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Duration);
                int dataColumn = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Data);

                while (cursor.MoveToNext())
                {
                    var path = cursor.GetString(dataColumn);
                    if (string.IsNullOrEmpty(path) || path.Contains("/WhatsApp/") || path.Contains("/Telegram/")) continue;

                    var albumArt = await GetAlbumArtCachedAsync(path);
                    songs.Add(new SongItem
                    {
                        Title = cursor.GetString(titleColumn) ?? "Unknown",
                        Artist = cursor.GetString(artistColumn) ?? "Unknown",
                        Album = cursor.GetString(albumColumn) ?? "Unknown",
                        Genre = GetGenre(path),
                        Duration = TimeSpan.FromMilliseconds(cursor.GetLong(durationColumn)),
                        Path = path,
                        AlbumArt = albumArt,
                        AccentColorHex = GetDominantColorHex(albumArt)
                    });
                }
            }
            catch { }
            return songs;
        }

        public async Task<byte[]?> GetAlbumArtCachedAsync(string path)
        {
            var cached = await _databaseService.GetAlbumArtCacheAsync(path);
            if (cached != null) return cached.AlbumArt;

            var raw = ReadEmbeddedAlbumArt(path);
            await _databaseService.SaveAlbumArtCacheAsync(path, raw, GetDominantColorHex(raw));
            return raw;
        }

        public async Task<string> GetDominantColorCachedAsync(string path, byte[]? albumArt)
        {
            var cached = await _databaseService.GetAlbumArtCacheAsync(path);
            if (cached != null && !string.IsNullOrWhiteSpace(cached.DominantColorHex))
                return MakeColorVibrant(cached.DominantColorHex);

            var dominant = GetDominantColorHex(albumArt);
            await _databaseService.SaveAlbumArtCacheAsync(path, albumArt, dominant);
            return dominant;
        }

        public byte[]? GetAlbumArt(string path)
            => ReadEmbeddedAlbumArt(path);

        public string GetGenre(string path)
        {
            try
            {
                using var tagFile = TagLib.File.Create(path);
                return string.IsNullOrWhiteSpace(tagFile.Tag.FirstGenre) ? "Unknown" : tagFile.Tag.FirstGenre;
            }
            catch
            {
                return "Unknown";
            }
        }

        public string GetDominantColorHex(byte[]? albumArt)
        {
            if (albumArt == null || albumArt.Length == 0) return "#1DB954";

            try
            {
                using var bitmap = BitmapFactory.DecodeByteArray(albumArt, 0, albumArt.Length);
                if (bitmap == null) return "#1DB954";

                var scaled = Bitmap.CreateScaledBitmap(bitmap, 24, 24, true);
                long totalR = 0;
                long totalG = 0;
                long totalB = 0;
                long count = 0;

                for (var x = 0; x < scaled.Width; x++)
                {
                    for (var y = 0; y < scaled.Height; y++)
                    {
                        var pixel = new Android.Graphics.Color(scaled.GetPixel(x, y));
                        if (pixel.A < 40) continue;
                        totalR += pixel.R;
                        totalG += pixel.G;
                        totalB += pixel.B;
                        count++;
                    }
                }

                if (count == 0) return "#1DB954";

                var r = (int)(totalR / count);
                var g = (int)(totalG / count);
                var b = (int)(totalB / count);

                // Lift brightness for gradients and readable contrast.
                r = Math.Min(255, (int)(r * 1.15));
                g = Math.Min(255, (int)(g * 1.15));
                b = Math.Min(255, (int)(b * 1.15));

                // Boost saturation so backgrounds don't look dull/gray.
                var avg = (r + g + b) / 3.0;
                r = ClampColor(avg + (r - avg) * 1.45);
                g = ClampColor(avg + (g - avg) * 1.45);
                b = ClampColor(avg + (b - avg) * 1.45);

                // If art is near-gray, nudge toward its dominant channel to keep it dynamic.
                if (Math.Abs(r - g) < 14 && Math.Abs(g - b) < 14)
                {
                    if (r >= g && r >= b)
                    {
                        r = ClampColor(r + 40);
                        g = ClampColor(g - 18);
                        b = ClampColor(b - 18);
                    }
                    else if (g >= r && g >= b)
                    {
                        g = ClampColor(g + 40);
                        r = ClampColor(r - 18);
                        b = ClampColor(b - 18);
                    }
                    else
                    {
                        b = ClampColor(b + 40);
                        r = ClampColor(r - 18);
                        g = ClampColor(g - 18);
                    }
                }

                return $"#{r:X2}{g:X2}{b:X2}";
            }
            catch
            {
                return "#1DB954";
            }
        }

        private static int ClampColor(double value)
            => Math.Max(0, Math.Min(255, (int)Math.Round(value)));

        private static string MakeColorVibrant(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "#1DB954";

            try
            {
                var color = Android.Graphics.Color.ParseColor(hex);
                int r = color.R;
                int g = color.G;
                int b = color.B;

                var avg = (r + g + b) / 3.0;
                r = ClampColor(avg + (r - avg) * 1.45);
                g = ClampColor(avg + (g - avg) * 1.45);
                b = ClampColor(avg + (b - avg) * 1.45);

                if (Math.Abs(r - g) < 14 && Math.Abs(g - b) < 14)
                {
                    if (r >= g && r >= b)
                    {
                        r = ClampColor(r + 40);
                        g = ClampColor(g - 18);
                        b = ClampColor(b - 18);
                    }
                    else if (g >= r && g >= b)
                    {
                        g = ClampColor(g + 40);
                        r = ClampColor(r - 18);
                        b = ClampColor(b - 18);
                    }
                    else
                    {
                        b = ClampColor(b + 40);
                        r = ClampColor(r - 18);
                        g = ClampColor(g - 18);
                    }
                }

                return $"#{r:X2}{g:X2}{b:X2}";
            }
            catch
            {
                return "#1DB954";
            }
        }

        private byte[]? ReadEmbeddedAlbumArt(string path)
        {
            try
            {
                using var retriever = new Android.Media.MediaMetadataRetriever();
                retriever.SetDataSource(path);
                return retriever.GetEmbeddedPicture();
            }
            catch
            {
                return null;
            }
        }
#else
        public Task<byte[]?> GetAlbumArtCachedAsync(string path) => Task.FromResult<byte[]?>(null);
        public Task<string> GetDominantColorCachedAsync(string path, byte[]? albumArt) => Task.FromResult("#1DB954");
        public byte[]? GetAlbumArt(string path) => null;
        public string GetGenre(string path) => "Unknown";
        public string GetDominantColorHex(byte[]? albumArt) => "#1DB954";
#endif
    }
}
