using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;

namespace Spomusic.Platforms.Android
{
    [BroadcastReceiver(Label = "Spomusic Widget", Exported = true)]
    [IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
    [MetaData("android.appwidget.provider", Resource = "@xml/spomusic_widget_info")]
    public class SpomusicAppWidget : AppWidgetProvider
    {
        private const string PreferencesName = "spomusic_widget";
        private const string TitleKey = "title";
        private const string ArtistKey = "artist";
        private const string StatusKey = "status";

        public override void OnUpdate(Context context, AppWidgetManager appWidgetManager, int[] appWidgetIds)
        {
            foreach (var appWidgetId in appWidgetIds)
                UpdateAppWidget(context, appWidgetManager, appWidgetId);
        }

        internal static void UpdateNowPlaying(Context context, string? title, string? artist, bool isPlaying)
        {
            var prefs = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
            using (var editor = prefs?.Edit())
            {
                editor?.PutString(TitleKey, string.IsNullOrWhiteSpace(title) ? "Spomusic" : title);
                editor?.PutString(ArtistKey, string.IsNullOrWhiteSpace(artist) ? "Tu música local" : artist);
                editor?.PutString(StatusKey, isPlaying ? "Reproduciendo" : "Pausado");
                editor?.Apply();
            }

            var appWidgetManager = AppWidgetManager.GetInstance(context);
            var componentName = new ComponentName(context, Java.Lang.Class.FromType(typeof(SpomusicAppWidget)));
            var appWidgetIds = appWidgetManager.GetAppWidgetIds(componentName);
            foreach (var appWidgetId in appWidgetIds)
                UpdateAppWidget(context, appWidgetManager, appWidgetId);
        }

        private static void UpdateAppWidget(Context context, AppWidgetManager appWidgetManager, int appWidgetId)
        {
            var prefs = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
            var title = prefs?.GetString(TitleKey, "Spomusic") ?? "Spomusic";
            var artist = prefs?.GetString(ArtistKey, "Tu música local") ?? "Tu música local";
            var status = prefs?.GetString(StatusKey, "Listo") ?? "Listo";

            var views = new RemoteViews(context.PackageName, Resource.Layout.spomusic_widget);
            views.SetTextViewText(Resource.Id.widget_title, title);
            views.SetTextViewText(Resource.Id.widget_subtitle, artist);
            views.SetTextViewText(Resource.Id.widget_status, status);

            var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName);
            if (launchIntent != null)
            {
                launchIntent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
                var pendingIntent = PendingIntent.GetActivity(
                    context,
                    0,
                    launchIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                views.SetOnClickPendingIntent(Resource.Id.widget_root, pendingIntent);
            }

            appWidgetManager.UpdateAppWidget(appWidgetId, views);
        }
    }
}
