using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaFlow.Core;

namespace LinguaFlow.Desktop;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAudioCaptureService _capture;
    private readonly ITranslationSessionService _session;
    private readonly ITranscriptProcessor _transcript;
    private readonly ISettingsService _settingsService;
    private readonly ILocalModelManager _localModelManager;
    private readonly OverlayViewModel _overlay;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioDevice? _selectedAudioDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private ProviderOption? _selectedProvider;
    [ObservableProperty] private TranslationMode _selectedMode = TranslationMode.Fast;
    [ObservableProperty] private string _azureKey = string.Empty;
    [ObservableProperty] private string _azureRegion = string.Empty;
    [ObservableProperty] private string _statusText = "Hazır";
    [ObservableProperty] private string _providerStatus = "Bağlı değil";
    [ObservableProperty] private string _currentOriginal = "Bir ses kaynağı seçin ve çeviriyi başlatın.";
    [ObservableProperty] private string _currentTranslation = "Başlamaya hazır.";
    [ObservableProperty] private string _diagnosticsText = "Henüz ölçüm yok";
    [ObservableProperty] private bool _isOverlayVisible = true;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelsCommand))]
    private bool _isDownloadingModels;
    [ObservableProperty] private string _localModelStatus = "Yerel modeller kontrol ediliyor…";
    [ObservableProperty] private string _modelDirectory = @"D:\LinguaFlowModels";

    public MainViewModel(
        IAudioCaptureService capture,
        ITranslationSessionService session,
        ITranscriptProcessor transcript,
        ISettingsService settingsService,
        ILocalModelManager localModelManager,
        OverlayViewModel overlay)
    {
        _capture = capture;
        _session = session;
        _transcript = transcript;
        _settingsService = settingsService;
        _localModelManager = localModelManager;
        _overlay = overlay;
        _session.StatusChanged += OnStatusChanged;
        _session.ProviderStateChanged += OnProviderStateChanged;
        _session.DiagnosticsChanged += OnDiagnosticsChanged;
        _transcript.SegmentChanged += OnSegmentChanged;
    }

    public ObservableCollection<AudioDevice> AudioDevices { get; } = [];
    public ObservableCollection<TranscriptSegment> History { get; } = [];
    public IReadOnlyList<TranslationMode> Modes { get; } = Enum.GetValues<TranslationMode>();
    public IReadOnlyList<ProviderOption> Providers { get; } =
    [
        new("local", "Yerel Ücretsiz — cihazda"),
        new("azure", "Azure Speech — bulut"),
        new("mock", "Demo — sahte veri")
    ];
    public bool IsAzureProvider => SelectedProvider?.Id == "azure";
    public bool IsLocalProvider => SelectedProvider?.Id == "local";
    public bool IsDemoProvider => SelectedProvider?.Id == "mock";
    public event EventHandler<bool>? OverlayVisibilityChanged;

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync().ConfigureAwait(true);
        ModelDirectory = string.IsNullOrWhiteSpace(settings.LocalModelDirectory)
            ? @"D:\LinguaFlowModels"
            : settings.LocalModelDirectory;
        _localModelManager.ModelDirectory = ModelDirectory;
        SelectedProvider = Providers.FirstOrDefault(provider => provider.Id == settings.ProviderId) ?? Providers[0];
        AzureRegion = string.IsNullOrWhiteSpace(settings.AzureRegion)
            ? Environment.GetEnvironmentVariable("LINGUAFLOW_AZURE_SPEECH_REGION") ?? string.Empty
            : settings.AzureRegion;
        SelectedMode = settings.TranslationMode;
        AzureKey = Environment.GetEnvironmentVariable("LINGUAFLOW_AZURE_SPEECH_KEY") ?? string.Empty;
        _overlay.ApplySettings(settings);
        RefreshDevices();
        SelectedAudioDevice = AudioDevices.FirstOrDefault(device => device.Id == settings.AudioDeviceId)
            ?? AudioDevices.FirstOrDefault(device => device.IsDefault)
            ?? AudioDevices.FirstOrDefault();
        RefreshLocalModelStatus();
    }

    partial void OnSelectedProviderChanged(ProviderOption? value)
    {
        OnPropertyChanged(nameof(IsAzureProvider));
        OnPropertyChanged(nameof(IsLocalProvider));
        OnPropertyChanged(nameof(IsDemoProvider));
    }

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
            StatusText = AudioDevices.Count == 0 ? "Aktif ses çıkış cihazı bulunamadı." : "Hazır";
        }
        catch (Exception exception)
        {
            StatusText = $"Ses cihazları okunamadı: {exception.Message}";
        }
    }

    private bool CanStart() => _session.Status == TranslationStatus.Idle && SelectedAudioDevice is not null && SelectedProvider is not null && !IsDownloadingModels;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedAudioDevice is null)
        {
            return;
        }

        if (SelectedProvider?.Id == "azure" && (string.IsNullOrWhiteSpace(AzureKey) || string.IsNullOrWhiteSpace(AzureRegion)))
        {
            StatusText = "Azure Speech anahtarı ve bölgesi gerekli. Yerel Ücretsiz modu anahtarsız çalışır.";
            return;
        }

        if (SelectedProvider?.Id == "local" && !_localModelManager.GetStatus().IsReady)
        {
            StatusText = "Yerel modeller eksik. Önce Modelleri İndir düğmesine basın.";
            return;
        }

        try
        {
            History.Clear();
            CurrentOriginal = "İngilizce konuşma algılandığında özgün metin burada görünecek.";
            CurrentTranslation = "Sistem sesi bekleniyor…";
            _overlay.IsDemo = SelectedProvider?.Id == "mock";
            await _session.StartAsync(new TranslationSessionOptions(
                SelectedAudioDevice.Id,
                "en-US",
                "tr",
                SelectedMode,
                SelectedProvider?.Id ?? "local",
                AzureKey,
                AzureRegion), CancellationToken.None).ConfigureAwait(true);
            IsRunning = true;
            await SaveSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            IsRunning = false;
            StatusText = $"Başlatılamadı: {exception.Message}";
        }
    }

    private bool CanDownloadModels() => !IsDownloadingModels && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanDownloadModels))]
    private async Task DownloadModelsAsync()
    {
        try
        {
            IsDownloadingModels = true;
            StartCommand.NotifyCanExecuteChanged();
            _localModelManager.ModelDirectory = ModelDirectory;
            var progress = new Progress<LocalModelDownloadProgress>(value =>
            {
                var percent = value.TotalBytes is > 0
                    ? $" %{value.BytesReceived * 100d / value.TotalBytes.Value:N0}"
                    : string.Empty;
                LocalModelStatus = $"İndiriliyor ({value.CompletedFiles + 1}/{value.TotalFiles}): {value.FileName}{percent}";
            });
            await _localModelManager.DownloadModelsAsync(progress).ConfigureAwait(true);
            RefreshLocalModelStatus();
            StatusText = "Yerel modeller hazır. Artık ücretsiz ve çevrimdışı çalışabilir.";
        }
        catch (Exception exception)
        {
            LocalModelStatus = $"Model indirilemedi: {exception.Message}";
        }
        finally
        {
            IsDownloadingModels = false;
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshLocalModelStatus()
    {
        var status = _localModelManager.GetStatus();
        LocalModelStatus = status.IsReady
            ? $"Hazır • {status.ModelDirectory}"
            : $"Eksik • modeller {status.ModelDirectory} klasörüne indirilecek (yaklaşık 170 MB)";
    }

    private bool CanStop() => _session.Status != TranslationStatus.Idle;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try
        {
            await _session.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void ToggleOverlay()
    {
        IsOverlayVisible = !IsOverlayVisible;
        OverlayVisibilityChanged?.Invoke(this, IsOverlayVisible);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        CurrentOriginal = "Bir ses kaynağı seçin ve çeviriyi başlatın.";
        CurrentTranslation = "Başlamaya hazır.";
        _overlay.Clear();
    }

    public async Task ShutdownAsync()
    {
        if (_session.Status != TranslationStatus.Idle)
        {
            await _session.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private Task SaveSettingsAsync() => _settingsService.SaveAsync(new ApplicationSettings
    {
        AudioDeviceId = SelectedAudioDevice?.Id,
        ProviderId = SelectedProvider?.Id ?? "local",
        LocalModelDirectory = ModelDirectory,
        TranslationMode = SelectedMode,
        AzureRegion = AzureRegion,
        OverlayLeft = _overlay.Left,
        OverlayTop = _overlay.Top,
        OverlayWidth = _overlay.Width,
        OverlayOpacity = _overlay.Opacity,
        OverlayFontSize = _overlay.FontSize
    });

    private void OnStatusChanged(object? sender, TranslationStatus status) => Dispatch(() =>
    {
        StatusText = status switch
        {
            TranslationStatus.Starting => "Başlatılıyor…",
            TranslationStatus.Listening => "Dinleniyor ve çevriliyor",
            TranslationStatus.Stopping => "Durduruluyor…",
            TranslationStatus.Faulted => "Akış hata nedeniyle durdu",
            _ => "Hazır"
        };
        IsRunning = status is TranslationStatus.Starting or TranslationStatus.Listening or TranslationStatus.Reconnecting;
    });

    private void OnProviderStateChanged(object? sender, ProviderStateChange args) =>
        Dispatch(() => ProviderStatus = args.Message);

    private void OnDiagnosticsChanged(object? sender, SessionDiagnostics diagnostics) => Dispatch(() =>
    {
        DiagnosticsText = $"Frame {diagnostics.CapturedFrames:N0}  •  Dropped {diagnostics.DroppedFrames:N0}  •  " +
            $"Buffer {diagnostics.BufferedFrames}  •  Capture→Provider {diagnostics.LastCaptureToProviderLatency.TotalMilliseconds:N0} ms";
    });

    private void OnSegmentChanged(object? sender, TranscriptSegment segment) => Dispatch(() =>
    {
        CurrentOriginal = segment.OriginalText;
        CurrentTranslation = segment.TranslatedText;
        _overlay.Update(segment);
        if (segment.IsFinal)
        {
            History.Insert(0, segment);
            while (History.Count > 500)
            {
                History.RemoveAt(History.Count - 1);
            }
        }
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

public sealed record ProviderOption(string Id, string Name);
