namespace Spomusic.Services
{
    public sealed class LyricAlignmentDiagnostics
    {
        public int WindowMs { get; init; }
        public int FrameCount { get; init; }
        public int CandidateCount { get; init; }
        public double AverageShiftMs { get; init; }
        public double CoverageScore { get; init; }
        public double AverageCandidateScore { get; init; }
    }

    public sealed class LyricAlignmentResult
    {
        public static LyricAlignmentResult None { get; } = new()
        {
            ConfidenceScore = 0,
            LineTimingsMs = Array.Empty<long>(),
            Diagnostics = new LyricAlignmentDiagnostics()
        };

        public double ConfidenceScore { get; init; }
        public IReadOnlyList<long> LineTimingsMs { get; init; } = Array.Empty<long>();
        public LyricAlignmentDiagnostics Diagnostics { get; init; } = new();
        public bool HasUsableAlignment => LineTimingsMs.Count > 0;
    }
}
