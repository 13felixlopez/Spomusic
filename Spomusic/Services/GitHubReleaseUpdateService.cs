using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;

#if ANDROID
using Android.Content;
using Android.Content.PM;
using Android.Provider;
using Microsoft.Maui.Storage;
#endif

namespace Spomusic.Services
{
    public sealed class GitHubReleaseUpdateService : IAppUpdateService
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/13felixlopez/Spomusic/releases/latest";

        public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
#if ANDROID
            using var client = CreateHttpClient();
            using var response = await client.GetAsync(LatestReleaseApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var releaseVersion = ParseVersion(
                root.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() :
                root.TryGetProperty("name", out var releaseName) ? releaseName.GetString() :
                null);

            var currentVersion = ParseVersion(AppInfo.Current.VersionString);
            if (releaseVersion <= currentVersion)
                return null;

            string? apkDownloadUrl = null;
            string? assetName = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                        continue;

                    if (!name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        continue;

                    assetName = name;
                    apkDownloadUrl = url;
                    break;
                }
            }

            return new AppUpdateInfo
            {
                VersionLabel = root.TryGetProperty("tag_name", out var versionLabel) ? versionLabel.GetString() ?? releaseVersion.ToString() : releaseVersion.ToString(),
                ParsedVersion = releaseVersion,
                ReleasePageUrl = root.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() ?? "https://github.com/13felixlopez/Spomusic/releases/latest" : "https://github.com/13felixlopez/Spomusic/releases/latest",
                ApkDownloadUrl = apkDownloadUrl,
                AssetName = assetName,
                ReleaseNotes = root.TryGetProperty("body", out var body) ? body.GetString() : null
            };
#else
            await Task.CompletedTask;
            return null;
#endif
        }

        public async Task<bool> InstallUpdateAsync(AppUpdateInfo updateInfo, CancellationToken cancellationToken = default)
        {
#if ANDROID
            var context = Platform.AppContext;
            if (string.IsNullOrWhiteSpace(updateInfo.ApkDownloadUrl))
            {
                await Launcher.Default.OpenAsync(updateInfo.ReleasePageUrl);
                return false;
            }

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O &&
                !context.PackageManager!.CanRequestPackageInstalls())
            {
                var settingsIntent = new Intent(Settings.ActionManageUnknownAppSources,
                    global::Android.Net.Uri.Parse($"package:{context.PackageName}"));
                settingsIntent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(settingsIntent);
                return false;
            }

            var fileName = string.IsNullOrWhiteSpace(updateInfo.AssetName)
                ? $"Spomusic-{updateInfo.VersionLabel}.apk"
                : updateInfo.AssetName!;

            var apkPath = Path.Combine(FileSystem.CacheDirectory, fileName);

            using (var client = CreateHttpClient())
            using (var response = await client.GetAsync(updateInfo.ApkDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = System.IO.File.Create(apkPath);
                await httpStream.CopyToAsync(fileStream, cancellationToken);
            }

            var apkFile = new Java.IO.File(apkPath);
            var apkUri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, $"{context.PackageName}.fileprovider", apkFile);
            var installIntent = new Intent(Intent.ActionView);
            installIntent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
            installIntent.AddFlags(ActivityFlags.NewTask);
            installIntent.AddFlags(ActivityFlags.GrantReadUriPermission);
            context.StartActivity(installIntent);
            return true;
#else
            await Task.CompletedTask;
            return false;
#endif
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Spomusic", AppInfo.Current.VersionString));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        private static Version ParseVersion(string? rawVersion)
        {
            if (string.IsNullOrWhiteSpace(rawVersion))
                return new Version(0, 0);

            var trimmed = rawVersion.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[1..];

            var numericPart = new string(trimmed
                .TakeWhile(c => char.IsDigit(c) || c == '.')
                .ToArray());

            if (string.IsNullOrWhiteSpace(numericPart))
                return new Version(0, 0);

            if (Version.TryParse(numericPart, out var parsed))
                return parsed;

            if (int.TryParse(numericPart, out var major))
                return new Version(major, 0);

            return new Version(0, 0);
        }
    }
}
