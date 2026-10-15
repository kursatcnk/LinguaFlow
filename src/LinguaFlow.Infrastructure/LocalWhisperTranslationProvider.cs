using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using BergamotTranslatorSharp;
using LinguaFlow.Core;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace LinguaFlow.Infrastructure;

public sealed partial class LocalWhisperTranslationProvider : IRealtimeTranslationProvider
{
    private readonly ILocalModelManager _modelManager;
    private readonly ILogger<LocalWhisperTranslationProvider> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Channel<NormalizedAudioChunk>? _audioChannel;
    private CancellationTokenSource? _workerCancellation;
    private Task? _worker;
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisperProcessor;
    private BlockingService? _translationService;
    private Stopwatch? _sessionClock;
    private string _sessionId = string.Empty;
    private int _segmentNumber;

    public LocalWhisperTranslationProvider(
        ILocalModelManager modelManager,
        ILogger<LocalWhisperTranslationProvider> logger)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    public string Id => "local";
    public string DisplayName => "Yerel Ücretsiz";
    public bool IsDemo => false;
    public ProviderCapabilities Capabilities => new(false, true, false, false);

    public event EventHandler<TranslationEvent>? TranslationReceived;
    public event EventHandler<ProviderStateChange>? StateChanged;

    public async Task StartAsync(TranslationSessionOptions options, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_worker is not null)
            {
                throw new InvalidOperationException("Yerel çeviri sağlayıcısı zaten çalışıyor.");
            }

            var status = _modelManager.GetStatus();
            if (!status.IsReady)
            {
                throw new InvalidOperationException("Yerel modeller hazır değil. Önce ‘Modelleri İndir’ düğmesine basın.");
            }

            StateChanged?.Invoke(this, new(ProviderConnectionState.Connecting, "Yerel yapay zekâ modelleri yükleniyor…"));
            cancellationToken.ThrowIfCancellationRequested();

            _whisperFactory = WhisperFactory.FromPath(status.WhisperModelPath);
            _whisperProcessor = _whisperFactory.CreateBuilder()
                .WithLanguage("en")
                .Build();

            _translationService = new BlockingService(status.TranslationModelPath);

            _sessionId = Guid.NewGuid().ToString("N");
            _segmentNumber = 0;
            _sessionClock = Stopwatch.StartNew();
            _workerCancellation = new CancellationTokenSource();
            _audioChannel = Channel.CreateBounded<NormalizedAudioChunk>(new BoundedChannelOptions(128)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _worker = ProcessAudioAsync(_audioChannel.Reader, _workerCancellation.Token);
            StateChanged?.Invoke(this, new(ProviderConnectionState.Connected, "Yerel ve ücretsiz • ses cihazdan çıkmıyor"));
        }
        catch
        {
            DisposeModels();
            StateChanged?.Invoke(this, new(ProviderConnectionState.Error, "Yerel modeller yüklenemedi"));
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask WriteAudioAsync(NormalizedAudioChunk chunk, CancellationToken cancellationToken)
    {
        var channel = _audioChannel ?? throw new InvalidOperationException("Yerel sağlayıcı başlatılmadı.");
        return channel.Writer.WriteAsync(chunk, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var worker = _worker;
            _audioChannel?.Writer.TryComplete();
            if (worker is not null)
            {
                try
                {
                    await worker.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _workerCancellation?.Cancel();
                    LogWorkerStopTimeout(_logger);
                }
            }

            StateChanged?.Invoke(this, new(ProviderConnectionState.Disconnected, "Yerel çeviri durduruldu"));
        }
        finally
        {
            _workerCancellation?.Cancel();
            _workerCancellation?.Dispose();
            _workerCancellation = null;
            _audioChannel = null;
            _worker = null;
            _sessionClock?.Stop();
            _sessionClock = null;
            DisposeModels();
            _lifecycleGate.Release();
        }
    }

    private async Task ProcessAudioAsync(ChannelReader<NormalizedAudioChunk> reader, CancellationToken cancellationToken)
    {
        var segmenter = new PcmSpeechSegmenter();
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var utterance = segmenter.Push(chunk.Pcm16Mono);
                if (utterance is not null)
                {
                    await RecognizeAndTranslateAsync(utterance, cancellationToken).ConfigureAwait(false);
                }
            }

            var remaining = segmenter.Flush();
            if (remaining is not null)
            {
                await RecognizeAndTranslateAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogLocalProcessingFailed(_logger, exception);
            StateChanged?.Invoke(this, new(ProviderConnectionState.Error, $"Yerel çeviri hatası: {exception.Message}"));
            throw;
        }
    }

    private async Task RecognizeAndTranslateAsync(SpeechUtterance utterance, CancellationToken cancellationToken)
    {
        var processor = _whisperProcessor ?? throw new InvalidOperationException("Whisper modeli yüklenmedi.");
        var samples = new float[utterance.Pcm16Mono.Length / 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var byteIndex = index * 2;
            var sample = (short)(utterance.Pcm16Mono[byteIndex] | (utterance.Pcm16Mono[byteIndex + 1] << 8));
            samples[index] = sample / 32768f;
        }

        var english = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            english.Append(segment.Text);
        }

        var original = english.ToString().Trim();
        if (string.IsNullOrWhiteSpace(original) || string.Equals(original, "[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StateChanged?.Invoke(this, new(ProviderConnectionState.Connected, "Konuşma algılandı • Türkçeye çevriliyor…"));
        cancellationToken.ThrowIfCancellationRequested();
        var translated = Translate(original);
        if (string.IsNullOrWhiteSpace(translated))
        {
            return;
        }

        var elapsed = _sessionClock?.Elapsed ?? TimeSpan.Zero;
        var offset = elapsed > utterance.Duration ? elapsed - utterance.Duration : TimeSpan.Zero;
        var segmentId = Interlocked.Increment(ref _segmentNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
        TranslationReceived?.Invoke(this, new(
            _sessionId,
            segmentId,
            original,
            translated,
            true,
            offset,
            utterance.Duration,
            DateTimeOffset.UtcNow));
        StateChanged?.Invoke(this, new(ProviderConnectionState.Connected, "Yerel ve ücretsiz • dinleniyor"));
    }

    private string Translate(string original)
    {
        var service = _translationService ?? throw new InvalidOperationException("Çeviri modeli yüklenmedi.");
        return CleanTranslation(service.Translate(original));
    }

    internal static string CleanTranslation(string value)
    {
        var result = value.Trim().Trim('"', '\'', '“', '”');
        string[] prefixes = ["Türkçe:", "Turkish:", "Çeviri:", "Translation:"];
        foreach (var prefix in prefixes)
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[prefix.Length..].Trim();
            }
        }

        return result;
    }

    private void DisposeModels()
    {
        _translationService?.Dispose();
        _translationService = null;
        _whisperProcessor?.Dispose();
        _whisperProcessor = null;
        _whisperFactory?.Dispose();
        _whisperFactory = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Error, Message = "Yerel konuşma tanıma veya çeviri başarısız oldu.")]
    private static partial void LogLocalProcessingFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning, Message = "Yerel çeviri işçisi durma zaman aşımına ulaştı.")]
    private static partial void LogWorkerStopTimeout(ILogger logger);
}
