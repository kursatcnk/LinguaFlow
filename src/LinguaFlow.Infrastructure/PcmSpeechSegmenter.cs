namespace LinguaFlow.Infrastructure;

public sealed record SpeechUtterance(byte[] Pcm16Mono, TimeSpan Duration);

public sealed class PcmSpeechSegmenter
{
    private const int SampleRate = 16_000;
    private const int BytesPerSample = 2;
    private readonly List<byte> _utterance = [];
    private readonly Queue<byte[]> _preRoll = [];
    private readonly double _speechThreshold;
    private readonly TimeSpan _silenceToComplete;
    private readonly TimeSpan _maximumDuration;
    private readonly TimeSpan _minimumVoicedDuration;
    private int _preRollBytes;
    private long _silentSamples;
    private long _voicedSamples;
    private bool _speaking;

    public PcmSpeechSegmenter(
        double speechThreshold = 0.012,
        TimeSpan? silenceToComplete = null,
        TimeSpan? maximumDuration = null,
        TimeSpan? minimumVoicedDuration = null)
    {
        _speechThreshold = speechThreshold;
        _silenceToComplete = silenceToComplete ?? TimeSpan.FromMilliseconds(650);
        _maximumDuration = maximumDuration ?? TimeSpan.FromSeconds(12);
        _minimumVoicedDuration = minimumVoicedDuration ?? TimeSpan.FromMilliseconds(350);
    }

    public SpeechUtterance? Push(ReadOnlySpan<byte> pcm16Mono)
    {
        if (pcm16Mono.Length < BytesPerSample)
        {
            return null;
        }

        var alignedLength = pcm16Mono.Length - (pcm16Mono.Length % BytesPerSample);
        var chunk = pcm16Mono[..alignedLength];
        var sampleCount = alignedLength / BytesPerSample;
        var speech = CalculateRms(chunk) >= _speechThreshold;

        if (!_speaking)
        {
            if (!speech)
            {
                AddPreRoll(chunk.ToArray());
                return null;
            }

            _speaking = true;
            while (_preRoll.TryDequeue(out var buffered))
            {
                _utterance.AddRange(buffered);
            }

            _preRollBytes = 0;
        }

        _utterance.AddRange(chunk.ToArray());
        if (speech)
        {
            _voicedSamples += sampleCount;
            _silentSamples = 0;
        }
        else
        {
            _silentSamples += sampleCount;
        }

        var duration = SamplesToTime(_utterance.Count / BytesPerSample);
        if (SamplesToTime(_silentSamples) >= _silenceToComplete || duration >= _maximumDuration)
        {
            return Complete();
        }

        return null;
    }

    public SpeechUtterance? Flush() => _speaking ? Complete() : null;

    public void Reset()
    {
        _utterance.Clear();
        _preRoll.Clear();
        _preRollBytes = 0;
        _silentSamples = 0;
        _voicedSamples = 0;
        _speaking = false;
    }

    private SpeechUtterance? Complete()
    {
        var voicedDuration = SamplesToTime(_voicedSamples);
        var result = voicedDuration >= _minimumVoicedDuration
            ? new SpeechUtterance(_utterance.ToArray(), SamplesToTime(_utterance.Count / BytesPerSample))
            : null;
        Reset();
        return result;
    }

    private void AddPreRoll(byte[] chunk)
    {
        const int maximumPreRollBytes = SampleRate * BytesPerSample / 4;
        _preRoll.Enqueue(chunk);
        _preRollBytes += chunk.Length;
        while (_preRollBytes > maximumPreRollBytes && _preRoll.TryDequeue(out var removed))
        {
            _preRollBytes -= removed.Length;
        }
    }

    private static double CalculateRms(ReadOnlySpan<byte> bytes)
    {
        double sum = 0;
        var samples = bytes.Length / BytesPerSample;
        for (var index = 0; index < bytes.Length; index += BytesPerSample)
        {
            var sample = (short)(bytes[index] | (bytes[index + 1] << 8));
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / samples);
    }

    private static TimeSpan SamplesToTime(long samples) => TimeSpan.FromSeconds(samples / (double)SampleRate);
}
