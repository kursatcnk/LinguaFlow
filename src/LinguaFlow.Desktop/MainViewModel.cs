using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaFlow.Core;
using LinguaFlow.Infrastructure;

namespace LinguaFlow.Desktop;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaximumHistory = 500;
    private readonly IAudioCaptureService _capture;
    private readonly ITranslationSessionService _session;
    private readonly ITranscriptProcessor _transcript;
    private readonly ISettingsService _settingsService;
    private readonly ILocalModelManager _localModelManager;
    private readonly BergamotModelStore _offlineModels;
    private readonly IReadOnlyList<IRealtimeTranslationProvider> _providers;
    private readonly OverlayViewModel _overlay;
    private ApplicationSettings _settings = new();
    private CancellationTokenSource? _warmupCancellation;
    private bool _initialized;
    private bool _isStopping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRunCommand))]
    private AudioDevice? _selectedAudioDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelsCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRunCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAzureProvider))]
    [NotifyPropertyChangedFor(nameof(IsLocalProvider))]
    [NotifyPropertyChangedFor(nameof(IsDemoProvider))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRunCommand))]
    private ProviderOption? _selectedProvider;

    [ObservableProperty] private LanguageOption? _selectedSourceLanguage;
    [ObservableProperty] private LanguageOption? _selectedTargetLanguage;
    [ObservableProperty] private ChoiceOption<TranslationMode>? _selectedMode;
    [ObservableProperty] private ChoiceOption<string>? _selectedTranslationEngine;
    [ObservableProperty] private ChoiceOption<bool>? _selectedFocusOption;
    [ObservableProperty] private WhisperModelInfo? _selectedWhisperModel;
    [ObservableProperty] private string _azureKey = string.Empty;
    [ObservableProperty] private string _azureRegion = string.Empty;
    [ObservableProperty] private string _statusText = "Hazır";
    [ObservableProperty] private string _providerStatus = string.Empty;
    [ObservableProperty] private string _detectedLanguageLabel = "—";
    [ObservableProperty] private string _diagnosticsText = string.Empty;
    [ObservableProperty] private bool _isOverlayVisible = true;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartial))]
    private string _partialTranslation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartial))]
    private string _partialOriginal = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranscriptEmpty))]
    private int _historyCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRunCommand))]
    private bool _isDownloadingModels;

    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _localModelStatus = "Modeller kontrol ediliyor…";
    [ObservableProperty] private bool _modelsReady;
    [ObservableProperty] private string _modelDirectory = @"D:\LinguaFlowModels";

    public MainViewModel(
        IAudioCaptureService capture,
        ITranslationSessionService session,
        ITranscriptProcessor transcript,
        ISettingsService settingsService,
        ILocalModelManager localModelManager,
        BergamotModelStore offlineModels,
        IEnumerable<IRealtimeTranslationProvider> providers,
        OverlayViewModel overlay)
    {
        _capture = capture;
        _session = session;
        _transcript = transcript;
        _settingsService = settingsService;
        _localModelManager = localModelManager;
        _offlineModels = offlineModels;
        _offlineModels.PairInstalled += (_, _) => Dispatch(RefreshLocalModelStatus);
        _providers = providers.ToArray();
        _overlay = overlay;
        _session.StatusChanged += OnStatusChanged;
        _session.ProviderStateChanged += OnProviderStateChanged;
        _session.DiagnosticsChanged += OnDiagnosticsChanged;
        _transcript.SegmentChanged += OnSegmentChanged;
        _overlay.HideRequested += (_, _) => IsOverlayVisible = false;
        WhisperModels = localModelManager.AvailableWhisperModels;
    }

    public ObservableCollection<AudioDevice> AudioDevices { get; } = [];
    public ObservableCollection<TranscriptItem> History { get; } = [];
    public IReadOnlyList<LanguageOption> SourceLanguages { get; } = LanguageCatalog.SourceLanguages;
    public IReadOnlyList<LanguageOption> TargetLanguages { get; } = LanguageCatalog.TargetLanguages;
    public IReadOnlyList<WhisperModelInfo> WhisperModels { get; }

    public IReadOnlyList<ChoiceOption<TranslationMode>> Modes { get; } =
    [
        new(TranslationMode.Fast, "Anlık"),
        new(TranslationMode.Accuracy, "Dengeli (daha doğru)")
    ];

    public IReadOnlyList<ChoiceOption<string>> TranslationEngineOptions { get; } =
    [
        new(TranslationEngines.Online, "Çevrimiçi (en iyi kalite)"),
        new(TranslationEngines.Offline, "Yalnızca cihazda")
    ];

    public IReadOnlyList<ChoiceOption<bool>> FocusOptions { get; } =
    [
        new(true, "Yalnızca ana konuşmacı"),
        new(false, "Duyulan her şey (şarkılar dahil)")
    ];

    public IReadOnlyList<ProviderOption> Providers { get; } =
    [
        new("local", "Yerel (Whisper)"),
        new("azure", "Azure Speech"),
        new("mock", "Demo")
    ];

    public bool IsAzureProvider => SelectedProvider?.Id == "azure";
    public bool IsLocalProvider => SelectedProvider?.Id == "local";
    public bool IsDemoProvider => SelectedProvider?.Id == "mock";
    public bool CanEditSettings => !IsRunning && !IsBusy;
    public bool HasPartial => PartialTranslation.Length > 0 || PartialOriginal.Length > 0;
    public bool IsTranscriptEmpty => HistoryCount == 0;
    public string RunButtonText => IsBusy ? (_isStopping ? "Durduruluyor…" : "Başlatılıyor…") : IsRunning ? "Durdur" : "Başlat";

    public event EventHandler<bool>? OverlayVisibilityChanged;
    public event EventHandler? TranscriptUpdated;

    public async Task InitializeAsync()
    {
        _settings = await _settingsService.LoadAsync().ConfigureAwait(true);
        ModelDirectory = string.IsNullOrWhiteSpace(_settings.LocalModelDirectory)
            ? @"D:\LinguaFlowModels"
            : _settings.LocalModelDirectory;
        try
        {
            _localModelManager.ModelDirectory = ModelDirectory;
        }
        catch (InvalidOperationException exception)
        {
            LocalModelStatus = exception.Message;
        }

        SelectedWhisperModel = WhisperModels.FirstOrDefault(model => model.Id == _settings.WhisperModelId) ?? WhisperModels[0];
        SelectedProvider = Providers.FirstOrDefault(provider => provider.Id == _settings.ProviderId) ?? Providers[0];
        SelectedSourceLanguage = SourceLanguages.FirstOrDefault(language => language.Code == _settings.SourceLanguage) ?? SourceLanguages[0];
        SelectedTargetLanguage = TargetLanguages.FirstOrDefault(language => language.Code == _settings.TargetLanguage) ?? TargetLanguages[0];
        SelectedMode = Modes.FirstOrDefault(mode => mode.Value == _settings.TranslationMode) ?? Modes[0];
        SelectedFocusOption = FocusOptions.First(option => option.Value == _settings.FocusMainSpeaker);
        SelectedTranslationEngine = TranslationEngineOptions.FirstOrDefault(engine => engine.Value == _settings.TranslationEngine)
            ?? TranslationEngineOptions[0];
        AzureRegion = string.IsNullOrWhiteSpace(_settings.AzureRegion)
            ? Environment.GetEnvironmentVariable("LINGUAFLOW_AZURE_SPEECH_REGION") ?? string.Empty
            : _settings.AzureRegion;
        AzureKey = Environment.GetEnvironmentVariable("LINGUAFLOW_AZURE_SPEECH_KEY") ?? string.Empty;
        IsOverlayVisible = _settings.OverlayVisible;
        _overlay.ApplySettings(_settings);
        RefreshDevices();
        SelectedAudioDevice = AudioDevices.FirstOrDefault(device => device.Id == _settings.AudioDeviceId)
            ?? AudioDevices.FirstOrDefault(device => device.IsDefault)
            ?? AudioDevices.FirstOrDefault();
        _initialized = true;
        RefreshLocalModelStatus();
        // İlk kurulumda indirme düğmesi görünsün.
        IsSettingsOpen = IsLocalProvider && !ModelsReady;
        StartWarmup();
        if (IsLocalProvider && ModelsReady)
        {
            _ = PrefetchOfflinePacksAsync();
        }
    }

    partial void OnSelectedProviderChanged(ProviderOption? value) => StartWarmup();

    partial void OnSelectedWhisperModelChanged(WhisperModelInfo? value)
    {
        if (value is null)
        {
            return;
        }

        _localModelManager.SelectedWhisperModel = value;
        if (_initialized)
        {
            RefreshLocalModelStatus();
            StartWarmup();
        }
    }

    partial void OnSelectedTargetLanguageChanged(LanguageOption? value) =>
        _overlay.TargetLanguageLabel = value is null ? string.Empty : LanguageCatalog.BaseCode(value.Code).ToUpperInvariant();

    [RelayCommand]
    private void RefreshDevices()
    {
        try
        {
            var selectedId = SelectedAudioDevice?.Id;
            AudioDevices.Clear();
            foreach (var device in _capture.GetPlaybackDevices())
            {
                AudioDevices.Add(device);
            }

            SelectedAudioDevice = AudioDevices.FirstOrDefault(device => device.Id == selectedId)
                ?? AudioDevices.FirstOrDefault(device => device.IsDefault)
                ?? AudioDevices.FirstOrDefault();
            if (AudioDevices.Count == 0)
            {
                SetError("Aktif ses çıkış cihazı bulunamadı.");
            }
        }
        catch (Exception exception)
        {
            SetError($"Ses cihazları okunamadı: {exception.Message}");
        }
    }

    private bool CanToggleRun() =>
        !IsBusy && !IsDownloadingModels && SelectedProvider is not null && (IsRunning || SelectedAudioDevice is not null);

    [RelayCommand(CanExecute = nameof(CanToggleRun))]
    private async Task ToggleRunAsync()
    {
        if (IsRunning)
        {
            await StopAsync().ConfigureAwait(true);
        }
        else
        {
            await StartAsync().ConfigureAwait(true);
        }
    }

    private async Task StartAsync()
    {
        if (SelectedAudioDevice is null || SelectedProvider is null)
        {
            return;
        }

        if (IsAzureProvider && (string.IsNullOrWhiteSpace(AzureKey) || string.IsNullOrWhiteSpace(AzureRegion)))
        {
            SetError("Azure Speech anahtarı ve bölgesi gerekli. Yerel Whisper anahtarsız ve ücretsiz çalışır.");
            return;
        }

        if (IsLocalProvider && !_localModelManager.GetStatus().IsReady)
        {
            SetError("Yerel modeller eksik. Ayarlar'daki ‘Modelleri indir’ düğmesine basın.");
            return;
        }

        try
        {
            IsBusy = true;
            HasError = false;
            CancelWarmup();
            ClearTranscript();
            StatusText = "Başlatılıyor…";
            _overlay.IsDemo = IsDemoProvider;
            _overlay.ShowListening();
            await _session.StartAsync(new TranslationSessionOptions(
                SelectedAudioDevice.Id,
                SelectedSourceLanguage?.Code ?? LanguageCatalog.Auto,
                SelectedTargetLanguage?.Code ?? "tr",
                SelectedMode?.Value ?? TranslationMode.Fast,
                SelectedProvider.Id,
                AzureKey,
                AzureRegion,
                SelectedTranslationEngine?.Value ?? TranslationEngines.Online,
                SelectedFocusOption?.Value ?? true), CancellationToken.None).ConfigureAwait(true);
            IsRunning = true;
            await SaveSettingsAsync().ConfigureAwait(true);
            if (IsLocalProvider)
            {
                _ = PrefetchOfflinePacksAsync();
            }
        }
        catch (Exception exception)
        {
            IsRunning = false;
            SetError($"Başlatılamadı: {exception.Message}");
            _overlay.ShowIdle();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        try
        {
            _isStopping = true;
            IsBusy = true;
            await _session.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            SetError($"Durdurulurken hata: {exception.Message}");
        }
        finally
        {
            IsRunning = false;
            _isStopping = false;
            IsBusy = false;
            _overlay.ShowIdle();
        }
    }

    private bool CanDownloadModels() => !IsDownloadingModels && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanDownloadModels))]
    private async Task DownloadModelsAsync()
    {
        try
        {
            IsDownloadingModels = true;
            DownloadProgress = 0;
            _localModelManager.ModelDirectory = ModelDirectory;
            var progress = new Progress<LocalModelDownloadProgress>(value =>
            {
                var fraction = value.TotalBytes is > 0 ? value.BytesReceived / (double)value.TotalBytes.Value : 0;
                DownloadProgress = (value.CompletedFiles + Math.Clamp(fraction, 0, 1)) * 100d / Math.Max(1, value.TotalFiles);
                var size = value.TotalBytes is > 0
                    ? $" — {value.BytesReceived / 1_048_576d:N0} / {value.TotalBytes.Value / 1_048_576d:N0} MB"
                    : string.Empty;
                LocalModelStatus = $"İndiriliyor ({Math.Min(value.CompletedFiles + 1, value.TotalFiles)}/{value.TotalFiles}): {value.FileName}{size}";
            });
            await _localModelManager.DownloadModelsAsync(progress).ConfigureAwait(true);
            // İnternet kesilse ya da çevrimiçi servis sınırlasa bile çeviri sürsün diye cihazdaki dil paketleri.
            LocalModelStatus = "Çevrimdışı çeviri paketleri indiriliyor…";
            await PrefetchOfflinePacksAsync().ConfigureAwait(true);
            DownloadProgress = 100;
            RefreshLocalModelStatus();
            StatusText = "Modeller hazır.";
            StartWarmup();
        }
        catch (Exception exception)
        {
            LocalModelStatus = $"Model indirilemedi: {exception.Message}";
        }
        finally
        {
            IsDownloadingModels = false;
        }
    }

    private void RefreshLocalModelStatus()
    {
        var status = _localModelManager.GetStatus();
        ModelsReady = status.IsReady;
        if (status.IsReady)
        {
            var pairs = _offlineModels.InstalledPairs();
            LocalModelStatus = pairs.Count == 0
                ? "Hazır"
                : $"Hazır · cihazdaki çeviri: {string.Join(", ", pairs)}";
            return;
        }

        LocalModelStatus = $"İndirilmedi ({status.WhisperModel.SizeBytes / 1_048_576d:N0} MB, {status.ModelDirectory})";
    }

    private Task<bool[]> PrefetchOfflinePacksAsync()
    {
        var target = SelectedTargetLanguage?.Code ?? "tr";
        var source = SelectedSourceLanguage?.Code ?? LanguageCatalog.Auto;
        // Otomatik dilde en olası kaynak İngilizcedir; diğer diller algılandıkça arka planda iner.
        return Task.WhenAll(
            _offlineModels.EnsureRouteAsync("en", target),
            _offlineModels.EnsureRouteAsync(source == LanguageCatalog.Auto ? "en" : source, target));
    }

    private void StartWarmup()
    {
        if (!_initialized || !IsLocalProvider || IsRunning || !_localModelManager.GetStatus().IsReady)
        {
            return;
        }

        var provider = _providers.FirstOrDefault(item => item.Id == "local") as IWarmupCapable;
        if (provider is null)
        {
            return;
        }

        CancelWarmup();
        var cancellation = new CancellationTokenSource();
        _warmupCancellation = cancellation;
        StatusText = "Model hazırlanıyor…";
        _ = Task.Run(async () =>
        {
            try
            {
                await provider.WarmUpAsync(cancellation.Token).ConfigureAwait(false);
                Dispatch(() =>
                {
                    if (!IsRunning && !IsBusy)
                    {
                        StatusText = "Hazır";
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Dispatch(() => SetError($"Model yüklenemedi: {exception.Message}"));
            }
        }, CancellationToken.None);
    }

    private void CancelWarmup()
    {
        _warmupCancellation?.Cancel();
        _warmupCancellation = null;
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void ToggleOverlay()
    {
        IsOverlayVisible = !IsOverlayVisible;
    }

    partial void OnIsOverlayVisibleChanged(bool value) => OverlayVisibilityChanged?.Invoke(this, value);

    [RelayCommand]
    private void ClearHistory()
    {
        ClearTranscript();
        if (!IsRunning)
        {
            _overlay.ShowIdle();
        }
    }

    [RelayCommand]
    private void CopyTranscript()
    {
        if (History.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var item in History)
        {
            builder.Append('[').Append(item.Time).Append("] ").AppendLine(item.Translation);
            if (item.ShowOriginal)
            {
                builder.Append("        ").AppendLine(item.Original);
            }
        }

        try
        {
            Clipboard.SetText(builder.ToString());
            StatusText = "Döküm panoya kopyalandı";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetError("Panoya kopyalanamadı; başka bir uygulama panoyu kullanıyor olabilir.");
        }
    }

    private void ClearTranscript()
    {
        History.Clear();
        HistoryCount = 0;
        PartialOriginal = string.Empty;
        PartialTranslation = string.Empty;
        DetectedLanguageLabel = "—";
        _overlay.Clear();
    }

    public async Task ShutdownAsync()
    {
        CancelWarmup();
        if (_session.Status != TranslationStatus.Idle)
        {
            await _session.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private Task SaveSettingsAsync()
    {
        _settings.AudioDeviceId = SelectedAudioDevice?.Id;
        _settings.ProviderId = SelectedProvider?.Id ?? "local";
        _settings.SourceLanguage = SelectedSourceLanguage?.Code ?? LanguageCatalog.Auto;
        _settings.TargetLanguage = SelectedTargetLanguage?.Code ?? "tr";
        _settings.TranslationEngine = SelectedTranslationEngine?.Value ?? TranslationEngines.Online;
        _settings.FocusMainSpeaker = SelectedFocusOption?.Value ?? true;
        _settings.WhisperModelId = SelectedWhisperModel?.Id ?? "turbo";
        _settings.LocalModelDirectory = ModelDirectory;
        _settings.TranslationMode = SelectedMode?.Value ?? TranslationMode.Fast;
        _settings.AzureRegion = AzureRegion;
        _settings.OverlayVisible = IsOverlayVisible;
        _overlay.WriteSettings(_settings);
        return _settingsService.SaveAsync(_settings);
    }

    private void SetError(string message)
    {
        StatusText = message;
        HasError = true;
    }

    private void OnStatusChanged(object? sender, TranslationStatus status) => Dispatch(() =>
    {
        if (status is TranslationStatus.Starting or TranslationStatus.Listening)
        {
            HasError = false;
        }

        StatusText = status switch
        {
            TranslationStatus.Starting => "Başlatılıyor…",
            TranslationStatus.Listening => "Dinleniyor",
            TranslationStatus.Stopping => "Durduruluyor…",
            TranslationStatus.Faulted => HasError ? StatusText : "Akış bir hata nedeniyle durdu",
            _ => HasError ? StatusText : "Hazır"
        };
        if (status == TranslationStatus.Faulted)
        {
            IsRunning = false;
            HasError = true;
            _overlay.ShowIdle();
            // Yakalama ve sağlayıcıyı temizle ki yeni bir oturum başlatılabilsin.
            _ = _session.StopAsync(CancellationToken.None);
        }
        else if (status == TranslationStatus.Listening)
        {
            IsRunning = true;
        }
    });

    private void OnProviderStateChanged(object? sender, ProviderStateChange args) => Dispatch(() =>
    {
        ProviderStatus = args.Message;
        if (args.State == ProviderConnectionState.Error)
        {
            SetError(args.Message);
        }
    });

    private void OnDiagnosticsChanged(object? sender, SessionDiagnostics diagnostics)
    {
        // Ses pompası saniyede ~20 kez tetikler; arayüzü yormamak için seyrelt.
        if (diagnostics.CapturedFrames % 20 != 0)
        {
            return;
        }

        Dispatch(() => DiagnosticsText = diagnostics.DroppedFrames > 0
            ? $"{diagnostics.DroppedFrames:N0} ses çerçevesi kaybedildi"
            : string.Empty);
    }

    private void OnSegmentChanged(object? sender, TranscriptSegment segment) => Dispatch(() =>
    {
        if (!string.IsNullOrWhiteSpace(segment.DetectedLanguage))
        {
            DetectedLanguageLabel = LanguageCatalog.DisplayName(segment.DetectedLanguage);
        }

        if (segment.IsFinal)
        {
            History.Add(new TranscriptItem(
                segment.SegmentId,
                segment.TranslatedText,
                segment.OriginalText,
                LanguageCatalog.BaseCode(segment.DetectedLanguage).ToUpperInvariant(),
                segment.ReceivedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
            while (History.Count > MaximumHistory)
            {
                History.RemoveAt(0);
            }

            HistoryCount = History.Count;
        }

        var partial = _transcript.PartialSegment;
        PartialOriginal = partial?.OriginalText ?? string.Empty;
        PartialTranslation = partial?.TranslatedText ?? string.Empty;
        _overlay.Update(segment, partial);
        TranscriptUpdated?.Invoke(this, EventArgs.Empty);
    });

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.InvokeAsync(action);
        }
    }
}

public sealed record ProviderOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ChoiceOption<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

public sealed record TranscriptItem(string Id, string TranslatedText, string Original, string Language, string Time)
{
    /// <summary>Çeviri alınamadıysa özgün metni ana satırda göster.</summary>
    public string Translation => string.IsNullOrWhiteSpace(TranslatedText) ? Original : TranslatedText;

    public bool ShowOriginal => !string.IsNullOrWhiteSpace(TranslatedText) &&
        !string.Equals(TranslatedText.Trim(), Original.Trim(), StringComparison.Ordinal);

    public string OriginalLine => ShowOriginal ? $"{Language}  {Original}" : Language;
}
