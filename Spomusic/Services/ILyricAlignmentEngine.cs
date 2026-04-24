using Spomusic.Models;

namespace Spomusic.Services
{
    public interface ILyricAlignmentEngine
    {
        Task<LyricAlignmentResult> TryAlignAsync(SongItem song, IReadOnlyList<LyricLine> lines, CancellationToken cancellationToken = default);
    }
}
