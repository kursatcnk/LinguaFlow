using System.Threading.Channels;
using LinguaFlow.Application;
using LinguaFlow.Core;
using LinguaFlow.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinguaFlow.IntegrationTests;

public sealed class StreamingPipelineTests
{
    [Fact]
    public async Task MockProviderReceivesNormalizedAudioAndProducesFinalTranscript()
    {
        await using var capture = new FakeCaptureService();
        var transcript = new TranscriptProcessor();
        await using var provider = new MockTranslationProvider();
        await using var session = new TranslationSessionService(
            capture,
            new AudioProcessingService(),
            transcript,
            [provider],
            NullLogger<TranslationSessionService>.Instance);
        var finalReceived = new TaskCompletionSource<TranscriptSegment>(TaskCreationOptions.RunContinuationsAsynchronously);
        transcript.SegmentChanged += (_, segment) =>
        {
            if (segment.IsFinal)
            {
                finalReceived.TrySetResult(segment);
            }
        };

        await session.StartAsync(new TranslationSessionOptions(
            FakeCaptureService.Device.Id,
            "en-US",
            "tr",
            TranslationMode.Fast,
            "mock",
            null,
            null), CancellationToken.None);

        capture.Emit(CreateFloatStereoFrame(96_000));
        var result = await finalReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.StopAsync(CancellationToken.None);

        Assert.True(result.IsFinal);
        Assert.Contains("LinguaFlow", result.TranslatedText, StringComparison.OrdinalIgnoreCase);
        Assert.True(session.Diagnostics.AudioBytesSent > 0);
    }

    private static AudioFrame CreateFloatStereoFrame(int frameCount)
    {
        var data = new byte[frameCount * 2 * sizeof(float)];
        return new AudioFrame(data, new AudioFormat(48_000, 32, 2, true), DateTimeOffset.UtcNow, 1);
    }

    private sealed class FakeCaptureService : IAudioCaptureService
    {
        private readonly Channel<AudioFrame> _channel = Channel.CreateBounded<AudioFrame>(8);
        public static AudioDevice Device { get; } = new("fake", "Fake output", true);
        public ChannelReader<AudioFrame> Frames => _channel.Reader;
        public AudioFormat? CurrentFormat { get; private set; }
        public long CapturedFrames { get; private set; }
        public long DroppedFrames => 0;
        public int BufferedFrames => _channel.Reader.Count;
        public event EventHandler<string>? CaptureFaulted;

        public IReadOnlyList<AudioDevice> GetPlaybackDevices() => [Device];

        public Task StartAsync(string deviceId, CancellationToken cancellationToken)
        {
            CurrentFormat = new AudioFormat(48_000, 32, 2, true);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _channel.Writer.TryComplete();
            CurrentFormat = null;
            return Task.CompletedTask;
        }

        public void Emit(AudioFrame frame)
        {
            if (!_channel.Writer.TryWrite(frame))
            {
                CaptureFaulted?.Invoke(this, "Fake buffer full");
                return;
            }

            CapturedFrames++;
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
