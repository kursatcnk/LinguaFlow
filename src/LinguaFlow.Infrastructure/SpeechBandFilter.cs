namespace LinguaFlow.Infrastructure;

/// <summary>
/// Konuşma bandının altını (bas gitar, davul, uğultu) kesen 2. derece Butterworth yüksek geçiren süzgeç.
/// İnsan sesinin temel frekansı ~85 Hz'den başlar; altındaki güçlü müzik enerjisi Whisper'ın
/// mel spektrumunda konuşmayı maskeler.
/// </summary>
public sealed class SpeechBandFilter
{
    private readonly double _b0;
    private readonly double _b1;
    private readonly double _b2;
    private readonly double _a1;
    private readonly double _a2;
    private double _x1;
    private double _x2;
    private double _y1;
    private double _y2;

    public SpeechBandFilter(int sampleRate = 16_000, double cutoffHz = 90)
    {
        var omega = 2 * Math.PI * cutoffHz / sampleRate;
        var cos = Math.Cos(omega);
        var alpha = Math.Sin(omega) / (2 * Math.Sqrt(0.5));
        var a0 = 1 + alpha;
        _b0 = (1 + cos) / 2 / a0;
        _b1 = -(1 + cos) / a0;
        _b2 = _b0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;

    public float Process(float sample)
    {
        var output = (_b0 * sample) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);
        _x2 = _x1;
        _x1 = sample;
        _y2 = _y1;
        _y1 = output;
        return (float)output;
    }
}
