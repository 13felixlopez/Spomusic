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
            _mediaSession.Active = true;
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(ChannelId, "Spomusic Playback", NotificationImportance.Low);
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
                    case ActionPlay: musicService?.Resume(); break;
                    case ActionPause: musicService?.Pause(); break;
                    case ActionNext: musicService?.Next(); break;
                    case ActionPrev: musicService?.Previous(); break;
                    case ActionStop:
                        musicService?.Stop();
                        StopForeground(StopForegroundFlags.Remove);
                        StopSelf();
                        return StartCommandResult.NotSticky;
                    case ActionClose:
                        musicService?.Stop();
                        StopForeground(StopForegroundFlags.Remove);
                        StopSelf();
                        return StartCommandResult.NotSticky;
                }
            }

            var title = intent?.GetStringExtra("title") ?? "Spomusic";
            var artist = intent?.GetStringExtra("artist") ?? "Escuchando ahora";
            var isPlaying = intent?.GetBooleanExtra("isPlaying", true) ?? true;
            var albumArtBytes = intent?.GetByteArrayExtra("albumArt");
            var durationMs = intent?.GetLongExtra("durationMs", 0) ?? 0;
            var positionMs = intent?.GetLongExtra("positionMs", 0) ?? 0;

            // Intent to open the app when tapping the notification
            var mainIntent = new Intent(this, typeof(MainActivity));
            mainIntent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            var pendingMainIntent = PendingIntent.GetActivity(this, 0, mainIntent, PendingIntentFlags.Immutable);
            _mediaSession?.SetSessionActivity(pendingMainIntent);

            // Lock-screen controls depend on a live media session with current
            // metadata and playback state, not just on a foreground notification.
            UpdateMediaSession(title, artist, albumArtBytes, durationMs, positionMs, isPlaying);

            var builder = new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle(title)
                .SetContentText(artist)
                .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                .SetContentIntent(pendingMainIntent)
                .SetOngoing(isPlaying)
                .SetOnlyAlertOnce(true)
                .SetCategory(NotificationCompat.CategoryTransport)
                .SetVisibility(NotificationCompat.VisibilityPublic)
                .SetStyle(new AndroidX.Media.App.NotificationCompat.MediaStyle()
                    .SetMediaSession(_mediaSession?.SessionToken)
                    .SetShowActionsInCompactView(0, 1, 2, 3));

            if (albumArtBytes != null)
            {
                Bitmap bitmap = BitmapFactory.DecodeByteArray(albumArtBytes, 0, albumArtBytes.Length);
                builder.SetLargeIcon(bitmap);
            }

            // Prev Action
            builder.AddAction(global::Android.Resource.Drawable.IcMediaPrevious, "Prev", GetPendingIntent(ActionPrev));
            
            // Play/Pause Action
            if (isPlaying)
                builder.AddAction(global::Android.Resource.Drawable.IcMediaPause, "Pause", GetPendingIntent(ActionPause));
            else
                builder.AddAction(global::Android.Resource.Drawable.IcMediaPlay, "Play", GetPendingIntent(ActionPlay));

            // Next Action
            builder.AddAction(global::Android.Resource.Drawable.IcMediaNext, "Next", GetPendingIntent(ActionNext));

            // Close Action
            builder.AddAction(global::Android.Resource.Drawable.IcMenuCloseClearCancel, "Cerrar", GetPendingIntent(ActionClose));

            var notification = builder.Build();
            StartForeground(NotificationId, notification);

            return StartCommandResult.Sticky;
        }

        private PendingIntent GetPendingIntent(string action)
        {
            var intent = new Intent(this, typeof(MusicForegroundService));
            intent.SetAction(action);
            return PendingIntent.GetService(this, action.GetHashCode(), intent, PendingIntentFlags.Immutable);
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

        public override void OnDestroy()
        {
            base.OnDestroy();
            _mediaSession?.Release();
        }
    }
}
