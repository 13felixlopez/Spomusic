namespace Spomusic.Services
{
    public interface IAppUpdateService
    {
        Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);
        Task<bool> InstallUpdateAsync(AppUpdateInfo updateInfo, CancellationToken cancellationToken = default);
    }
}
