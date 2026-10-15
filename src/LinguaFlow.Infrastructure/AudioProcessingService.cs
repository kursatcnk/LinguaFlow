using System.Buffers.Binary;
using LinguaFlow.Core;

namespace LinguaFlow.Infrastructure;

public sealed class AudioProcessingService : IAudioProcessingService
{
    private const int TargetSampleRate = 16_000;
    private readonly List<float> _samples = new(16_384);
    private byte[] _remainder = [];
    private AudioFormat? _format;
    private double _sourcePosition;

    public void Reset()
    {
        _samples.Clear();
        _remainder = [];
        _format = null;
        _sourcePosition = 0;
    }

    public NormalizedAudioChunk? Process(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateFormat(frame.Format);

        if (_format != frame.Format)
        {
            Reset();
            _format = frame.Format;
        }

        var data = CombineRemainder(frame.Data);
        var blockAlign = frame.Format.BlockAlign;
        var alignedLength = data.Length - data.Length % blockAlign;
        if (alignedLength == 0)
        {
            _remainder = data;
            return null;
        }

        DecodeToMono(data.AsSpan(0, alignedLength), frame.Format, _samples);
        _remainder = data.AsSpan(alignedLength).ToArray();

        var step = (double)frame.Format.SampleRate / TargetSampleRate;
        var output = new List<short>(Math.Max(256, (int)(_samples.Count / step)));
        while (_sourcePosition + 1 < _samples.Count)
        {
            var leftIndex = (int)_sourcePosition;
            var fraction = _sourcePosition - leftIndex;
            var sample = _samples[leftIndex] + ((_samples[leftIndex + 1] - _samples[leftIndex]) * fraction);
            output.Add(FloatToPcm16(sample));
            _sourcePosition += step;
        }

        var consumed = Math.Min((int)_sourcePosition, Math.Max(0, _samples.Count - 1));
        if (consumed > 0)
        {
            _samples.RemoveRange(0, consumed);
            _sourcePosition -= consumed;
        }

        if (output.Count == 0)
        {
            return null;
        }

        var pcm = GC.AllocateUninitializedArray<byte>(output.Count * sizeof(short));
        for (var index = 0; index < output.Count; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2, 2), output[index]);
        }

        return new NormalizedAudioChunk(pcm, frame.CapturedAt, frame.Sequence);
    }

    private byte[] CombineRemainder(byte[] current)
    {
        if (_remainder.Length == 0)
        {
            return current;
        }

        var combined = GC.AllocateUninitializedArray<byte>(_remainder.Length + current.Length);
        _remainder.CopyTo(combined, 0);
        current.CopyTo(combined, _remainder.Length);
        return combined;
    }

    private static void DecodeToMono(ReadOnlySpan<byte> data, AudioFormat format, List<float> destination)
    {
        for (var offset = 0; offset < data.Length; offset += format.BlockAlign)
        {
            double sum = 0;
            var bytesPerSample = format.BitsPerSample / 8;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sample = data.Slice(offset + channel * bytesPerSample, bytesPerSample);
                sum += DecodeSample(sample, format);
            }

            destination.Add((float)(sum / format.Channels));
        }
    }

    private static float DecodeSample(ReadOnlySpan<byte> sample, AudioFormat format) =>
        (format.BitsPerSample, format.IsIeeeFloat) switch
        {
            (32, true) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample)),
            (16, false) => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
            (24, false) => DecodePcm24(sample) / 8_388_608f,
            (32, false) => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2_147_483_648f,
            _ => throw new NotSupportedException($"Desteklenmeyen PCM biçimi: {format.BitsPerSample}-bit")
        };

    private static int DecodePcm24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | sample[1] << 8 | sample[2] << 16;
        return (value & 0x0080_0000) != 0 ? value | unchecked((int)0xFF00_0000) : value;
    }

    private static short FloatToPcm16(double sample)
    {
        var clamped = Math.Clamp(sample, -1d, 1d);
        return clamped <= -1d ? short.MinValue : (short)Math.Round(clamped * short.MaxValue);
    }

    private static void ValidateFormat(AudioFormat format)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0 || format.BlockAlign <= 0)
        {
            throw new ArgumentException("Geçersiz ses biçimi.", nameof(format));
        }

        if (format.BitsPerSample is not (16 or 24 or 32))
        {
            throw new NotSupportedException($"{format.BitsPerSample}-bit ses desteklenmiyor.");
        }
    }
}
