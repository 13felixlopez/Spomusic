using Microsoft.Maui.ApplicationModel;

#if ANDROID
using Android;
using Android.OS;
#endif

namespace Spomusic.Services
{
    public sealed class AudioLibraryPermission : Permissions.BasePlatformPermission
    {
#if ANDROID
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                ? new[] { (Manifest.Permission.ReadMediaAudio, true) }
                : new[] { (Manifest.Permission.ReadExternalStorage, true) };
#endif
    }
}
