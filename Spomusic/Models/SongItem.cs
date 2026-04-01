using SQLite;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Spomusic.Models
{
    public partial class SongItem : ObservableObject
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Genre { get; set; } = "Unknown";
        public string AccentColorHex { get; set; } = "#1DB954";
        public string Path { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public byte[]? AlbumArt { get; set; }

        [ObservableProperty]
        private bool _isFavorite;
    }

    public class Playlist
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class PlaylistSong
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public int PlaylistId { get; set; }
        public int SongId { get; set; }
    }
}
