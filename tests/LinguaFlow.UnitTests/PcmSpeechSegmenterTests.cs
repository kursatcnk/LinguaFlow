using System.Buffers.Binary;
using LinguaFlow.Infrastructure;

namespace LinguaFlow.UnitTests;

public sealed class PcmSpeechSegmenterTests
{
    [Fact]
    public void CompletesUtteranceAfterTrailingSilence()
    {
        var segmenter = new PcmSpeechSegmenter();

        Assert.Null(segmenter.Push(CreateTone(TimeSpan.FromMilliseconds(500))));
        var utterance = segmenter.Push(CreateSilence(TimeSpan.FromMilliseconds(700)));

        Assert.NotNull(utterance);
        Assert.True(utterance.Duration >= TimeSpan.FromSeconds(1.1));
        Assert.NotEmpty(utterance.Pcm16Mono);
    }

    [Fact]
    public void IgnoresVeryShortNoiseBurst()
    {
        var segmenter = new PcmSpeechSegmenter();

        Assert.Null(segmenter.Push(CreateTone(TimeSpan.FromMilliseconds(100))));
        var utterance = segmenter.Push(CreateSilence(TimeSpan.FromMilliseconds(700)));

        Assert.Null(utterance);
    }

    [Fact]
    public void FlushReturnsActiveUtterance()
    {
        var segmenter = new PcmSpeechSegmenter(minimumVoicedDuration: TimeSpan.FromMilliseconds(100));
        Assert.Null(segmenter.Push(CreateTone(TimeSpan.FromMilliseconds(300))));

        var utterance = segmenter.Flush();

        Assert.NotNull(utterance);
        Assert.True(utterance.Duration >= TimeSpan.FromMilliseconds(290));
    }

    private static byte[] CreateTone(TimeSpan duration)
    {
        var samples = (int)(16_000 * duration.TotalSeconds);
        var result = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
        {
            var value = (short)(Math.Sin(index * 2 * Math.PI * 440 / 16_000) * 8_000);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(index * 2, 2), value);
        }

        return result;
    }

    private static byte[] CreateSilence(TimeSpan duration) => new byte[(int)(16_000 * duration.TotalSeconds) * 2];
}
