using Spomusic.Models;

#if ANDROID
using Android.Media;
using Java.Nio;
#endif

namespace Spomusic.Services
{
    public sealed class EmbeddedLyricAlignmentEngine : ILyricAlignmentEngine
    {
#if ANDROID
        private const int WindowMs = 40;
        private const int MaxSearchRadiusMs = 1800;
        private const int MinGapMs = 220;

        public async Task<LyricAlignmentResult> TryAlignAsync(SongItem song, IReadOnlyList<LyricLine> lines, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(song.Path) || lines.Count < 4)
                return LyricAlignmentResult.None;

            if (lines.Any(l => !l.HasExplicitTiming))
                return LyricAlignmentResult.None;

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var envelope = DecodeEnvelope(song.Path, cancellationToken);
                if (envelope.Count < 32)
                    return LyricAlignmentResult.None;

                var smooth = Smooth(envelope, radius: 2);
                var onset = BuildOnsetCurve(smooth);
                var activeThreshold = ComputeActiveThreshold(smooth);
                var onsetThreshold = ComputeOnsetThreshold(onset);
                var candidates = BuildCandidates(smooth, onset, activeThreshold, onsetThreshold);
                if (candidates.Count < 6)
                    return LyricAlignmentResult.None;

                var original = lines.OrderBy(l => l.Index).Select(l => (long)l.OriginalTime.TotalMilliseconds).ToArray();
                var aligned = new long[original.Length];
                var scores = new double[original.Length];
                long last = -MinGapMs;

                for (var i = 0; i < original.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var baseTime = original[i];
                    var prevGap = i > 0 ? Math.Max(400, original[i] - original[i - 1]) : 1400;
                    var nextGap = i + 1 < original.Length ? Math.Max(400, original[i + 1] - original[i]) : 1400;
                    var backRadius = Math.Min(MaxSearchRadiusMs, (int)(prevGap * 0.55) + 260);
                    var forwardRadius = Math.Min(MaxSearchRadiusMs, (int)(nextGap * 0.65) + 320);
                    var minTime = Math.Max(last + MinGapMs, baseTime - backRadius);
                    var maxTime = baseTime + forwardRadius;

                    var bestTime = Math.Max(baseTime, last + MinGapMs);
                    var bestScore = ScoreAtTime(bestTime, baseTime, smooth, onset, candidates, activeThreshold);

                    foreach (var candidate in candidates)
                    {
                        if (candidate.TimeMs < minTime || candidate.TimeMs > maxTime)
                            continue;

                        var score = ScoreCandidate(candidate, baseTime, activeThreshold, backRadius, forwardRadius);
                        if (score <= bestScore)
                            continue;

                        bestScore = score;
                        bestTime = candidate.TimeMs;
                    }

                    aligned[i] = bestTime;
                    scores[i] = bestScore;
                    last = bestTime;
                }

                var avgScore = scores.Average();
                var coverage = scores.Count(s => s >= 0.56d) / (double)scores.Length;
                var avgShift = original.Zip(aligned, (o, a) => Math.Abs(a - o)).Average();
                var stability = Math.Max(0, 1d - (avgShift / 2200d));
                var confidence = Math.Clamp((avgScore * 0.5d) + (coverage * 0.3d) + (stability * 0.2d), 0, 1);

                if (confidence < 0.74d)
                    return LyricAlignmentResult.None;

                return new LyricAlignmentResult
                {
                    ConfidenceScore = confidence,
                    LineTimingsMs = aligned,
                    Diagnostics = new LyricAlignmentDiagnostics
                    {
                        WindowMs = WindowMs,
                        FrameCount = smooth.Count,
                        CandidateCount = candidates.Count,
                        AverageShiftMs = avgShift,
                        CoverageScore = coverage,
                        AverageCandidateScore = avgScore
                    }
                };
            }, cancellationToken);
        }

        private static List<float> DecodeEnvelope(string path, CancellationToken cancellationToken)
        {
            using var extractor = new MediaExtractor();
            extractor.SetDataSource(path);

            var audioTrackIndex = -1;
            MediaFormat? format = null;
            for (var i = 0; i < extractor.TrackCount; i++)
            {
                var candidate = extractor.GetTrackFormat(i);
                var mime = candidate.GetString(MediaFormat.KeyMime);
                if (mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    audioTrackIndex = i;
                    format = candidate;
                    break;
                }
            }

            if (audioTrackIndex < 0 || format == null)
                return new List<float>();

            extractor.SelectTrack(audioTrackIndex);
            var mimeType = format.GetString(MediaFormat.KeyMime);
            if (string.IsNullOrWhiteSpace(mimeType))
                return new List<float>();

            using var codec = MediaCodec.CreateDecoderByType(mimeType);
            codec.Configure(format, null, null, 0);
            codec.Start();

            var outputInfo = new MediaCodec.BufferInfo();
            var builder = new EnvelopeBuilder(Math.Max(8000, format.GetInteger(MediaFormat.KeySampleRate)));
            var inputDone = false;
            var outputDone = false;

            while (!outputDone)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!inputDone)
                {
                    var inputIndex = codec.DequeueInputBuffer(10000);
                    if (inputIndex >= 0)
                    {
                        var inputBuffer = codec.GetInputBuffer(inputIndex);
                        if (inputBuffer == null)
                            break;

                        var sampleSize = extractor.ReadSampleData(inputBuffer, 0);
                        if (sampleSize < 0)
                        {
                            codec.QueueInputBuffer(inputIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                        }
                        else
                        {
                            codec.QueueInputBuffer(inputIndex, 0, sampleSize, extractor.SampleTime, 0);
                            extractor.Advance();
                        }
                    }
                }

                var outputIndex = codec.DequeueOutputBuffer(outputInfo, 10000);
                if (outputIndex == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    builder.UpdateFormat(codec.OutputFormat);
                    continue;
                }

                if (outputIndex < 0)
                    continue;

                var outputBuffer = codec.GetOutputBuffer(outputIndex);
                if (outputBuffer != null && outputInfo.Size > 0)
                    builder.Append(outputBuffer, outputInfo.Offset, outputInfo.Size);

                outputDone = (outputInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                codec.ReleaseOutputBuffer(outputIndex, false);
            }

            codec.Stop();
            return builder.Build();
        }

        private static List<float> Smooth(IReadOnlyList<float> values, int radius)
        {
            var result = new List<float>(values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                var start = Math.Max(0, i - radius);
                var end = Math.Min(values.Count - 1, i + radius);
                float sum = 0;
                for (var j = start; j <= end; j++)
                    sum += values[j];
                result.Add(sum / (end - start + 1));
            }

            return result;
        }

        private static List<float> BuildOnsetCurve(IReadOnlyList<float> smooth)
        {
            var onset = new List<float>(smooth.Count) { 0 };
            for (var i = 1; i < smooth.Count; i++)
                onset.Add(Math.Max(0, smooth[i] - smooth[i - 1]));
            return Normalize(onset);
        }

        private static float ComputeActiveThreshold(IReadOnlyList<float> values)
        {
            var avg = values.Average();
            var std = Math.Sqrt(values.Select(v => Math.Pow(v - avg, 2)).Average());
            return (float)Math.Max(0.06d, avg + (std * 0.35d));
        }

        private static float ComputeOnsetThreshold(IReadOnlyList<float> values)
        {
            var avg = values.Average();
            var std = Math.Sqrt(values.Select(v => Math.Pow(v - avg, 2)).Average());
            return (float)Math.Max(0.04d, avg + (std * 0.55d));
        }

        private static List<CandidatePoint> BuildCandidates(IReadOnlyList<float> smooth, IReadOnlyList<float> onset, float activeThreshold, float onsetThreshold)
        {
            var candidates = new List<CandidatePoint>();
            var lastWasActive = false;

            for (var i = 0; i < smooth.Count; i++)
            {
                var isActive = smooth[i] >= activeThreshold;
                if (isActive && !lastWasActive)
                {
                    candidates.Add(new CandidatePoint(i * WindowMs, smooth[i], onset[i], true));
                }

                if (onset[i] >= onsetThreshold)
                    candidates.Add(new CandidatePoint(i * WindowMs, smooth[i], onset[i], false));

                lastWasActive = isActive;
            }

            return candidates
                .OrderBy(c => c.TimeMs)
                .GroupBy(c => c.TimeMs / 40)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .ToList();
        }

        private static double ScoreCandidate(CandidatePoint candidate, long baseTime, float activeThreshold, int backRadius, int forwardRadius)
        {
            var distance = candidate.TimeMs - baseTime;
            var radius = distance < 0 ? Math.Max(300, backRadius) : Math.Max(300, forwardRadius);
            var proximity = Math.Max(0, 1d - (Math.Abs(distance) / (double)radius));
            var energy = Math.Clamp(candidate.Energy / Math.Max(0.0001f, activeThreshold * 1.6f), 0, 1);
            var onset = Math.Clamp(candidate.Onset * 1.25f, 0, 1);
            var boundaryBonus = candidate.IsRegionStart ? 0.12d : 0d;
            return (proximity * 0.45d) + (energy * 0.2d) + (onset * 0.23d) + boundaryBonus;
        }

        private static double ScoreAtTime(long timeMs, long baseTime, IReadOnlyList<float> smooth, IReadOnlyList<float> onset, IReadOnlyList<CandidatePoint> candidates, float activeThreshold)
        {
            var frame = (int)Math.Clamp(timeMs / WindowMs, 0, smooth.Count - 1);
            var proximity = Math.Max(0, 1d - (Math.Abs(timeMs - baseTime) / 1800d));
            var energy = Math.Clamp(smooth[frame] / Math.Max(0.0001f, activeThreshold * 1.6f), 0, 1);
            var onsetStrength = Math.Clamp(onset[frame] * 1.25f, 0, 1);
            return (proximity * 0.55d) + (energy * 0.25d) + (onsetStrength * 0.2d);
        }

        private static List<float> Normalize(IReadOnlyList<float> values)
        {
            var max = values.Count == 0 ? 0 : values.Max();
            if (max <= 0.0001f)
                return values.Select(_ => 0f).ToList();

            return values.Select(v => v / max).ToList();
        }

        private sealed record CandidatePoint(long TimeMs, float Energy, float Onset, bool IsRegionStart)
        {
            public double Score => Energy + Onset + (IsRegionStart ? 0.1d : 0d);
        }

        private sealed class EnvelopeBuilder
        {
            private readonly List<float> _rms = new();
            private int _sampleRate;
            private int _channelCount = 1;
            private bool _isFloat;
            private double _sumSquares;
            private int _windowSampleCount;
            private int _accumulatedSamples;

            public EnvelopeBuilder(int sampleRate)
            {
                _sampleRate = sampleRate;
                RecalculateWindow();
            }

            public void UpdateFormat(MediaFormat format)
            {
                if (format.ContainsKey(MediaFormat.KeySampleRate))
                    _sampleRate = format.GetInteger(MediaFormat.KeySampleRate);
                if (format.ContainsKey(MediaFormat.KeyChannelCount))
                    _channelCount = Math.Max(1, format.GetInteger(MediaFormat.KeyChannelCount));

                if (format.ContainsKey(MediaFormat.KeyPcmEncoding))
                {
                    var pcmEncoding = format.GetInteger(MediaFormat.KeyPcmEncoding);
                    _isFloat = pcmEncoding == (int)Encoding.PcmFloat;
                }

                RecalculateWindow();
            }

            public void Append(ByteBuffer buffer, int offset, int size)
            {
                var bytes = new byte[size];
                buffer.Position(offset);
                buffer.Limit(offset + size);
                buffer.Get(bytes, 0, size);

                if (_isFloat)
                    AppendFloat(bytes);
                else
                    AppendPcm16(bytes);
            }

            public List<float> Build()
            {
                FlushWindow();
                return Normalize(_rms);
            }

            private void AppendPcm16(byte[] bytes)
            {
                var frameSize = Math.Max(2, 2 * _channelCount);
                for (var i = 0; i + frameSize <= bytes.Length; i += frameSize)
                {
                    double mono = 0;
                    for (var ch = 0; ch < _channelCount; ch++)
                    {
                        var sampleOffset = i + (ch * 2);
                        short sample = BitConverter.ToInt16(bytes, sampleOffset);
                        mono += sample / 32768d;
                    }

                    mono /= _channelCount;
                    AddSample(mono);
                }
            }

            private void AppendFloat(byte[] bytes)
            {
                var frameSize = Math.Max(4, 4 * _channelCount);
                for (var i = 0; i + frameSize <= bytes.Length; i += frameSize)
                {
                    double mono = 0;
                    for (var ch = 0; ch < _channelCount; ch++)
                    {
                        var sampleOffset = i + (ch * 4);
                        mono += BitConverter.ToSingle(bytes, sampleOffset);
                    }

                    mono /= _channelCount;
                    AddSample(mono);
                }
            }

            private void AddSample(double sample)
            {
                _sumSquares += sample * sample;
                _accumulatedSamples++;
                if (_accumulatedSamples < _windowSampleCount)
                    return;

                FlushWindow();
            }

            private void FlushWindow()
            {
                if (_accumulatedSamples == 0)
                    return;

                _rms.Add((float)Math.Sqrt(_sumSquares / _accumulatedSamples));
                _sumSquares = 0;
                _accumulatedSamples = 0;
            }

            private void RecalculateWindow()
            {
                _windowSampleCount = Math.Max(1, (int)Math.Round(_sampleRate * (WindowMs / 1000d)));
            }
        }
#else
        public Task<LyricAlignmentResult> TryAlignAsync(SongItem song, IReadOnlyList<LyricLine> lines, CancellationToken cancellationToken = default)
            => Task.FromResult(LyricAlignmentResult.None);
#endif
    }
}
