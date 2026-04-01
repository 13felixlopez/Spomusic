using SQLite;
using Spomusic.Models;

namespace Spomusic.Services
{
    public class LyricCache
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string SongKey { get; set; } = string.Empty;
        public string RawLrc { get; set; } = string.Empty;
        public bool IsDownloaded { get; set; }
        public long LastFetchedUtcTicks { get; set; }
        public long ExpiresUtcTicks { get; set; }
    }

    public class PendingLyricRetry
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        [Indexed]
        public string SongKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public int Attempts { get; set; }
        public long NextAttemptUtcTicks { get; set; }
        public string LastError { get; set; } = string.Empty;
    }

    public class PlaybackEvent
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        [Indexed]
        public string SongKey { get; set; } = string.Empty;
        [Indexed]
        public string EventType { get; set; } = string.Empty;
        [Indexed]
        public long UtcTicks { get; set; }
    }

    public class AlbumArtCache
    {
        [PrimaryKey]
        public string SongPath { get; set; } = string.Empty;
        public byte[]? AlbumArt { get; set; }
        public string DominantColorHex { get; set; } = "#1DB954";
        public long ExpiresUtcTicks { get; set; }
        public long LastAccessUtcTicks { get; set; }
    }

    public class PlaybackResumeState
    {
        [PrimaryKey]
        public string SongPath { get; set; } = string.Empty;
        public double PositionSeconds { get; set; }
        public long LastUpdatedUtcTicks { get; set; }
    }

    public class LyricTimingOverride
    {
        [PrimaryKey]
        public string SongKey { get; set; } = string.Empty;
        public int OffsetMs { get; set; }
        public long UpdatedUtcTicks { get; set; }
    }

    public class KaraokePreset
    {
        [PrimaryKey]
        public string SongKey { get; set; } = string.Empty;
        public int LeadMs { get; set; }
        public double FontSize { get; set; }
        public bool HighContrast { get; set; }
        public long UpdatedUtcTicks { get; set; }
    }

    public sealed class DatabaseService
    {
        private SQLiteAsyncConnection? _database;

        private sealed class ColumnInfo
        {
            public string name { get; set; } = string.Empty;
        }

        private async Task Init()
        {
            if (_database is not null) return;
            var dbPath = Path.Combine(FileSystem.AppDataDirectory, "Spomusic.db3");
            _database = new SQLiteAsyncConnection(dbPath);

            await _database.CreateTableAsync<SongItem>();
            await _database.CreateTableAsync<LyricCache>();
            await _database.CreateTableAsync<PendingLyricRetry>();
            await _database.CreateTableAsync<PlaybackEvent>();
            await _database.CreateTableAsync<AlbumArtCache>();
            await _database.CreateTableAsync<PlaybackResumeState>();
            await _database.CreateTableAsync<LyricTimingOverride>();
            await _database.CreateTableAsync<KaraokePreset>();

            await EnsureColumnAsync("SongItem", "Genre", "TEXT NOT NULL DEFAULT 'Unknown'");
            await EnsureColumnAsync("SongItem", "AccentColorHex", "TEXT NOT NULL DEFAULT '#1DB954'");
            await EnsureColumnAsync("LyricCache", "LastFetchedUtcTicks", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync("LyricCache", "ExpiresUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        }

        private async Task EnsureColumnAsync(string tableName, string columnName, string definition)
        {
            var columns = await _database!.QueryAsync<ColumnInfo>($"PRAGMA table_info({tableName});");
            if (columns.Any(c => string.Equals(c.name, columnName, StringComparison.OrdinalIgnoreCase)))
                return;

            await _database.ExecuteAsync($"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};");
        }

        public async Task<int> UpdateSongAsync(SongItem song)
        {
            await Init();
            return await _database!.UpdateAsync(song);
        }

        public async Task SaveLyricsAsync(string songKey, string rawLrc, bool isDownloaded, TimeSpan? ttl = null)
        {
            await Init();
            var nowTicks = DateTime.UtcNow.Ticks;
            var expiryTicks = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromDays(7)).Ticks;
            var existing = await _database!.Table<LyricCache>().FirstOrDefaultAsync(x => x.SongKey == songKey);

            if (existing != null)
            {
                existing.RawLrc = rawLrc;
                existing.IsDownloaded = isDownloaded || existing.IsDownloaded;
                existing.LastFetchedUtcTicks = nowTicks;
                existing.ExpiresUtcTicks = expiryTicks;
                await _database.UpdateAsync(existing);
            }
            else
            {
                await _database.InsertAsync(new LyricCache
                {
                    SongKey = songKey,
                    RawLrc = rawLrc,
                    IsDownloaded = isDownloaded,
                    LastFetchedUtcTicks = nowTicks,
                    ExpiresUtcTicks = expiryTicks
                });
            }
        }

        public async Task<LyricCache?> GetLyricsAsync(string songKey, bool allowExpired = false)
        {
            await Init();
            var cached = await _database!.Table<LyricCache>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (cached == null) return null;
            if (allowExpired || cached.IsDownloaded) return cached;
            return cached.ExpiresUtcTicks >= DateTime.UtcNow.Ticks ? cached : null;
        }

        public async Task QueueLyricRetryAsync(string title, string artist, string lastError)
        {
            await Init();
            var songKey = $"{title}_{artist}";
            var existing = await _database!.Table<PendingLyricRetry>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (existing != null) return;

            await _database.InsertAsync(new PendingLyricRetry
            {
                SongKey = songKey,
                Title = title,
                Artist = artist,
                Attempts = 0,
                NextAttemptUtcTicks = DateTime.UtcNow.AddSeconds(20).Ticks,
                LastError = lastError
            });
        }

        public async Task<List<PendingLyricRetry>> GetDueLyricRetriesAsync(int limit = 10)
        {
            await Init();
            var nowTicks = DateTime.UtcNow.Ticks;
            return await _database!.Table<PendingLyricRetry>()
                .Where(x => x.NextAttemptUtcTicks <= nowTicks)
                .OrderBy(x => x.NextAttemptUtcTicks)
                .Take(limit)
                .ToListAsync();
        }

        public async Task MarkLyricRetrySucceededAsync(string songKey)
        {
            await Init();
            var row = await _database!.Table<PendingLyricRetry>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (row != null) await _database.DeleteAsync(row);
        }

        public async Task MarkLyricRetryFailedAsync(PendingLyricRetry retry, string lastError)
        {
            await Init();
            retry.Attempts += 1;
            var backoffMinutes = Math.Min(240, Math.Pow(2, retry.Attempts));
            retry.NextAttemptUtcTicks = DateTime.UtcNow.AddMinutes(backoffMinutes).Ticks;
            retry.LastError = lastError;
            await _database!.UpdateAsync(retry);
        }

        public async Task SaveAlbumArtCacheAsync(string songPath, byte[]? albumArt, string dominantColorHex, TimeSpan? ttl = null)
        {
            await Init();
            var expires = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromHours(8)).Ticks;
            var now = DateTime.UtcNow.Ticks;
            var row = await _database!.Table<AlbumArtCache>().FirstOrDefaultAsync(x => x.SongPath == songPath);
            if (row == null)
            {
                row = new AlbumArtCache
                {
                    SongPath = songPath,
                    AlbumArt = albumArt,
                    DominantColorHex = dominantColorHex,
                    ExpiresUtcTicks = expires,
                    LastAccessUtcTicks = now
                };
                await _database.InsertAsync(row);
                return;
            }

            row.AlbumArt = albumArt;
            row.DominantColorHex = dominantColorHex;
            row.ExpiresUtcTicks = expires;
            row.LastAccessUtcTicks = now;
            await _database.UpdateAsync(row);
        }

        public async Task<AlbumArtCache?> GetAlbumArtCacheAsync(string songPath)
        {
            await Init();
            var row = await _database!.Table<AlbumArtCache>().FirstOrDefaultAsync(x => x.SongPath == songPath);
            if (row == null) return null;
            if (row.ExpiresUtcTicks < DateTime.UtcNow.Ticks) return null;
            row.LastAccessUtcTicks = DateTime.UtcNow.Ticks;
            await _database.UpdateAsync(row);
            return row;
        }

        public async Task RecordPlaybackEventAsync(string songKey, string eventType)
        {
            await Init();
            await _database!.InsertAsync(new PlaybackEvent
            {
                SongKey = songKey,
                EventType = eventType,
                UtcTicks = DateTime.UtcNow.Ticks
            });
        }

        public async Task SaveResumeStateAsync(string songPath, double positionSeconds)
        {
            await Init();
            var row = await _database!.Table<PlaybackResumeState>().FirstOrDefaultAsync(x => x.SongPath == songPath);
            if (row == null)
            {
                row = new PlaybackResumeState
                {
                    SongPath = songPath,
                    PositionSeconds = positionSeconds,
                    LastUpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                await _database.InsertAsync(row);
                return;
            }

            row.PositionSeconds = positionSeconds;
            row.LastUpdatedUtcTicks = DateTime.UtcNow.Ticks;
            await _database.UpdateAsync(row);
        }

        public async Task<double> GetResumeStateAsync(string songPath)
        {
            await Init();
            var row = await _database!.Table<PlaybackResumeState>().FirstOrDefaultAsync(x => x.SongPath == songPath);
            return row?.PositionSeconds ?? 0d;
        }

        public async Task<Dictionary<string, double>> GetRecentResumeStatesAsync(int limit = 20)
        {
            await Init();
            var rows = await _database!.Table<PlaybackResumeState>()
                .OrderByDescending(x => x.LastUpdatedUtcTicks)
                .Take(limit)
                .ToListAsync();
            return rows.ToDictionary(x => x.SongPath, x => x.PositionSeconds, StringComparer.OrdinalIgnoreCase);
        }

        public async Task SetLyricTimingOffsetAsync(string songKey, int offsetMs)
        {
            await Init();
            var row = await _database!.Table<LyricTimingOverride>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (row == null)
            {
                row = new LyricTimingOverride
                {
                    SongKey = songKey,
                    OffsetMs = offsetMs,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                await _database.InsertAsync(row);
                return;
            }

            row.OffsetMs = offsetMs;
            row.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            await _database.UpdateAsync(row);
        }

        public async Task<int> GetLyricTimingOffsetAsync(string songKey)
        {
            await Init();
            var row = await _database!.Table<LyricTimingOverride>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            return row?.OffsetMs ?? 0;
        }

        public async Task<Dictionary<string, int>> GetTopSongScoresAsync(int days = 30, int limit = 20)
        {
            await Init();
            var sinceTicks = DateTime.UtcNow.AddDays(-days).Ticks;
            var rows = await _database!.Table<PlaybackEvent>().Where(x => x.UtcTicks >= sinceTicks).ToListAsync();

            var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["play"] = 3,
                ["next"] = 1,
                ["previous"] = 1,
                ["favorite"] = 6,
                ["repeat"] = 2
            };

            return rows
                .GroupBy(x => x.SongKey)
                .Select(g => new
                {
                    SongKey = g.Key,
                    Score = g.Sum(ev => weights.TryGetValue(ev.EventType, out var w) ? w : 1)
                })
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .ToDictionary(x => x.SongKey, x => x.Score, StringComparer.OrdinalIgnoreCase);
        }

        public async Task<KaraokePreset?> GetKaraokePresetAsync(string songKey)
        {
            await Init();
            return await _database!.Table<KaraokePreset>().FirstOrDefaultAsync(x => x.SongKey == songKey);
        }

        public async Task SaveKaraokePresetAsync(string songKey, int leadMs, double fontSize, bool highContrast)
        {
            await Init();
            var row = await _database!.Table<KaraokePreset>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (row == null)
            {
                row = new KaraokePreset
                {
                    SongKey = songKey,
                    LeadMs = leadMs,
                    FontSize = fontSize,
                    HighContrast = highContrast,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                await _database.InsertAsync(row);
                return;
            }

            row.LeadMs = leadMs;
            row.FontSize = fontSize;
            row.HighContrast = highContrast;
            row.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            await _database.UpdateAsync(row);
        }

        public async Task DeleteLyricsAsync(string songKey)
        {
            await Init();
            var cached = await _database!.Table<LyricCache>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (cached != null)
                await _database.DeleteAsync(cached);
        }
    }
}
