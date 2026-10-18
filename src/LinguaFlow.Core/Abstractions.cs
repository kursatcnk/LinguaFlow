using System.Threading.Channels;

namespace LinguaFlow.Core;

public interface IAudioCaptureService : IAsyncDisposable
{
    ChannelReader<AudioFrame> Frames { get; }
    AudioFormat? CurrentFormat { get; }
    long CapturedFrames { get; }
    long DroppedFrames { get; }
    int BufferedFrames { get; }
    event EventHandler<string>? CaptureFaulted;

    IReadOnlyList<AudioDevice> GetPlaybackDevices();
    Task StartAsync(string deviceId, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IAudioProcessingService
{
    void Reset();
    NormalizedAudioChunk? Process(AudioFrame frame);
}

public interface IRealtimeTranslationProvider : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    bool IsDemo { get; }
    ProviderCapabilities Capabilities { get; }
    event EventHandler<TranslationEvent>? TranslationReceived;
    event EventHandler<ProviderStateChange>? StateChanged;

    Task StartAsync(TranslationSessionOptions options, CancellationToken cancellationToken);
    ValueTask WriteAudioAsync(NormalizedAudioChunk chunk, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface ITranscriptProcessor
{
    event EventHandler<TranscriptSegment>? SegmentChanged;
    IReadOnlyList<TranscriptSegment> FinalSegments { get; }
    TranscriptSegment? PartialSegment { get; }
    void Process(TranslationEvent translationEvent);
    void Reset();
}

public interface ITranslationSessionService : IAsyncDisposable
{
    TranslationStatus Status { get; }
    SessionDiagnostics Diagnostics { get; }
    event EventHandler<TranslationStatus>? StatusChanged;
    event EventHandler<ProviderStateChange>? ProviderStateChanged;
    event EventHandler<SessionDiagnostics>? DiagnosticsChanged;

    Task StartAsync(TranslationSessionOptions options, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface ISettingsService
{
    Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);
}

public interface ITextTranslator
{
    string Id { get; }
    bool CanTranslate(string sourceLanguage, string targetLanguage);
    Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken);
}

/// <summary>
/// Önceki cümleyi bağlam olarak kullanıp yalnızca yeni cümlenin çevirisini döndürebilen çevirmenler.
/// Bağlam; zamir, hitap, cinsiyet ve terim seçimini cümleler arasında tutarlı kılar.
/// </summary>
public interface IContextualTextTranslator : ITextTranslator
{
    Task<string> TranslateAsync(
        string text,
        string? context,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}

/// <summary>Ağır modelleri oturum başlamadan önce belleğe/GPU'ya yükleyebilen sağlayıcılar.</summary>
public interface IWarmupCapable
{
    bool IsWarm { get; }
    Task WarmUpAsync(CancellationToken cancellationToken);
}

public interface ILocalModelManager
{
    string ModelDirectory { get; set; }
    IReadOnlyList<WhisperModelInfo> AvailableWhisperModels { get; }
    WhisperModelInfo SelectedWhisperModel { get; set; }
    LocalModelStatus GetStatus();
    Task DownloadModelsAsync(
        IProgress<LocalModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
