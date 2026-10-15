using LinguaFlow.Core;

namespace LinguaFlow.Infrastructure;

public sealed class MockTranslationProvider : IRealtimeTranslationProvider
{
    private static readonly (string Original, string Turkish)[] Samples =
    [
        ("Hello everyone, today we are testing LinguaFlow.", "Herkese merhaba, bugün LinguaFlow'u test ediyoruz."),
        ("The subtitle line is updated while the speaker is talking.", "Konuşmacı konuşurken altyazı satırı güncellenir."),
        ("This is demo data and not a real translation.", "Bu demo verisidir ve gerçek bir çeviri değildir.")
    ];

    private string _sessionId = string.Empty;
    private long _bytesReceived;
    private int _sampleIndex;
    private int _stage;
    private bool _started;

    public string Id => "mock";
    public string DisplayName => "Demo Mode (Sahte Veri)";
    public bool IsDemo => true;
    public ProviderCapabilities Capabilities { get; } = new(true, true, false, false);

    public event EventHandler<TranslationEvent>? TranslationReceived;
    public event EventHandler<ProviderStateChange>? StateChanged;

    public Task StartAsync(TranslationSessionOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sessionId = Guid.NewGuid().ToString("N");
        _bytesReceived = 0;
        _sampleIndex = 0;
        _stage = 0;
        _started = true;
        StateChanged?.Invoke(this, new ProviderStateChange(
            ProviderConnectionState.Connected,
            "DEMO MODE — gösterilen metinler gerçek çeviri değildir."));
        return Task.CompletedTask;
    }

    public ValueTask WriteAudioAsync(NormalizedAudioChunk chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started)
        {
            throw new InvalidOperationException("Demo sağlayıcısı başlatılmadı.");
        }

        _bytesReceived += chunk.Pcm16Mono.Length;
        const long bytesPerStage = 24_000;
        var expectedStage = (int)(_bytesReceived / bytesPerStage);
        while (_stage < expectedStage)
        {
            EmitNext();
        }

        return ValueTask.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        StateChanged?.Invoke(this, new ProviderStateChange(ProviderConnectionState.Disconnected, "Demo modu durdu."));
        return Task.CompletedTask;
    }

    private void EmitNext()
    {
        var sample = Samples[_sampleIndex % Samples.Length];
        var segmentId = $"demo-{_sampleIndex}";
        var final = _stage % 2 == 1;
        var original = final ? sample.Original : sample.Original[..Math.Max(1, sample.Original.Length / 2)];
        var translated = final ? sample.Turkish : sample.Turkish[..Math.Max(1, sample.Turkish.Length / 2)];

        TranslationReceived?.Invoke(this, new TranslationEvent(
            _sessionId,
            segmentId,
            original,
            translated,
            final,
            TimeSpan.FromSeconds(_sampleIndex * 3),
            TimeSpan.FromSeconds(3),
            DateTimeOffset.UtcNow));

        _stage++;
        if (final)
        {
            _sampleIndex++;
        }
    }

    public ValueTask DisposeAsync()
    {
        _started = false;
        return ValueTask.CompletedTask;
    }
}
