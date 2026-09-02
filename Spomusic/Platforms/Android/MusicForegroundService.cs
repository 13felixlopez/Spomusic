using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Support.V4.Media;
using Android.Support.V4.Media.Session;
using AndroidX.Core.App;
using Spomusic.Models;
using Spomusic.Services;
using Android.Graphics;

namespace Spomusic.Platforms.Android
{
    [Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
    public class MusicForegroundService : Service
    {
        private const int NotificationId = 1001;
        private const string ChannelId = "music_channel";
        private MediaSessionCompat? _mediaSession;

        public const string ActionPlay = "ACTION_PLAY";
        public const string ActionPause = "ACTION_PAUSE";
        public const string ActionNext = "ACTION_NEXT";
        public const string ActionPrev = "ACTION_PREV";
        public const string ActionStop = "ACTION_STOP";
        public const string ActionClose = "ACTION_CLOSE";

        public override IBinder? OnBind(Intent? intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            CreateNotificationChannel();
            _mediaSession = new MediaSessionCompat(this, "Spomusic");
            
            var musicService = IPlatformApplication.Current.Services.GetService<IMusicService>();
            if (musicService != null)
            {
                _mediaSession.SetCallback(new MediaSessionCallback(musicService));
            }
            
            _mediaSession.Active = true;
        }

        private class MediaSessionCallback : MediaSessionCompat.Callback
        {
            private readonly IMusicService _musicService;
            public MediaSessionCallback(IMusicService musicService) => _musicService = musicService;
            public override void OnPlay() => _musicService.Resume();
            public override void OnPause() => _musicService.Pause();
            public override void OnSkipToNext() => _musicService.Next();
            public override void OnSkipToPrevious() => _musicService.Previous();
            public override void OnStop() => _musicService.Stop();
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(ChannelId, "Spomusic Playback", NotificationImportance.Low)
                {
                    Description = "Controles de reproducción y pantalla de bloqueo"
                };
                channel.SetShowBadge(false);
                channel.LockscreenVisibility = NotificationVisibility.Public;
                var manager = (NotificationManager)GetSystemService(NotificationService)!;
                manager?.CreateNotificationChannel(channel);
            }
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            var action = intent?.Action;
            if (!string.IsNullOrEmpty(action))
            {
                var musicService = IPlatformApplication.Current.Services.GetService<IMusicService>();
                switch (action)
                {
                    case ActionPlay:
                        musicService?.Resume();
                        return StartCommandResult.Sticky;
                    case ActionPause:
                        musicService?.Pause();
                        return StartCommandResult.Sticky;
                    case ActionNext:
                        musicService?.Next();
                        return StartCommandResult.Sticky;
                    case ActionPrev:
                        musicService?.Previous();
                        return StartCommandResult.Sticky;
                    case ActionStop:
                    case ActionClose:
                        musicService?.Stop();
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                            StopForeground(StopForegroundFlags.Remove);
                        else
#pragma warning disable CS0618
                            StopForeground(true);
#pragma warning restore CS0618
                        StopSelf();
                        return StartCommandResult.NotSticky;
                }
            }

            var title = intent?.GetStringExtra("title") ?? "Spomusic";
            var artist = intent?.GetStringExtra("artist") ?? "Reproduciendo música";
            var isPlaying = intent?.GetBooleanExtra("isPlaying", true) ?? true;
            var albumArtBytes = intent?.GetByteArrayExtra("albumArt");
            var durationMs = intent?.GetLongExtra("durationMs", 0) ?? 0;
            var positionMs = intent?.GetLongExtra("positionMs", 0) ?? 0;

            var mainIntent = new Intent(this, typeof(MainActivity));
            mainIntent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            var pendingMainIntent = PendingIntent.GetActivity(this, 0, mainIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            _mediaSession?.SetSessionActivity(pendingMainIntent);

            UpdateMediaSession(title, artist, albumArtBytes, durationMs, positionMs, isPlaying);

            var builder = new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle(title)
                .SetContentText(artist)
                .SetSmallIcon(isPlaying
                    ? global::Android.Resource.Drawable.IcMediaPlay
                    : global::Android.Resource.Drawable.IcMediaPause)
                .SetContentIntent(pendingMainIntent)
                .SetOngoing(isPlaying)
                .SetOnlyAlertOnce(true)
                .SetShowWhen(false)
                .SetCategory(NotificationCompat.CategoryTransport)
                .SetVisibility(NotificationCompat.VisibilityPublic)
                .SetPriority(NotificationCompat.PriorityLow)
                .SetStyle(new AndroidX.Media.App.NotificationCompat.MediaStyle()
                    .SetMediaSession(_mediaSession?.SessionToken)
                    .SetShowActionsInCompactView(0, 1, 2));

            if (albumArtBytes != null)
            {
                try
                {
                    Bitmap bitmap = BitmapFactory.DecodeByteArray(albumArtBytes, 0, albumArtBytes.Length);
                    if (bitmap != null)
                        builder.SetLargeIcon(bitmap);
                }
                catch { }
            }

            // Prev Action (index 0)
            builder.AddAction(global::Android.Resource.Drawable.IcMediaPrevious, "Anterior", GetPendingIntent(ActionPrev));

            // Play/Pause Action (index 1)
            if (isPlaying)
                builder.AddAction(global::Android.Resource.Drawable.IcMediaPause, "Pausa", GetPendingIntent(ActionPause));
            else
                builder.AddAction(global::Android.Resource.Drawable.IcMediaPlay, "Reproducir", GetPendingIntent(ActionPlay));

            // Next Action (index 2)
            builder.AddAction(global::Android.Resource.Drawable.IcMediaNext, "Siguiente", GetPendingIntent(ActionNext));

            // Close Action (index 3)
            builder.AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Cerrar", GetPendingIntent(ActionClose));

            var notification = builder.Build();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeMediaPlayback);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }

            if (!isPlaying && Build.VERSION.SdkInt >= BuildVersionCodes.N)
            {
                StopForeground(StopForegroundFlags.Detach);
            }

            return StartCommandResult.Sticky;
        }

        private PendingIntent GetPendingIntent(string action)
        {
            var intent = new Intent(this, typeof(MusicForegroundService));
            intent.SetAction(action);
            return PendingIntent.GetService(this, action.GetHashCode(), intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        }

        private void UpdateMediaSession(string title, string artist, byte[]? albumArtBytes, long durationMs, long positionMs, bool isPlaying)
        {
            if (_mediaSession == null)
                return;

            var metadataBuilder = new MediaMetadataCompat.Builder()
                .PutString(MediaMetadataCompat.MetadataKeyTitle, title)
                .PutString(MediaMetadataCompat.MetadataKeyArtist, artist)
                .PutLong(MediaMetadataCompat.MetadataKeyDuration, durationMs);

            if (albumArtBytes != null)
            {
                var bitmap = BitmapFactory.DecodeByteArray(albumArtBytes, 0, albumArtBytes.Length);
                if (bitmap != null)
                    metadataBuilder.PutBitmap(MediaMetadataCompat.MetadataKeyAlbumArt, bitmap);
            }

            _mediaSession.SetMetadata(metadataBuilder.Build());

            var availableActions =
                PlaybackStateCompat.ActionPlay |
                PlaybackStateCompat.ActionPause |
                PlaybackStateCompat.ActionPlayPause |
                PlaybackStateCompat.ActionSkipToNext |
                PlaybackStateCompat.ActionSkipToPrevious |
                PlaybackStateCompat.ActionStop;

            var playbackState = new PlaybackStateCompat.Builder()
                .SetActions(availableActions)
                .SetState(
                    isPlaying ? PlaybackStateCompat.StatePlaying : PlaybackStateCompat.StatePaused,
                    Math.Max(0, positionMs),
                    isPlaying ? 1f : 0f)
                .Build();

            _mediaSession.SetPlaybackState(playbackState);
        }

        public override void OnTaskRemoved(Intent? rootIntent)
        {
            base.OnTaskRemoved(rootIntent);
            try
            {
                var musicService = IPlatformApplication.Current?.Services?.GetService<IMusicService>();
                musicService?.Stop();
            }
            catch { }

            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                    StopForeground(StopForegroundFlags.Remove);
                else
#pragma warning disable CS0618
                    StopForeground(true);
#pragma warning restore CS0618
            }
            catch { }

            StopSelf();
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            _mediaSession?.Release();
        }
    }
}
