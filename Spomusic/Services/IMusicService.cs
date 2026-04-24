using Spomusic.Models;
using System.Collections.Generic;
using System;

namespace Spomusic.Services
{
    public class LyricLine
    {
        public int Index { get; set; }
        public TimeSpan Time { get; set; }
        public TimeSpan OriginalTime { get; set; }
        public bool HasExplicitTiming { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public enum RepeatMode
    {
        None,
        One,
        All
    }

    public interface IMusicService
    {
        void Play(SongItem song);
        void Pause();
        void Resume();
        void Next();
        void Previous();
        void Stop();
        void PlayRandom();
        void SeekTo(TimeSpan position);
        
        bool IsPlaying { get; }
        SongItem? CurrentSong { get; }
        bool IsShuffle { get; set; }
        RepeatMode RepeatMode { get; set; }
        
        List<LyricLine> CurrentLyrics { get; }
        string? CurrentLyricLine { get; }
        int CurrentLyricIndex { get; }
        double CurrentLyricWordProgress { get; }
        int LyricLeadMs { get; set; }
        int CurrentLyricOffsetMs { get; }
        
        void SetPlaylist(List<SongItem> songs);
        Task ShareCurrentSong();
        Task FetchLyricsAsync(SongItem song);
        Task SaveLyricsOfflineAsync(SongItem song);
        Task DeleteDownloadedLyricsAsync(SongItem song);
        Task DownloadLyricsBatchAsync(IEnumerable<SongItem> songs);
        Task RegisterTimingTapAsync();
        Task AutoSyncLyricsAsync();
        Task SetLyricOffsetAsync(int offsetMs);
        void MoveQueueItem(int fromIndex, int toIndex);
        IReadOnlyList<SongItem> GetQueueSnapshot();

        event Action<SongItem?>? OnSongChanged;
        event Action<bool>? OnPlaybackStatusChanged;
        event Action<TimeSpan>? OnPositionChanged;
        event Action<int>? OnLyricIndexChanged;
        event Action<double>? OnLyricProgressChanged;
        event Action<IReadOnlyList<SongItem>>? OnQueueChanged;
        event Action<TimeSpan?>? OnSleepTimerChanged;
        
        TimeSpan CurrentPosition { get; }
        TimeSpan Duration { get; }
        TimeSpan? SleepTimerRemaining { get; }
        void SetSleepTimer(TimeSpan duration);
        void CancelSleepTimer();
    }
}
