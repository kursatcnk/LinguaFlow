using System.Buffers.Binary;
using LinguaFlow.Core;
using LinguaFlow.Infrastructure;

namespace LinguaFlow.UnitTests;

public sealed class AudioProcessingServiceTests
{
    [Fact]
    public void Converts48KhzFloatStereoTo16KhzPcm16Mono()
    {
        var service = new AudioProcessingService();
        var source = new byte[480 * 2 * sizeof(float)];
        for (var frame = 0; frame < 480; frame++)
        {
            WriteFloat(source, (frame * 2) * 4, 0.5f);
            WriteFloat(source, ((frame * 2) + 1) * 4, -0.25f);
        }

        var result = service.Process(new AudioFrame(source, new AudioFormat(48_000, 32, 2, true), DateTimeOffset.UtcNow, 1));

        Assert.NotNull(result);
        Assert.Equal(320, result.Pcm16Mono.Length);
        var firstSample = BinaryPrimitives.ReadInt16LittleEndian(result.Pcm16Mono);
        Assert.InRange(firstSample, 4_095, 4_097);
    }

    [Fact]
    public void PreservesIncompleteFrameUntilNextChunk()
    {
        var service = new AudioProcessingService();
        var format = new AudioFormat(16_000, 16, 1, false);

        Assert.Null(service.Process(new AudioFrame([0x00], format, DateTimeOffset.UtcNow, 1)));
        var result = service.Process(new AudioFrame([0x40, 0x00, 0x40], format, DateTimeOffset.UtcNow, 2));

        Assert.NotNull(result);
        Assert.Equal(2, result.Pcm16Mono.Length);
    }

    [Fact]
    public void RejectsUnsupportedBitDepth()
    {
        var service = new AudioProcessingService();
        var frame = new AudioFrame([0, 0], new AudioFormat(44_100, 8, 2, false), DateTimeOffset.UtcNow, 1);

        Assert.Throws<NotSupportedException>(() => service.Process(frame));
    }

    private static void WriteFloat(byte[] buffer, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
}
