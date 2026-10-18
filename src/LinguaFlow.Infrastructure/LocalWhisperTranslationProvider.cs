using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using LinguaFlow.Core;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LinguaFlow.Infrastructure;

/// <summary>
/// Gerçek zamanlı yerel konuşma tanıma + çeviri.
/// <para>
/// Ses kayan bir tampona eklenir. Konuşma sürerken tampon her ~0,3 sn'de bir GPU üzerinde Whisper ile
/// yeniden çözülür ve anlık (partial) altyazı üretilir. Silero VAD konuşmacının ne zaman sustuğunu,
/// <see cref="StreamingCommitPolicy"/> cümlenin ne zaman kesinleştiğini belirler. Kesinleşen cümle
/// tampondan kesilir ve sıralı çeviri kuyruğuna verilir.
/// </para>
/// </summary>
public sealed partial class LocalWhisperTranslationProvider : IRealtimeTranslationProvider, IWarmupCapable
{
    private const int SampleRate = 16_000;
    private static readonly TimeSpan MaximumBufferedAudio = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan OnlineErrorBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OnlineThrottleBackoff = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OnlinePartialInterval = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan FocusReportInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TranslationContextWindow = TimeSpan.FromSeconds(20);
    private const int MaximumContextLength = 280;

    private readonly ILocalModelManager _modelManager;
    private readonly ITextTranslator _onlineTranslator;
    private readonly ITextTranslator _offlineTranslator;
    private readonly ILogger<LocalWhisperTranslationProvider> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly object _audioGate = new();
    private readonly object _partialGate = new();
    private readonly List<float> _audio = new(SampleRate * 30);
    private readonly SpeechBandFilter _bandFilter = new(SampleRate);
    private readonly SpeechFocusFilter _focus = new();

    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _processor;
    private WhisperVadFactory? _vadFactory;
    private WhisperVadProcessor? _vad;
    private string? _loadedWhisperPath;
    private TranslationMode? _loadedMode;
    private string _runtimeLabel = "CPU";
    private volatile bool _isWarm;

    private CancellationTokenSource? _sessionCancellation;
    private Channel<TranslationJob>? _jobs;
    private Task? _recognitionWorker;
    private Task? _translationWorker;
    private TranslationSessionOptions _options = new(string.Empty, LanguageCatalog.Auto, "tr", TranslationMode.Fast, "local", null, null);
    private string _sessionId = string.Empty;
    private long _bufferStartSample;
    private long _totalSamples;
    private int _segmentNumber;
    private long _partialVersion;
    private volatile bool _flushRequested;
    private DateTimeOffset _onlineBlockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOnlinePartial = DateTimeOffset.MinValue;
    private string? _partialSegmentId;
    private string _partialOriginal = string.Empty;
    private string _partialTranslation = string.Empty;
    private DateTimeOffset _lastFocusReport = DateTimeOffset.MinValue;
    private string? _contextText;
    private string? _contextLanguage;
    private TimeSpan _contextEnd;

    public LocalWhisperTranslationProvider(
        ILocalModelManager modelManager,
        IEnumerable<ITextTranslator> translators,
        ILogger<LocalWhisperTranslationProvider> logger)
    {
        _modelManager = modelManager;
        var all = translators.ToArray();
        _onlineTranslator = all.FirstOrDefault(translator => translator.Id == "google-free")
            ?? throw new InvalidOperationException("Çevrimiçi çevirmen kayıtlı değil.");
        _offlineTranslator = all.FirstOrDefault(translator => translator.Id == "bergamot")
            ?? throw new InvalidOperationException("Çevrimdışı çevirmen kayıtlı değil.");
        _logger = logger;
    }

    public string Id => "local";
    public string DisplayName => "Yerel Whisper";
    public bool IsDemo => false;
    public bool IsWarm => _isWarm;
    public ProviderCapabilities Capabilities => new(true, true, false, true);

    public event EventHandler<TranslationEvent>? TranslationReceived;
    public event EventHandler<ProviderStateChange>? StateChanged;

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognitionWorker is not null)
            {
                return;
            }

            await EnsureModelsLoadedAsync(_options.Mode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public async Task StartAsync(TranslationSessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognitionWorker is not null)
            {
                throw new InvalidOperationException("Yerel çeviri sağlayıcısı zaten çalışıyor.");
            }

            if (!_modelManager.GetStatus().IsReady)
            {
                throw new InvalidOperationException("Yerel modeller hazır değil. Önce ‘Modelleri indir’ düğmesine basın.");
            }

            _options = options;
            await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureModelsLoadedAsync(options.Mode, cancellationToken).ConfigureAwait(false);
                var fixedLanguage = LanguageCatalog.ToWhisperCode(options.SourceLanguage);
                _processor!.ChangeLanguage(fixedLanguage);
            }
            finally
            {
                _modelGate.Release();
            }

            lock (_audioGate)
            {
                _audio.Clear();
                _bufferStartSample = 0;
                _totalSamples = 0;
                _bandFilter.Reset();
                _focus.Reset();
            }

            lock (_partialGate)
            {
                _partialSegmentId = null;
                _partialOriginal = string.Empty;
                _partialTranslation = string.Empty;
            }

            _sessionId = Guid.NewGuid().ToString("N");
            _segmentNumber = 0;
            _flushRequested = false;
            _onlineBlockedUntil = DateTimeOffset.MinValue;
            _lastFocusReport = DateTimeOffset.MinValue;
            _contextText = null;
            _contextLanguage = null;
            _sessionCancellation = new CancellationTokenSource();
            _jobs = Channel.CreateUnbounded<TranslationJob>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            _translationWorker = Task.Run(() => TranslateLoopAsync(_jobs.Reader, _sessionCancellation.Token), CancellationToken.None);
            _recognitionWorker = Task.Run(() => RecognizeLoopAsync(_jobs.Writer, _sessionCancellation.Token), CancellationToken.None);
            ReportState($"Dinleniyor · {_runtimeLabel} · {DescribeTranslation()}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StateChanged?.Invoke(this, new(ProviderConnectionState.Error, $"Yerel modeller yüklenemedi: {exception.Message}"));
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask WriteAudioAsync(NormalizedAudioChunk chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var pcm = chunk.Pcm16Mono;
        var count = pcm.Length / 2;
        var samples = new float[count];
        lock (_audioGate)
        {
            for (var index = 0; index < count; index++)
            {
                samples[index] = _bandFilter.Process((short)(pcm[index * 2] | (pcm[(index * 2) + 1] << 8)) / 32768f);
            }

            _audio.AddRange(samples);
            _focus.Append(samples);
            _totalSamples += count;
            var maximum = (int)(MaximumBufferedAudio.TotalSeconds * SampleRate);
            if (_audio.Count > maximum)
            {
                // Tanıma bu makinede gerçek zamanın gerisinde kaldı; en eski sesi bırak.
                var overflow = _audio.Count - maximum;
                _audio.RemoveRange(0, overflow);
                _bufferStartSample += overflow;
            }
        }

        return ValueTask.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recognition = _recognitionWorker;
            var translation = _translationWorker;
            if (recognition is null)
            {
                return;
            }

            // Son cümleyi yarıda bırakmamak için tamponu bir kez daha çöz ve kesinleştir.
            _flushRequested = true;
            await WaitQuietlyAsync(recognition, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _jobs?.Writer.TryComplete();
            if (translation is not null)
            {
                await WaitQuietlyAsync(translation, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            StateChanged?.Invoke(this, new(ProviderConnectionState.Disconnected, "Durduruldu"));
        }
        finally
        {
            _sessionCancellation?.Cancel();
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _recognitionWorker = null;
            _translationWorker = null;
            _jobs = null;
            _lifecycleGate.Release();
        }
    }

    private async Task RecognizeLoopAsync(ChannelWriter<TranslationJob> jobs, CancellationToken cancellationToken)
    {
        var accuracy = _options.Mode == TranslationMode.Accuracy;
        var policy = new StreamingCommitPolicy
        {
            PauseToFinalize = TimeSpan.FromMilliseconds(accuracy ? 800 : 600),
            SentencePause = TimeSpan.FromMilliseconds(accuracy ? 320 : 240)
        };
        var stepSamples = (long)(SampleRate * (accuracy ? 0.5 : 0.3));
        var fixedLanguage = LanguageCatalog.ToWhisperCode(_options.SourceLanguage);
        var autoLanguage = fixedLanguage == LanguageCatalog.Auto;
        string? utteranceLanguage = autoLanguage ? null : fixedLanguage;
        IReadOnlyList<RecognizedSegment> previous = [];
        var lastCheckedTotal = 0L;
        var lastDecodedTotal = 0L;
        var decodedUntil = 0L;
        string? lastDetectedLanguage = null;
        var lastDecodeTime = TimeSpan.Zero;
        var lastPartial = string.Empty;
        var segmentId = NextSegmentId();
        var vadStepSamples = SampleRate / 10;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var flushing = _flushRequested;
                long total;
                lock (_audioGate)
                {
                    total = _totalSamples;
                }

                if (!flushing && total - lastCheckedTotal < vadStepSamples)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                float[] samples;
                long bufferStart;
                lock (_audioGate)
                {
                    samples = _audio.ToArray();
                    bufferStart = _bufferStartSample;
                    total = _totalSamples;
                }

                lastCheckedTotal = total;
                var bufferDuration = SamplesToTime(samples.Length);
                if (samples.Length < SampleRate / 4)
                {
                    if (flushing)
                    {
                        break;
                    }

                    continue;
                }

                // VAD ucuzdur (~1 ms/sn ses); sessizliği her 100 ms'de kontrol et, Whisper'ı yalnızca gerektiğinde çalıştır.
                var speech = DetectSpeech(samples);
                var speechDuration = speech.Aggregate(TimeSpan.Zero, (sum, span) => sum + (span.End - span.Start));
                IReadOnlyList<RecognizedSegment> hypothesis;
                if (speechDuration < TimeSpan.FromMilliseconds(200))
                {
                    hypothesis = [];
                }
                else
                {
                    var speechEnd = bufferStart + (long)(speech[^1].End.TotalSeconds * SampleRate);
                    var hypothesisCoversSpeech = decodedUntil >= speechEnd && speechEnd < bufferStart + samples.Length;
                    if (!flushing && hypothesisCoversSpeech)
                    {
                        // Konuşmacı sustu ve son çözümleme tüm konuşmayı içeriyor: yeniden çözmeden karar ver.
                        hypothesis = previous;
                    }
                    else if (flushing || total - lastDecodedTotal >= stepSamples)
                    {
                        var decodeWatch = Stopwatch.StartNew();
                        (hypothesis, var detected) = await DecodeAsync(samples, speech, bufferStart, cancellationToken).ConfigureAwait(false);
                        lastDecodeTime = decodeWatch.Elapsed;
                        lastDecodedTotal = total;
                        decodedUntil = bufferStart + samples.Length;
                        lastDetectedLanguage = detected ?? lastDetectedLanguage;
                        if (autoLanguage && utteranceLanguage is null && detected is not null && speechDuration >= TimeSpan.FromSeconds(1.2))
                        {
                            // Kısa seslerde dil tahmini kararsızdır; yeterli konuşma duyulunca ifade boyunca kilitle.
                            utteranceLanguage = detected;
                            _processor?.ChangeLanguage(detected);
                        }
                    }
                    else
                    {
                        continue;
                    }
                }

                var language = utteranceLanguage ?? lastDetectedLanguage;
                var decision = flushing
                    ? new CommitDecision(hypothesis, [], bufferDuration, true)
                    : policy.Decide(hypothesis, previous, speech, bufferDuration);

                if (decision.Committed.Count > 0)
                {
                    var text = TranscriptCleaner.Join(decision.Committed.Select(segment => segment.Text));
                    var offset = SamplesToTime(bufferStart) + decision.Committed[0].Start;
                    var duration = decision.Committed[^1].End - decision.Committed[0].Start;
                    jobs.TryWrite(new TranslationJob(segmentId, text, language, true, 0, offset, duration, lastDecodeTime));
                    if (_options.FocusMainSpeaker)
                    {
                        _focus.ObserveMainSpeaker(
                            bufferStart + TimeToSamples(decision.Committed[0].Start),
                            bufferStart + TimeToSamples(decision.Committed[^1].End));
                    }

                    segmentId = NextSegmentId();
                    lastPartial = string.Empty;
                }

                if (decision.TrimTo > TimeSpan.Zero)
                {
                    var trimSamples = (int)Math.Min(samples.Length, decision.TrimTo.TotalSeconds * SampleRate);
                    lock (_audioGate)
                    {
                        var removable = Math.Min(trimSamples, _audio.Count);
                        _audio.RemoveRange(0, removable);
                        _bufferStartSample += removable;
                    }
                }

                previous = decision.TrimTo > TimeSpan.Zero
                    ? decision.Pending.Select(segment => segment with
                    {
                        Start = segment.Start - decision.TrimTo,
                        End = segment.End - decision.TrimTo
                    }).ToArray()
                    : decision.Pending;

                var pendingText = TranscriptCleaner.Join(decision.Pending.Select(segment => segment.Text));
                if (!string.Equals(pendingText, lastPartial, StringComparison.Ordinal))
                {
                    lastPartial = pendingText;
                    PublishPartialOriginal(segmentId, pendingText, language);
                    if (pendingText.Length > 0)
                    {
                        var version = Interlocked.Increment(ref _partialVersion);
                        var offset = SamplesToTime(bufferStart) + decision.Pending[0].Start;
                        jobs.TryWrite(new TranslationJob(segmentId, pendingText, utteranceLanguage, false, version, offset,
                            decision.Pending[^1].End - decision.Pending[0].Start, lastDecodeTime));
                    }
                }

                if (decision.EndOfUtterance)
                {
                    previous = [];
                    if (autoLanguage && utteranceLanguage is not null)
                    {
                        utteranceLanguage = null;
                        _processor?.ChangeLanguage(LanguageCatalog.Auto);
                    }
                }

                if (flushing)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogLocalProcessingFailed(_logger, exception);
            StateChanged?.Invoke(this, new(ProviderConnectionState.Error, $"Konuşma tanıma hatası: {exception.Message}"));
        }
    }

    private async Task<(IReadOnlyList<RecognizedSegment> Segments, string? Language)> DecodeAsync(
        float[] samples,
        IReadOnlyList<SpeechSpan> speech,
        long bufferStart,
        CancellationToken cancellationToken)
    {
        var processor = _processor ?? throw new InvalidOperationException("Whisper modeli yüklenmedi.");
        var segments = new List<RecognizedSegment>();
        string? language = null;
        var rejection = FocusVerdict.Accepted;
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(segment.Language))
            {
                language ??= segment.Language;
            }

            // Whisper şarkı sözlerini ♪ ile işaretler. "♪ söz ♪ konuşma" temizlikte ayrılır; "söz ♪" gibi
            // yalnızca kapanış işareti olan segmentte ise sözler işaretin önündedir.
            var raw = segment.Text.Trim();
            var hasNote = raw.AsSpan().IndexOfAny("♪♫♬") >= 0;
            var lyricsOnly = _options.FocusMainSpeaker && raw.Length > 0 && raw[^1] is '♪' or '♫' or '♬';
            var text = lyricsOnly ? string.Empty : TranscriptCleaner.Clean(raw);
            // VAD'ın konuşma bulmadığı bölgedeki segmentler Whisper'ın uydurmasıdır.
            if (text.Length == 0 || !OverlapsSpeech(segment.Start, segment.End, speech))
            {
                rejection = hasNote && _options.FocusMainSpeaker ? FocusVerdict.Singing : rejection;
                continue;
            }

            if (_options.FocusMainSpeaker)
            {
                var verdict = _focus.Evaluate(
                    bufferStart + TimeToSamples(segment.Start),
                    bufferStart + TimeToSamples(segment.End),
                    segment.Probability);
                if (verdict != FocusVerdict.Accepted)
                {
                    rejection = verdict;
                    continue;
                }
            }

            segments.Add(new RecognizedSegment(text, segment.Start, segment.End));
        }

        if (rejection != FocusVerdict.Accepted)
        {
            ReportIgnored(rejection);
        }

        return (segments, language);
    }

    private void ReportIgnored(FocusVerdict verdict)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastFocusReport < FocusReportInterval)
        {
            return;
        }

        _lastFocusReport = now;
        ReportState(verdict switch
        {
            FocusVerdict.Singing => "Arka planda müzik/şarkı var · yalnızca konuşmacı çevriliyor",
            FocusVerdict.Background => "Arka plandaki kısık sesler yoksayılıyor · ana konuşmacı çevriliyor",
            _ => "Anlaşılmayan ses yoksayıldı"
        });
    }

    private static bool OverlapsSpeech(TimeSpan start, TimeSpan end, IReadOnlyList<SpeechSpan> speech)
    {
        var tolerance = TimeSpan.FromMilliseconds(300);
        return speech.Any(span => start < span.End + tolerance && end > span.Start - tolerance);
    }

    private List<SpeechSpan> DetectSpeech(float[] samples)
    {
        var vad = _vad ?? throw new InvalidOperationException("VAD modeli yüklenmedi.");
        return vad.DetectSpeech(samples)
            .Select(segment => new SpeechSpan(segment.Start, segment.End))
            .ToList();
    }

    private async Task TranslateLoopAsync(ChannelReader<TranslationJob> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Arada daha yeni bir partial geldiyse eskisini çevirmeye uğraşma.
                if (!job.IsFinal && job.Version != Interlocked.Read(ref _partialVersion))
                {
                    continue;
                }

                var watch = Stopwatch.StartNew();
                var context = job.IsFinal &&
                    _contextText is not null &&
                    string.Equals(_contextLanguage, job.Language, StringComparison.OrdinalIgnoreCase) &&
                    job.Offset - _contextEnd < TranslationContextWindow
                        ? _contextText
                        : null;
                var translated = await TranslateWithFallbackAsync(job.Text, job.Language, job.IsFinal, context, cancellationToken).ConfigureAwait(false);
                watch.Stop();
                if (job.IsFinal)
                {
                    _contextText = ContextTail(TranslationTextPreparer.Prepare(job.Text, job.Language));
                    _contextLanguage = job.Language;
                    _contextEnd = job.Offset + job.Duration;

                    lock (_partialGate)
                    {
                        if (_partialSegmentId == job.SegmentId)
                        {
                            _partialSegmentId = null;
                            _partialOriginal = string.Empty;
                            _partialTranslation = string.Empty;
                        }
                    }

                    TranslationReceived?.Invoke(this, new TranslationEvent(
                        _sessionId, job.SegmentId, job.Text, translated, true, job.Offset, job.Duration,
                        DateTimeOffset.UtcNow, job.Language));
                    var languageLabel = string.IsNullOrWhiteSpace(job.Language) ? "?" : job.Language.ToUpperInvariant();
                    ReportState($"{languageLabel} → {LanguageCatalog.BaseCode(_options.TargetLanguage).ToUpperInvariant()} · " +
                        $"tanıma {job.DecodeTime.TotalMilliseconds:N0} ms · çeviri {watch.ElapsedMilliseconds:N0} ms · {_runtimeLabel}");
                    continue;
                }

                string original;
                lock (_partialGate)
                {
                    if (_partialSegmentId != job.SegmentId || _partialOriginal.Length == 0)
                    {
                        continue;
                    }

                    if (translated.Length > 0)
                    {
                        _partialTranslation = translated;
                    }

                    original = _partialOriginal;
                    translated = _partialTranslation;
                }

                // Özgün metin olarak en güncel partial'ı göster; çeviri bir adım geriden gelebilir.
                TranslationReceived?.Invoke(this, new TranslationEvent(
                    _sessionId, job.SegmentId, original, translated, false, job.Offset, job.Duration,
                    DateTimeOffset.UtcNow, job.Language));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogLocalProcessingFailed(_logger, exception);
            StateChanged?.Invoke(this, new(ProviderConnectionState.Error, $"Çeviri hatası: {exception.Message}"));
        }
    }

    private void PublishPartialOriginal(string segmentId, string text, string? language)
    {
        string translation;
        lock (_partialGate)
        {
            if (_partialSegmentId != segmentId)
            {
                _partialSegmentId = segmentId;
                _partialTranslation = string.Empty;
            }

            _partialOriginal = text;
            translation = text.Length == 0 ? string.Empty : _partialTranslation;
        }

        TranslationReceived?.Invoke(this, new TranslationEvent(
            _sessionId, segmentId, text, translation, false, TimeSpan.Zero, TimeSpan.Zero,
            DateTimeOffset.UtcNow, language));
    }

    /// <summary>
    /// Çeviri bütçesi: ücretsiz çevrimiçi servis yoğun istekte IP'yi geçici olarak engeller (HTTP 429).
    /// Bu yüzden anlık (partial) çeviriler mümkünse cihazda yapılır, çevrimiçi servise seyrek gider;
    /// kesin cümleler en iyi kalite için çevrimiçi servise gider. Servis engellerse bir süre tamamen
    /// cihazdaki modele geçilir.
    /// </summary>
    private async Task<string> TranslateWithFallbackAsync(
        string text,
        string? language,
        bool isFinal,
        string? context,
        CancellationToken cancellationToken)
    {
        var source = string.IsNullOrWhiteSpace(language) ? LanguageCatalog.Auto : language;
        var target = _options.TargetLanguage;
        if (LanguageCatalog.IsSameLanguage(source, target))
        {
            return text;
        }

        // Konuşma dökümünü yazı diline yaklaştır (dolgu sözcükleri, yarım sözcükler, BÜYÜK HARF).
        var prepared = TranslationTextPreparer.Prepare(text, language);
        text = prepared.Length > 0 ? prepared : text;

        // Anlık parçalarda dil tahmini kararsız olabilir; yalnızca kesin cümleler paket indirmesi tetikler.
        var offlineReady = isFinal
            ? _offlineTranslator.CanTranslate(source, target)
            : _offlineTranslator is BergamotOfflineTranslator bergamot && bergamot.IsReady(source, target);
        var onlineAllowed = _options.TranslationEngine != TranslationEngines.Offline && DateTimeOffset.UtcNow >= _onlineBlockedUntil;
        if (!isFinal)
        {
            if (offlineReady)
            {
                return await TryTranslateAsync(_offlineTranslator, text, source, target, cancellationToken).ConfigureAwait(false);
            }

            if (!onlineAllowed || DateTimeOffset.UtcNow - _lastOnlinePartial < OnlinePartialInterval)
            {
                return string.Empty;
            }

            _lastOnlinePartial = DateTimeOffset.UtcNow;
            return await TryTranslateAsync(_onlineTranslator, text, source, target, cancellationToken).ConfigureAwait(false);
        }

        if (onlineAllowed)
        {
            var online = await TryTranslateAsync(_onlineTranslator, text, source, target, cancellationToken, context).ConfigureAwait(false);
            if (online.Length > 0)
            {
                return online;
            }
        }

        return offlineReady
            ? await TryTranslateAsync(_offlineTranslator, text, source, target, cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    private async Task<string> TryTranslateAsync(
        ITextTranslator translator,
        string text,
        string source,
        string target,
        CancellationToken cancellationToken,
        string? context = null)
    {
        try
        {
            var result = context is not null && translator is IContextualTextTranslator contextual
                ? await contextual.TranslateAsync(text, context, source, target, cancellationToken).ConfigureAwait(false)
                : await translator.TranslateAsync(text, source, target, cancellationToken).ConfigureAwait(false);
            if (translator == _onlineTranslator && _onlineBlockedUntil != DateTimeOffset.MinValue)
            {
                _onlineBlockedUntil = DateTimeOffset.MinValue;
                ReportState("Çevrimiçi çeviri yeniden kullanılabilir");
            }

            return result.Trim();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (translator == _onlineTranslator)
            {
                var throttled = exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
                _onlineBlockedUntil = DateTimeOffset.UtcNow + (throttled ? OnlineThrottleBackoff : OnlineErrorBackoff);
                ReportState(throttled
                    ? "Çevrimiçi çeviri geçici olarak sınırlandı · birkaç dakika cihazdaki çeviri kullanılacak"
                    : "Çevrimiçi çeviriye ulaşılamadı · cihazdaki çeviri kullanılıyor");
            }

            LogTranslationFailed(_logger, translator.Id, exception);
            return string.Empty;
        }
    }

    private async Task EnsureModelsLoadedAsync(TranslationMode mode, CancellationToken cancellationToken)
    {
        var status = _modelManager.GetStatus();
        if (!status.IsReady)
        {
            throw new InvalidOperationException("Yerel modeller hazır değil.");
        }

        var sameModel = _whisperFactory is not null &&
            string.Equals(_loadedWhisperPath, status.WhisperModelPath, StringComparison.OrdinalIgnoreCase);
        if (sameModel && _processor is not null && _loadedMode == mode && _isWarm)
        {
            return;
        }

        if (!sameModel)
        {
            StateChanged?.Invoke(this, new(ProviderConnectionState.Connecting,
                $"{status.WhisperModel.DisplayName.Split('—')[0].Trim()} yükleniyor…"));
        }

        await Task.Run(() =>
        {
            if (!sameModel)
            {
                DisposeModels();
                _whisperFactory = WhisperFactory.FromPath(status.WhisperModelPath, new WhisperFactoryOptions
                {
                    UseGpu = true,
                    UseFlashAttention = true
                });
                _runtimeLabel = RuntimeOptions.LoadedLibrary?.ToString() switch
                {
                    "Cuda" => "GPU (CUDA)",
                    "Vulkan" => "GPU (Vulkan)",
                    "CoreML" => "GPU (CoreML)",
                    "OpenVino" => "OpenVINO",
                    _ => "CPU"
                };

                _vadFactory = WhisperVadFactory.FromPath(status.VadModelPath, new WhisperFactoryOptions { UseGpu = false });
                _vad = _vadFactory.CreateBuilder()
                    .WithThreshold(0.5f)
                    .WithMinSpeechDuration(TimeSpan.FromMilliseconds(150))
                    .WithMinSilenceDuration(TimeSpan.FromMilliseconds(150))
                    .WithSpeechPadding(TimeSpan.FromMilliseconds(40))
                    .Build();
                _loadedWhisperPath = status.WhisperModelPath;
            }

            _processor?.Dispose();
            var builder = _whisperFactory!.CreateBuilder()
                .WithLanguage(LanguageCatalog.Auto)
                .WithNoContext()
                .WithProbabilities()
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
            builder = mode == TranslationMode.Accuracy
                ? builder.WithBeamSearchSamplingStrategy(strategy => strategy.WithBeamSize(4))
                : builder.WithGreedySamplingStrategy();
            _processor = builder.Build();
            _loadedMode = mode;
        }, cancellationToken).ConfigureAwait(false);

        // İlk çözümleme GPU çekirdeklerini derler; oturum başlamadan önce sahte sesle ısındır.
        var warmup = new float[SampleRate];
        var random = new Random(7);
        for (var index = 0; index < warmup.Length; index++)
        {
            warmup[index] = (float)((random.NextDouble() - 0.5) * 0.02);
        }

        await foreach (var _ in _processor!.ProcessAsync(warmup, cancellationToken).ConfigureAwait(false))
        {
        }

        _isWarm = true;
        StateChanged?.Invoke(this, new(ProviderConnectionState.Connected, $"Model hazır · {_runtimeLabel}"));
    }

    private string DescribeTranslation() => _options.TranslationEngine == TranslationEngines.Offline
        ? "çevrimdışı çeviri"
        : "çevrimiçi çeviri";

    private void ReportState(string message) =>
        StateChanged?.Invoke(this, new(ProviderConnectionState.Connected, message));

    private string NextSegmentId() =>
        $"{_sessionId[..Math.Min(8, _sessionId.Length)]}-{Interlocked.Increment(ref _segmentNumber).ToString(CultureInfo.InvariantCulture)}";

    private static TimeSpan SamplesToTime(long samples) => TimeSpan.FromSeconds(samples / (double)SampleRate);

    private static long TimeToSamples(TimeSpan time) => (long)(time.TotalSeconds * SampleRate);

    /// <summary>Bağlam olarak önceki cümlenin yalnızca son kısmı yeterli; uzun bağlam isteği şişirir.</summary>
    internal static string? ContextTail(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length <= MaximumContextLength)
        {
            return trimmed;
        }

        var tail = trimmed[^MaximumContextLength..];
        var space = tail.IndexOf(' ', StringComparison.Ordinal);
        return space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
    }

    private static async Task WaitQuietlyAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DisposeModels()
    {
        _isWarm = false;
        _processor?.Dispose();
        _processor = null;
        _whisperFactory?.Dispose();
        _whisperFactory = null;
        _vad?.Dispose();
        _vad = null;
        _vadFactory?.Dispose();
        _vadFactory = null;
        _loadedWhisperPath = null;
        _loadedMode = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _modelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeModels();
        }
        finally
        {
            _modelGate.Release();
        }

        _lifecycleGate.Dispose();
        _modelGate.Dispose();
    }

    private sealed record TranslationJob(
        string SegmentId,
        string Text,
        string? Language,
        bool IsFinal,
        long Version,
        TimeSpan Offset,
        TimeSpan Duration,
        TimeSpan DecodeTime);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Error, Message = "Yerel konuşma tanıma veya çeviri başarısız oldu.")]
    private static partial void LogLocalProcessingFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning, Message = "Çevirmen {Translator} başarısız oldu.")]
    private static partial void LogTranslationFailed(ILogger logger, string translator, Exception exception);
}
