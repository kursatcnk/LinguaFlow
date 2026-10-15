using LinguaFlow.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Extensions.Logging;

namespace LinguaFlow.Infrastructure;

public sealed partial class AzureSpeechTranslationProvider : IRealtimeTranslationProvider
{
    private readonly ILogger<AzureSpeechTranslationProvider> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _streamGate = new();
    private TranslationRecognizer? _recognizer;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private string _sessionId = string.Empty;
    private string _targetLanguage = "tr";
    private bool _started;

    public AzureSpeechTranslationProvider(ILogger<AzureSpeechTranslationProvider> logger)
    {
        _logger = logger;
    }

    public string Id => "azure";
    public string DisplayName => "Azure AI Speech";
    public bool IsDemo => false;
    public ProviderCapabilities Capabilities { get; } = new(true, true, false, false);

    public event EventHandler<TranslationEvent>? TranslationReceived;
    public event EventHandler<ProviderStateChange>? StateChanged;

    public async Task StartAsync(TranslationSessionOptions options, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                throw new InvalidOperationException("Azure Speech oturumu zaten çalışıyor.");
            }

            var key = FirstNonEmpty(options.SubscriptionKey, Environment.GetEnvironmentVariable("LINGUAFLOW_AZURE_SPEECH_KEY"));
            var region = FirstNonEmpty(options.Region, Environment.GetEnvironmentVariable("LINGUAFLOW_AZURE_SPEECH_REGION"));
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
            {
                throw new InvalidOperationException(
                    "Azure Speech anahtarı ve bölgesi gerekli. Arayüzden girin veya " +
                    "LINGUAFLOW_AZURE_SPEECH_KEY / LINGUAFLOW_AZURE_SPEECH_REGION değişkenlerini ayarlayın.");
            }

            StateChanged?.Invoke(this, new ProviderStateChange(ProviderConnectionState.Connecting, "Azure Speech bağlantısı kuruluyor…"));
            var speechConfig = SpeechTranslationConfig.FromSubscription(key, region);
            speechConfig.SpeechRecognitionLanguage = options.SourceLanguage;
            speechConfig.AddTargetLanguage(options.TargetLanguage);

            var streamFormat = AudioStreamFormat.GetWaveFormatPCM(16_000, 16, 1);
            _pushStream = AudioInputStream.CreatePushStream(streamFormat);
            _audioConfig = AudioConfig.FromStreamInput(_pushStream);
            _recognizer = new TranslationRecognizer(speechConfig, _audioConfig);
            _sessionId = Guid.NewGuid().ToString("N");
            _targetLanguage = options.TargetLanguage;

            _recognizer.Recognizing += OnRecognizing;
            _recognizer.Recognized += OnRecognized;
            _recognizer.Canceled += OnCanceled;
            _recognizer.SessionStarted += OnSessionStarted;
            _recognizer.SessionStopped += OnSessionStopped;

            try
            {
                await _recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                _started = true;
            }
            catch
            {
                DisposeSdkObjects();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask WriteAudioAsync(NormalizedAudioChunk chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_streamGate)
        {
            if (!_started || _pushStream is null)
            {
                throw new InvalidOperationException("Azure Speech stream başlatılmadı.");
            }

            _pushStream.Write(chunk.Pcm16Mono);
        }

        return ValueTask.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognizer is null)
            {
                _started = false;
                return;
            }

            lock (_streamGate)
            {
                _pushStream?.Close();
            }

            try
            {
                await _recognizer.StopContinuousRecognitionAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _started = false;
                DisposeSdkObjects();
                StateChanged?.Invoke(this, new ProviderStateChange(ProviderConnectionState.Disconnected, "Azure Speech bağlantısı kapandı."));
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void OnRecognizing(object? sender, TranslationRecognitionEventArgs args) => Publish(args.Result, false);

    private void OnRecognized(object? sender, TranslationRecognitionEventArgs args)
    {
        if (args.Result.Reason == ResultReason.TranslatedSpeech)
        {
            Publish(args.Result, true);
        }
    }

    private void Publish(TranslationRecognitionResult result, bool isFinal)
    {
        if (!result.Translations.TryGetValue(_targetLanguage, out var translation) ||
            (string.IsNullOrWhiteSpace(result.Text) && string.IsNullOrWhiteSpace(translation)))
        {
            return;
        }

        TranslationReceived?.Invoke(this, new TranslationEvent(
            _sessionId,
            string.IsNullOrWhiteSpace(result.ResultId) ? $"offset-{result.OffsetInTicks}" : result.ResultId,
            result.Text,
            translation,
            isFinal,
            TimeSpan.FromTicks(result.OffsetInTicks),
            result.Duration,
            DateTimeOffset.UtcNow));
    }

    private void OnCanceled(object? sender, TranslationRecognitionCanceledEventArgs args)
    {
        var details = CancellationDetails.FromResult(args.Result);
        var message = details.Reason == CancellationReason.Error
            ? $"Azure Speech hatası: {details.ErrorCode} — {details.ErrorDetails}"
            : $"Azure Speech iptal edildi: {details.Reason}";
        LogAzureCanceled(_logger, message);
        StateChanged?.Invoke(this, new ProviderStateChange(ProviderConnectionState.Error, message));
    }

    private void OnSessionStarted(object? sender, SessionEventArgs args)
    {
        LogAzureSessionStarted(_logger, args.SessionId);
        StateChanged?.Invoke(this, new ProviderStateChange(ProviderConnectionState.Connected, "Azure Speech bağlı."));
    }

    private void OnSessionStopped(object? sender, SessionEventArgs args)
    {
        LogAzureSessionStopped(_logger, args.SessionId);
    }

    private void DisposeSdkObjects()
    {
        if (_recognizer is not null)
        {
            _recognizer.Recognizing -= OnRecognizing;
            _recognizer.Recognized -= OnRecognized;
            _recognizer.Canceled -= OnCanceled;
            _recognizer.SessionStarted -= OnSessionStarted;
            _recognizer.SessionStopped -= OnSessionStopped;
            _recognizer.Dispose();
            _recognizer = null;
        }

        _audioConfig?.Dispose();
        _audioConfig = null;
        _pushStream?.Dispose();
        _pushStream = null;
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogAzureCanceled(ILogger logger, string message);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "Azure Speech session başladı: {SessionId}")]
    private static partial void LogAzureSessionStarted(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Information, Message = "Azure Speech session durdu: {SessionId}")]
    private static partial void LogAzureSessionStopped(ILogger logger, string sessionId);
}
