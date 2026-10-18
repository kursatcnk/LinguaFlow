namespace LinguaFlow.Infrastructure;

/// <summary>Bir Whisper segmentinin neden çevrilmediği.</summary>
public enum FocusVerdict
{
    Accepted,
    Singing,
    LowConfidence,
    Background
}

/// <summary>
/// Arka plandaki şarkı ve müziği ana konuşmacıdan ayırır.
/// <para>
/// Ses akışından 10 ms'lik karelerde enerji ve perde (pitch) çıkarılır. Bir Whisper segmenti şu
/// durumlarda çevrilmez:
/// <list type="bullet">
/// <item>Şarkı: baskın seste perde uzun süre aynı notada kalıyor. Konuşmada perde sürekli kayar,
/// şarkıda notalar 250 ms'den uzun tutulur.</item>
/// <item>Düşük güven: Whisper metne kendisi de inanmıyor (müzik altında uydurulan söz, uzak konuşma).</item>
/// <item>Arka plan: segment, ana konuşmacının ses seviyesinin çok altında (fondaki şarkı, TV, kalabalık).</item>
/// </list>
/// </para>
/// Tüm üyeler iş parçacığı güvenlidir: ses yakalama ve tanıma farklı iş parçacıklarında çalışır.
/// </summary>
public sealed class SpeechFocusFilter
{
    public const int SampleRate = 16_000;
    private const int Hop = 160;          // 10 ms
    private const int FrameSize = 512;    // 32 ms: 70 Hz'lik sesin iki periyodu
    private const int PitchRate = SampleRate / 2;
    private const int MinLag = PitchRate / 450;
    private const int MaxLag = PitchRate / 70;
    private const int MaximumFrames = 60 * SampleRate / Hop;
    private const float SilenceRms = 0.0015f;

    private readonly object _gate = new();
    private readonly List<float> _pending = new(FrameSize * 4);
    private readonly List<FrameFeature> _frames = new(MaximumFrames);
    private readonly float[] _decimated = new float[FrameSize / 2];
    private long _firstFrame;
    private double? _mainLevelDb;
    private long _mainLevelSample;

    /// <summary>Uzun nota sayılması için gereken süre.</summary>
    public TimeSpan SustainedNote { get; init; } = TimeSpan.FromMilliseconds(260);

    /// <summary>Baskın seslendirilmiş karelerin bu oranı uzun notadaysa segment kesin şarkıdır.</summary>
    public double SingingRatio { get; init; } = 0.5;

    /// <summary>Bu orandan itibaren Whisper'ın güveni de düşükse segment şarkı sayılır.</summary>
    public double LikelySingingRatio { get; init; } = 0.3;

    public double MinimumProbability { get; init; } = 0.35;

    /// <summary>Ana konuşmacının bu kadar altındaki sesler arka plan sayılır.</summary>
    public double BackgroundMarginDb { get; init; } = 15;

    /// <summary>Ses düzeyi değişirse (kullanıcı sesi kıstı) ana konuşmacı seviyesi bu hızla gevşer.</summary>
    public double MainLevelDecayDbPerSecond { get; init; } = 1.5;

    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
            _frames.Clear();
            _firstFrame = 0;
            _mainLevelDb = null;
            _mainLevelSample = 0;
        }
    }

    /// <summary>16 kHz mono sesi akışın sonuna ekler ve tamamlanan kareleri çözümler.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            foreach (var sample in samples)
            {
                _pending.Add(sample);
            }

            while (_pending.Count >= FrameSize)
            {
                _frames.Add(Analyze(_pending));
                _pending.RemoveRange(0, Hop);
            }

            if (_frames.Count > MaximumFrames)
            {
                var overflow = _frames.Count - MaximumFrames;
                _frames.RemoveRange(0, overflow);
                _firstFrame += overflow;
            }
        }
    }

    /// <param name="startSample">Akış başından itibaren segment başlangıcı (örnek).</param>
    /// <param name="endSample">Akış başından itibaren segment sonu (örnek).</param>
    /// <param name="probability">Whisper'ın ortalama token olasılığı; bilinmiyorsa 0 veya altı.</param>
    public FocusVerdict Evaluate(long startSample, long endSample, double probability)
    {
        var stats = Measure(startSample, endSample);
        if (stats.Frames == 0)
        {
            return FocusVerdict.Accepted;
        }

        var knownProbability = probability > 0;
        if (stats.SustainedRatio >= SingingRatio ||
            (stats.SustainedRatio >= LikelySingingRatio && knownProbability && probability < 0.75))
        {
            return FocusVerdict.Singing;
        }

        if (knownProbability && probability < MinimumProbability)
        {
            return FocusVerdict.LowConfidence;
        }

        double? main;
        lock (_gate)
        {
            main = DecayedMainLevel(endSample);
        }

        return main is { } level && stats.LevelDb < level - BackgroundMarginDb
            ? FocusVerdict.Background
            : FocusVerdict.Accepted;
    }

    /// <summary>Kesinleşen (çevrilen) konuşma ana konuşmacının ses seviyesini günceller.</summary>
    public void ObserveMainSpeaker(long startSample, long endSample)
    {
        var stats = Measure(startSample, endSample);
        if (stats.Frames == 0 || double.IsNegativeInfinity(stats.LevelDb))
        {
            return;
        }

        lock (_gate)
        {
            var decayed = DecayedMainLevel(endSample);
            _mainLevelDb = decayed is { } current ? Math.Max(current, stats.LevelDb) : stats.LevelDb;
            _mainLevelSample = endSample;
        }
    }

    internal SegmentStats Measure(long startSample, long endSample)
    {
        lock (_gate)
        {
            // Kare i, [i·Hop, i·Hop + FrameSize) aralığını kapsar; merkezine göre seç.
            var first = Math.Max(_firstFrame, (startSample - FrameSize / 2 + Hop - 1) / Hop);
            var last = Math.Min(_firstFrame + _frames.Count - 1, (endSample - FrameSize / 2) / Hop);
            if (last < first)
            {
                return new SegmentStats(0, 0, 0, double.NegativeInfinity);
            }

            var count = (int)(last - first + 1);
            var offset = (int)(first - _firstFrame);
            var levels = new float[count];
            for (var index = 0; index < count; index++)
            {
                levels[index] = _frames[offset + index].Rms;
            }

            Array.Sort(levels);
            var loud = levels[(int)Math.Min(count - 1, Math.Floor(count * 0.9))];
            if (loud < SilenceRms)
            {
                return new SegmentStats(count, 0, 0, ToDb(loud));
            }

            // Yalnızca baskın sesi (konuşmacı ya da şarkıcı) değerlendir: aradaki fon müziği uzun
            // notalar içerse de konuşmanın şarkı sanılmasına yol açmasın.
            var dominantFloor = loud * 0.316f; // −10 dB
            var voiced = 0;
            var sustained = 0;
            var minimumRun = (int)Math.Ceiling(SustainedNote.TotalMilliseconds / 10);
            var runLength = 0;
            var runSum = 0d;
            var previousPitch = 0d;
            for (var index = 0; index <= count; index++)
            {
                var frame = index < count ? _frames[offset + index] : default;
                var isDominant = index < count && frame.Pitch > 0 && frame.Rms >= dominantFloor;
                if (isDominant)
                {
                    voiced++;
                }

                var continues = isDominant && runLength > 0 &&
                    Math.Abs(Semitones(frame.Pitch, previousPitch)) < 0.5 &&
                    Math.Abs(Semitones(frame.Pitch, runSum / runLength)) < 1.0;
                if (continues)
                {
                    runLength++;
                    runSum += frame.Pitch;
                }
                else
                {
                    if (runLength >= minimumRun)
                    {
                        sustained += runLength;
                    }

                    runLength = isDominant ? 1 : 0;
                    runSum = isDominant ? frame.Pitch : 0;
                }

                previousPitch = isDominant ? frame.Pitch : 0;
            }

            // Çok kısa segmentlerde karar verecek kadar kanıt yok.
            var ratio = voiced >= minimumRun ? sustained / (double)voiced : 0;
            return new SegmentStats(count, voiced, ratio, ToDb(loud));
        }
    }

    private double? DecayedMainLevel(long atSample)
    {
        if (_mainLevelDb is not { } level)
        {
            return null;
        }

        var elapsed = Math.Max(0, atSample - _mainLevelSample) / (double)SampleRate;
        return level - elapsed * MainLevelDecayDbPerSecond;
    }

    private FrameFeature Analyze(List<float> pending)
    {
        double energy = 0;
        for (var index = 0; index < FrameSize; index++)
        {
            energy += pending[index] * pending[index];
        }

        var rms = (float)Math.Sqrt(energy / FrameSize);
        if (rms < SilenceRms)
        {
            return new FrameFeature(rms, 0);
        }

        // Perde için 8 kHz yeterli ve dört kat ucuz; ikili ortalama kaba bir alçak geçiren süzgeçtir.
        double mean = 0;
        for (var index = 0; index < _decimated.Length; index++)
        {
            var value = (pending[index * 2] + pending[(index * 2) + 1]) * 0.5f;
            _decimated[index] = value;
            mean += value;
        }

        mean /= _decimated.Length;
        for (var index = 0; index < _decimated.Length; index++)
        {
            _decimated[index] -= (float)mean;
        }

        return new FrameFeature(rms, (float)EstimatePitch(_decimated));
    }

    /// <summary>Normalize öz-ilinti ile temel frekans; seslendirilmemiş karede 0.</summary>
    internal static double EstimatePitch(ReadOnlySpan<float> frame)
    {
        var length = frame.Length;
        var maxLag = Math.Min(MaxLag, length / 2);
        Span<double> scores = stackalloc double[maxLag + 2];
        var best = 0d;
        for (var lag = MinLag; lag <= maxLag + 1 && lag < length; lag++)
        {
            double cross = 0, head = 0, tail = 0;
            for (var index = 0; index + lag < length; index++)
            {
                var a = frame[index];
                var b = frame[index + lag];
                cross += a * b;
                head += a * a;
                tail += b * b;
            }

            var score = head > 0 && tail > 0 ? cross / Math.Sqrt(head * tail) : 0;
            scores[lag] = score;
            if (lag <= maxLag)
            {
                best = Math.Max(best, score);
            }
        }

        if (best < 0.6)
        {
            return 0;
        }

        // Alt harmoniğe (oktav hatası) kaymamak için en iyi skora yakın ilk tepeyi seç.
        for (var lag = MinLag + 1; lag <= maxLag; lag++)
        {
            var score = scores[lag];
            if (score >= best * 0.9 && score >= scores[lag - 1] && score >= scores[lag + 1])
            {
                var left = scores[lag - 1];
                var right = scores[lag + 1];
                var curvature = left - (2 * score) + right;
                var shift = curvature < 0 ? 0.5 * (left - right) / curvature : 0;
                return PitchRate / (lag + Math.Clamp(shift, -0.5, 0.5));
            }
        }

        return 0;
    }

    private static double Semitones(double pitch, double reference) =>
        pitch > 0 && reference > 0 ? 12 * Math.Log2(pitch / reference) : double.PositiveInfinity;

    private static double ToDb(float rms) => rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;

    private readonly record struct FrameFeature(float Rms, float Pitch);

    internal readonly record struct SegmentStats(int Frames, int VoicedFrames, double SustainedRatio, double LevelDb);
}
