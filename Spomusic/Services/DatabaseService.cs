using SQLite;
using Spomusic.Models;
using System.Text.Json;

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
        public int? ManualOffsetMs { get; set; }
        public int? AutoOffsetMs { get; set; }
        public long UpdatedUtcTicks { get; set; }
    }

    public sealed class LyricTimingState
    {
        public int EffectiveOffsetMs { get; init; }
        public bool HasManualOverride { get; init; }
        public bool HasAutoOverride { get; init; }
    }

    public class VerifiedLyricAlignment
    {
        [PrimaryKey]
        public string SongKey { get; set; } = string.Empty;
        [Indexed]
        public string LyricHash { get; set; } = string.Empty;
        public string Mode { get; set; } = "Original";
        public double ConfidenceScore { get; set; }
        public string LineTimingsJson { get; set; } = string.Empty;
        public long CreatedUtcTicks { get; set; }
        public long UpdatedUtcTicks { get; set; }
    }

    public sealed class VerifiedLyricAlignmentState
    {
        public string Mode { get; init; } = "Original";
        public double ConfidenceScore { get; init; }
        public IReadOnlyList<long> LineTimingsMs { get; init; } = Array.Empty<long>();
        public bool HasUsableAlignment => string.Equals(Mode, "Aligned", StringComparison.OrdinalIgnoreCase) && LineTimingsMs.Count > 0;
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

    public class ScanFolderPreference
    {
        [PrimaryKey]
        public string FolderPath { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
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
            await _database.CreateTableAsync<VerifiedLyricAlignment>();
            await _database.CreateTableAsync<KaraokePreset>();
            await _database.CreateTableAsync<Playlist>();
            await _database.CreateTableAsync<PlaylistSong>();
            await _database.CreateTableAsync<ScanFolderPreference>();

            await EnsureColumnAsync("SongItem", "Genre", "TEXT NOT NULL DEFAULT 'Unknown'");
            await EnsureColumnAsync("SongItem", "AccentColorHex", "TEXT NOT NULL DEFAULT '#1DB954'");
            await EnsureColumnAsync("SongItem", "FolderPath", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync("SongItem", "SearchIndex", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync("LyricCache", "LastFetchedUtcTicks", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync("LyricCache", "ExpiresUtcTicks", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync("LyricTimingOverride", "ManualOffsetMs", "INTEGER NULL");
            await EnsureColumnAsync("LyricTimingOverride", "AutoOffsetMs", "INTEGER NULL");
            await EnsureColumnAsync("VerifiedLyricAlignment", "LyricHash", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync("VerifiedLyricAlignment", "Mode", "TEXT NOT NULL DEFAULT 'Original'");
            await EnsureColumnAsync("VerifiedLyricAlignment", "ConfidenceScore", "REAL NOT NULL DEFAULT 0");
            await EnsureColumnAsync("VerifiedLyricAlignment", "LineTimingsJson", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync("VerifiedLyricAlignment", "CreatedUtcTicks", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync("VerifiedLyricAlignment", "UpdatedUtcTicks", "INTEGER NOT NULL DEFAULT 0");
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

        public async Task<List<SongItem>> GetSongsAsync()
        {
            await Init();
            return await _database!.Table<SongItem>().OrderBy(x => x.Title).ToListAsync();
        }

        public async Task ReplaceSongsAsync(IEnumerable<SongItem> songs)
        {
            await Init();
            var incoming = songs.ToList();
            var existing = await _database!.Table<SongItem>().ToListAsync();
            var existingByPath = existing
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);

            await _database.RunInTransactionAsync(connection =>
            {
                connection.DeleteAll<SongItem>();

                foreach (var song in incoming)
                {
                    if (existingByPath.TryGetValue(song.Path, out var prior))
                    {
                        song.Id = prior.Id;
                        song.IsFavorite = prior.IsFavorite;
                        if (song.AlbumArt == null)
                            song.AlbumArt = prior.AlbumArt;
                        if (string.IsNullOrWhiteSpace(song.AccentColorHex))
                            song.AccentColorHex = prior.AccentColorHex;
                    }

                    connection.Insert(song);
                }
            });
        }

        public async Task UpsertScanFoldersAsync(IEnumerable<ScanFolderItem> folders)
        {
            await Init();
            var now = DateTime.UtcNow.Ticks;
            foreach (var folder in folders)
            {
                var row = await _database!.Table<ScanFolderPreference>()
                    .FirstOrDefaultAsync(x => x.FolderPath == folder.Path);

                if (row == null)
                {
                    await _database.InsertAsync(new ScanFolderPreference
                    {
                        FolderPath = folder.Path,
                        DisplayName = folder.DisplayName,
                        IsSelected = folder.IsSelected,
                        UpdatedUtcTicks = now
                    });
                    continue;
                }

                row.DisplayName = folder.DisplayName;
                row.IsSelected = folder.IsSelected;
                row.UpdatedUtcTicks = now;
                await _database.UpdateAsync(row);
            }
        }

        public async Task<List<ScanFolderItem>> GetScanFoldersAsync()
        {
            await Init();
            var rows = await _database!.Table<ScanFolderPreference>()
                .OrderBy(x => x.DisplayName)
                .ToListAsync();

            return rows.Select(x => new ScanFolderItem
            {
                Path = x.FolderPath,
                DisplayName = x.DisplayName,
                IsSelected = x.IsSelected
            }).ToList();
        }

        public async Task<List<string>> GetSelectedScanFolderPathsAsync()
        {
            await Init();
            var rows = await _database!.Table<ScanFolderPreference>()
                .Where(x => x.IsSelected)
                .ToListAsync();

            return rows.Select(x => x.FolderPath).ToList();
        }

        public async Task SaveSelectedScanFoldersAsync(IEnumerable<ScanFolderItem> folders)
            => await UpsertScanFoldersAsync(folders);

        public async Task<int> CreatePlaylistAsync(string name)
        {
            await Init();
            var playlist = new Playlist { Name = name.Trim() };
            await _database!.InsertAsync(playlist);
            return playlist.Id;
        }

        public async Task<List<Playlist>> GetPlaylistsAsync()
        {
            await Init();
            return await _database!.Table<Playlist>().OrderBy(x => x.Name).ToListAsync();
        }

        public async Task DeletePlaylistAsync(int playlistId)
        {
            await Init();
            var playlist = await _database!.Table<Playlist>().FirstOrDefaultAsync(x => x.Id == playlistId);
            if (playlist != null)
                await _database.DeleteAsync(playlist);

            var playlistSongs = await _database.Table<PlaylistSong>().Where(x => x.PlaylistId == playlistId).ToListAsync();
            foreach (var row in playlistSongs)
                await _database.DeleteAsync(row);
        }

        public async Task AddSongToPlaylistAsync(int playlistId, int songId)
        {
            await Init();
            var exists = await _database!.Table<PlaylistSong>()
                .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.SongId == songId);
            if (exists != null) return;

            await _database.InsertAsync(new PlaylistSong
            {
                PlaylistId = playlistId,
                SongId = songId
            });
        }

        public async Task RemoveSongFromPlaylistAsync(int playlistId, int songId)
        {
            await Init();
            var row = await _database!.Table<PlaylistSong>()
                .FirstOrDefaultAsync(x => x.PlaylistId == playlistId && x.SongId == songId);
            if (row != null)
                await _database.DeleteAsync(row);
        }

        public async Task<List<SongItem>> GetSongsForPlaylistAsync(int playlistId)
        {
            await Init();
            var songs = await _database!.QueryAsync<SongItem>(
                @"SELECT s.* FROM SongItem s
                  INNER JOIN PlaylistSong ps ON ps.SongId = s.Id
                  WHERE ps.PlaylistId = ?
                  ORDER BY ps.Id", playlistId);
            return songs;
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
            await SetManualLyricTimingOffsetAsync(songKey, offsetMs);
        }

        public async Task SetManualLyricTimingOffsetAsync(string songKey, int offsetMs)
        {
            await Init();
            var row = await _database!.Table<LyricTimingOverride>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (row == null)
            {
                row = new LyricTimingOverride
                {
                    SongKey = songKey,
                    OffsetMs = offsetMs,
                    ManualOffsetMs = offsetMs,
                    AutoOffsetMs = null,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                await _database.InsertAsync(row);
                return;
            }

            row.ManualOffsetMs = offsetMs == 0 ? null : offsetMs;
            if (offsetMs == 0)
            {
                row.AutoOffsetMs = null;
                row.OffsetMs = 0;
            }
            else
            {
                row.OffsetMs = offsetMs;
            }
            row.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            await _database.UpdateAsync(row);
        }

        public async Task SetAutoLyricTimingOffsetAsync(string songKey, int offsetMs)
        {
            await Init();
            var row = await _database!.Table<LyricTimingOverride>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (row == null)
            {
                row = new LyricTimingOverride
                {
                    SongKey = songKey,
                    OffsetMs = offsetMs,
                    ManualOffsetMs = null,
                    AutoOffsetMs = offsetMs,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                await _database.InsertAsync(row);
                return;
            }

            row.AutoOffsetMs = offsetMs;
            row.OffsetMs = ResolveEffectiveOffset(row);
            row.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            await _database.UpdateAsync(row);
        }

        public async Task<int> GetLyricTimingOffsetAsync(string songKey)
        {
            var state = await GetLyricTimingStateAsync(songKey);
            return state.EffectiveOffsetMs;
        }

        public async Task<LyricTimingState> GetLyricTimingStateAsync(string songKey)
        {
            await Init();
            var row = await _database!.Table<LyricTimingOverride>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (row == null)
            {
                return new LyricTimingState
                {
                    EffectiveOffsetMs = 0,
                    HasManualOverride = false,
                    HasAutoOverride = false
                };
            }

            var hasManual = row.ManualOffsetMs.HasValue || (!row.ManualOffsetMs.HasValue && !row.AutoOffsetMs.HasValue && row.OffsetMs != 0);
            var hasAuto = row.AutoOffsetMs.HasValue;

            return new LyricTimingState
            {
                EffectiveOffsetMs = ResolveEffectiveOffset(row),
                HasManualOverride = hasManual,
                HasAutoOverride = hasAuto
            };
        }

        private static int ResolveEffectiveOffset(LyricTimingOverride row)
        {
            if (row.ManualOffsetMs.HasValue)
                return row.ManualOffsetMs.Value;

            if (row.AutoOffsetMs.HasValue)
                return row.AutoOffsetMs.Value;

            return row.OffsetMs;
        }

        public async Task<VerifiedLyricAlignmentState?> GetVerifiedLyricAlignmentAsync(string songKey, string lyricHash)
        {
            await Init();
            var row = await _database!.Table<VerifiedLyricAlignment>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (row == null)
                return null;

            if (!string.Equals(row.LyricHash, lyricHash, StringComparison.Ordinal))
                return null;

            IReadOnlyList<long> timings = Array.Empty<long>();
            if (!string.IsNullOrWhiteSpace(row.LineTimingsJson))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<long>>(row.LineTimingsJson);
                    timings = parsed != null ? parsed : Array.Empty<long>();
                }
                catch
                {
                    timings = Array.Empty<long>();
                }
            }

            return new VerifiedLyricAlignmentState
            {
                Mode = row.Mode,
                ConfidenceScore = row.ConfidenceScore,
                LineTimingsMs = timings
            };
        }

        public async Task SaveVerifiedLyricAlignmentAsync(string songKey, string lyricHash, string mode, double confidenceScore, IReadOnlyList<long> lineTimingsMs)
        {
            await Init();
            var now = DateTime.UtcNow.Ticks;
            var payload = JsonSerializer.Serialize(lineTimingsMs);
            var row = await _database!.Table<VerifiedLyricAlignment>().FirstOrDefaultAsync(x => x.SongKey == songKey);
            if (row == null)
            {
                row = new VerifiedLyricAlignment
                {
                    SongKey = songKey,
                    LyricHash = lyricHash,
                    Mode = mode,
                    ConfidenceScore = confidenceScore,
                    LineTimingsJson = payload,
                    CreatedUtcTicks = now,
                    UpdatedUtcTicks = now
                };
                await _database.InsertAsync(row);
                return;
            }

            row.LyricHash = lyricHash;
            row.Mode = mode;
            row.ConfidenceScore = confidenceScore;
            row.LineTimingsJson = payload;
            if (row.CreatedUtcTicks == 0)
                row.CreatedUtcTicks = now;
            row.UpdatedUtcTicks = now;
            await _database.UpdateAsync(row);
        }

        public async Task DeleteVerifiedLyricAlignmentAsync(string songKey)
        {
            await Init();
            await _database!.DeleteAsync<VerifiedLyricAlignment>(songKey);
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
