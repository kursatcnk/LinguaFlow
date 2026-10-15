namespace LinguaFlow.Core;

public enum TranslationMode
{
    Fast,
    Accuracy
}

public enum TranslationStatus
{
    Idle,
    Starting,
    Listening,
    Reconnecting,
    Stopping,
    Faulted
}

public enum ProviderConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Recovering,
    Error
}

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

public sealed record AudioFormat(int SampleRate, int BitsPerSample, int Channels, bool IsIeeeFloat)
{
    public int BlockAlign => checked((BitsPerSample / 8) * Channels);
}

public sealed record AudioFrame(
    byte[] Data,
    AudioFormat Format,
    DateTimeOffset CapturedAt,
    long Sequence);

public sealed record NormalizedAudioChunk(
    byte[] Pcm16Mono,
    DateTimeOffset CapturedAt,
    long Sequence)
{
    public static readonly AudioFormat Format = new(16_000, 16, 1, false);
}

public sealed record ProviderCapabilities(
    bool SupportsPartialResults,
    bool SupportsFinalResults,
    bool SupportsGlossary,
    bool SupportsAutomaticLanguageDetection);

public sealed record TranslationEvent(
    string SessionId,
    string SegmentId,
    string OriginalText,
    string TranslatedText,
    bool IsFinal,
    TimeSpan Offset,
    TimeSpan Duration,
    DateTimeOffset ReceivedAt);

public sealed record TranscriptSegment(
    string SessionId,
    string SegmentId,
    string OriginalText,
    string TranslatedText,
    bool IsFinal,
    TimeSpan Offset,
    TimeSpan Duration,
    DateTimeOffset ReceivedAt);

public sealed record TranslationSessionOptions(
    string AudioDeviceId,
    string SourceLanguage,
    string TargetLanguage,
    TranslationMode Mode,
    string ProviderId,
    string? SubscriptionKey,
    string? Region);

public sealed record SessionDiagnostics(
    long CapturedFrames,
    long DroppedFrames,
    long AudioBytesSent,
    int BufferedFrames,
    TimeSpan LastCaptureToProviderLatency,
    int ErrorCount);

public sealed record ProviderStateChange(
    ProviderConnectionState State,
    string Message);

public sealed class ApplicationSettings
{
    public string? AudioDeviceId { get; set; }
    public string ProviderId { get; set; } = "local";
    public string LocalModelDirectory { get; set; } = @"D:\LinguaFlowModels";
    public TranslationMode TranslationMode { get; set; } = TranslationMode.Fast;
    public bool ShowOriginalText { get; set; } = true;
    public bool ShowPartialResults { get; set; } = true;
    public double OverlayOpacity { get; set; } = 0.9;
    public double OverlayFontSize { get; set; } = 26;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double OverlayWidth { get; set; } = 900;
    public int MaxHistoryItems { get; set; } = 500;
    public bool PersistHistory { get; set; }
    public string AzureRegion { get; set; } = string.Empty;
}

public sealed record LocalModelStatus(
    string ModelDirectory,
    string WhisperModelPath,
    string TranslationModelPath,
    bool WhisperReady,
    bool TranslationReady)
{
    public bool IsReady => WhisperReady && TranslationReady;
}

public sealed record LocalModelDownloadProgress(
    string FileName,
    long BytesReceived,
    long? TotalBytes,
    int CompletedFiles,
    int TotalFiles);
