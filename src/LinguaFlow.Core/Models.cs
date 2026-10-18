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

public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => Name;
}

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
    DateTimeOffset ReceivedAt,
    string? DetectedLanguage = null);

public sealed record TranscriptSegment(
    string SessionId,
    string SegmentId,
    string OriginalText,
    string TranslatedText,
    bool IsFinal,
    TimeSpan Offset,
    TimeSpan Duration,
    DateTimeOffset ReceivedAt,
    string? DetectedLanguage = null);

public sealed record TranslationSessionOptions(
    string AudioDeviceId,
    string SourceLanguage,
    string TargetLanguage,
    TranslationMode Mode,
    string ProviderId,
    string? SubscriptionKey,
    string? Region,
    string TranslationEngine = TranslationEngines.Online,
    bool FocusMainSpeaker = true);

public static class TranslationEngines
{
    /// <summary>Ücretsiz çevrimiçi metin çevirisi: tüm dil çiftleri, en yüksek kalite.</summary>
    public const string Online = "online";

    /// <summary>Tamamen cihazda çeviri (Bergamot); şu an yalnızca İngilizce → Türkçe.</summary>
    public const string Offline = "offline";
}

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
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "tr";
    public string TranslationEngine { get; set; } = TranslationEngines.Online;
    public string WhisperModelId { get; set; } = "turbo";
    public string LocalModelDirectory { get; set; } = @"D:\LinguaFlowModels";
    public TranslationMode TranslationMode { get; set; } = TranslationMode.Fast;
    public bool ShowOriginalText { get; set; } = true;
    public bool ShowPartialResults { get; set; } = true;

    /// <summary>Arka plandaki şarkı, müzik ve uzak konuşmaları çevirme; yalnızca ana konuşmacıyı çevir.</summary>
    public bool FocusMainSpeaker { get; set; } = true;

    public bool OverlayVisible { get; set; } = true;
    public double OverlayOpacity { get; set; } = 0.9;
    public double OverlayFontSize { get; set; } = 30;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    public double OverlayWidth { get; set; } = 1100;
    public int MaxHistoryItems { get; set; } = 500;
    public bool PersistHistory { get; set; }
    public string AzureRegion { get; set; } = string.Empty;
}

public sealed record WhisperModelInfo(string Id, string DisplayName, string FileName, long SizeBytes)
{
    public override string ToString() => DisplayName;
}

public sealed record LocalModelStatus(
    string ModelDirectory,
    WhisperModelInfo WhisperModel,
    string WhisperModelPath,
    string VadModelPath,
    bool WhisperReady,
    bool VadReady)
{
    public bool IsReady => WhisperReady && VadReady;
}

public sealed record LocalModelDownloadProgress(
    string FileName,
    long BytesReceived,
    long? TotalBytes,
    int CompletedFiles,
    int TotalFiles);
