namespace Spomusic.Services
{
    public sealed class AppUpdateInfo
    {
        public required string VersionLabel { get; init; }
        public required string ReleasePageUrl { get; init; }
        public string? ApkDownloadUrl { get; init; }
        public string? AssetName { get; init; }
        public string? ReleaseNotes { get; init; }
        public Version ParsedVersion { get; init; } = new(0, 0);
    }
}
