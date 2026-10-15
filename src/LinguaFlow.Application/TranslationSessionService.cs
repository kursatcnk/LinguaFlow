using LinguaFlow.Core;
using Microsoft.Extensions.Logging;

namespace LinguaFlow.Application;

public sealed partial class TranslationSessionService : ITranslationSessionService
{
    private readonly IAudioCaptureService _capture;
    private readonly IAudioProcessingService _audioProcessor;
    private readonly ITranscriptProcessor _transcriptProcessor;
    private readonly Dictionary<string, IRealtimeTranslationProvider> _providers;
    private readonly ILogger<TranslationSessionService> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private Task? _audioPump;
    private IRealtimeTranslationProvider? _activeProvider;
    private int _errorCount;
    private long _audioBytesSent;
    private TimeSpan _lastCaptureToProviderLatency;

    public TranslationSessionService(
        IAudioCaptureService capture,
        IAudioProcessingService audioProcessor,
        ITranscriptProcessor transcriptProcessor,
        IEnumerable<IRealtimeTranslationProvider> providers,
        ILogger<TranslationSessionService> logger)
    {
        _capture = capture;
        _audioProcessor = audioProcessor;
        _transcriptProcessor = transcriptProcessor;
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _capture.CaptureFaulted += OnCaptureFaulted;
    }

    public TranslationStatus Status { get; private set; } = TranslationStatus.Idle;

    public SessionDiagnostics Diagnostics => new(
        _capture.CapturedFrames,
        _capture.DroppedFrames,
        Interlocked.Read(ref _audioBytesSent),
        _capture.BufferedFrames,
        _lastCaptureToProviderLatency,
        Volatile.Read(ref _errorCount));

    public event EventHandler<TranslationStatus>? StatusChanged;
    public event EventHandler<ProviderStateChange>? ProviderStateChanged;
    public event EventHandler<SessionDiagnostics>? DiagnosticsChanged;

    public async Task StartAsync(TranslationSessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status is not TranslationStatus.Idle and not TranslationStatus.Faulted)
            {
                throw new InvalidOperationException("Bir çeviri oturumu zaten çalışıyor.");
            }

            if (!_providers.TryGetValue(options.ProviderId, out var provider))
            {
                throw new InvalidOperationException($"Çeviri sağlayıcısı bulunamadı: {options.ProviderId}");
            }

            SetStatus(TranslationStatus.Starting);
            _transcriptProcessor.Reset();
            _audioProcessor.Reset();
            Interlocked.Exchange(ref _audioBytesSent, 0);
            Interlocked.Exchange(ref _errorCount, 0);
            _lastCaptureToProviderLatency = TimeSpan.Zero;

            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeProvider = provider;
            provider.TranslationReceived += OnTranslationReceived;
            provider.StateChanged += OnProviderStateChanged;

            try
            {
                await provider.StartAsync(options, _sessionCancellation.Token).ConfigureAwait(false);
                await _capture.StartAsync(options.AudioDeviceId, _sessionCancellation.Token).ConfigureAwait(false);
                _audioPump = PumpAudioAsync(provider, _sessionCancellation.Token);
                SetStatus(TranslationStatus.Listening);
            }
            catch
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(TranslationStatus.Faulted);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status == TranslationStatus.Idle)
            {
                return;
            }

            SetStatus(TranslationStatus.Stopping);
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(TranslationStatus.Idle);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task PumpAudioAsync(IRealtimeTranslationProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _capture.Frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var chunk = _audioProcessor.Process(frame);
                if (chunk is null || chunk.Pcm16Mono.Length == 0)
                {
                    continue;
                }

                await provider.WriteAudioAsync(chunk, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _audioBytesSent, chunk.Pcm16Mono.Length);
                _lastCaptureToProviderLatency = DateTimeOffset.UtcNow - chunk.CapturedAt;
                DiagnosticsChanged?.Invoke(this, Diagnostics);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errorCount);
            LogAudioPipelineFailed(_logger, exception);
            SetStatus(TranslationStatus.Faulted);
            _sessionCancellation?.Cancel();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var provider = _activeProvider;
        var pump = _audioPump;
        var cancellation = _sessionCancellation;

        cancellation?.Cancel();
        try
        {
            await _capture.StopAsync(cancellationToken).ConfigureAwait(false);
            if (pump is not null)
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            LogAudioPumpTimeout(_logger);
        }
        finally
        {
            if (provider is not null)
            {
                try
                {
                    await provider.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    provider.TranslationReceived -= OnTranslationReceived;
                    provider.StateChanged -= OnProviderStateChanged;
                }
            }

            cancellation?.Dispose();
            _sessionCancellation = null;
            _audioPump = null;
            _activeProvider = null;
        }
    }

    private void OnTranslationReceived(object? sender, TranslationEvent translationEvent) =>
        _transcriptProcessor.Process(translationEvent);

    private void OnProviderStateChanged(object? sender, ProviderStateChange args)
    {
        ProviderStateChanged?.Invoke(this, args);
        if (args.State == ProviderConnectionState.Error && Status == TranslationStatus.Listening)
        {
            Interlocked.Increment(ref _errorCount);
            SetStatus(TranslationStatus.Faulted);
            _sessionCancellation?.Cancel();
        }
    }

    private void OnCaptureFaulted(object? sender, string message)
    {
        Interlocked.Increment(ref _errorCount);
        LogCaptureFailed(_logger, message);
        SetStatus(TranslationStatus.Faulted);
        _sessionCancellation?.Cancel();
    }

    private void SetStatus(TranslationStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        _capture.CaptureFaulted -= OnCaptureFaulted;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "Ses işleme hattı beklenmedik biçimde durdu.")]
    private static partial void LogAudioPipelineFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Ses pompası zaman aşımı nedeniyle zorla sonlandırıldı.")]
    private static partial void LogAudioPumpTimeout(ILogger logger);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Ses yakalama hatası: {Message}")]
    private static partial void LogCaptureFailed(ILogger logger, string message);
}
