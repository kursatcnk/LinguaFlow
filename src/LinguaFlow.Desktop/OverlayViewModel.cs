using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaFlow.Core;

namespace LinguaFlow.Desktop;

/// <summary>
/// Her zaman üstte duran altyazı şeridi: üstte bir önceki cümle (soluk), altta şu an konuşulan
/// cümlenin canlı çevirisi. Konuşma bir süre durunca şerit sadeleşir.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(8);
    private readonly DispatcherTimer _idleTimer;
    private string _lastFinalTranslation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    private string _previousText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent))]
    private string _currentText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOriginal))]
    private string _originalText = string.Empty;

    [ObservableProperty] private bool _isPartial;
    [ObservableProperty] private bool _isIdle = true;
    [ObservableProperty] private string _idleText = "LinguaFlow hazır";
    [ObservableProperty] private string _detectedLanguageLabel = string.Empty;
    [ObservableProperty] private string _targetLanguageLabel = "TR";
    [ObservableProperty] private bool _isDemo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOriginal))]
    private bool _showOriginal = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryFontSize))]
    [NotifyPropertyChangedFor(nameof(OriginalFontSize))]
    private double _fontSize = 30;

    [ObservableProperty] private double _backgroundOpacity = 0.82;
    [ObservableProperty] private double _left = double.NaN;
    [ObservableProperty] private double _top = double.NaN;
    [ObservableProperty] private double _width = 1100;

    public OverlayViewModel()
    {
        _idleTimer = new DispatcherTimer { Interval = IdleAfter };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            IsIdle = true;
        };
    }

    public bool HasPrevious => PreviousText.Length > 0;
    public bool HasCurrent => CurrentText.Length > 0;
    public bool HasOriginal => ShowOriginal && OriginalText.Length > 0;
    public double SecondaryFontSize => Math.Round(FontSize * 0.68);
    public double OriginalFontSize => Math.Max(12, Math.Round(FontSize * 0.5));

    public event EventHandler? HideRequested;

    public void Update(TranscriptSegment changed, TranscriptSegment? partial)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (!string.IsNullOrWhiteSpace(changed.DetectedLanguage))
        {
            DetectedLanguageLabel = LanguageCatalog.BaseCode(changed.DetectedLanguage).ToUpperInvariant();
        }

        if (changed.IsFinal)
        {
            var finalText = Display(changed);
            if (partial is null)
            {
                PreviousText = _lastFinalTranslation;
                CurrentText = finalText;
                OriginalText = string.IsNullOrWhiteSpace(changed.TranslatedText) ? string.Empty : changed.OriginalText;
                IsPartial = false;
            }
            else
            {
                PreviousText = finalText;
                ShowPartial(partial);
            }

            _lastFinalTranslation = finalText;
        }
        else if (partial is not null)
        {
            PreviousText = _lastFinalTranslation;
            ShowPartial(partial);
        }
        else
        {
            // Partial geri çekildi; son kesin cümleyi göster.
            PreviousText = string.Empty;
            CurrentText = _lastFinalTranslation;
            OriginalText = string.Empty;
            IsPartial = false;
        }

        IsIdle = CurrentText.Length == 0 && PreviousText.Length == 0;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    public void ShowListening()
    {
        IdleText = IsDemo ? "Demo modu • sahte veri" : "Dinleniyor… konuşma bekleniyor";
        IsIdle = true;
    }

    public void ShowIdle()
    {
        IdleText = "LinguaFlow hazır";
        IsIdle = CurrentText.Length == 0;
    }

    public void Clear()
    {
        _idleTimer.Stop();
        _lastFinalTranslation = string.Empty;
        PreviousText = string.Empty;
        CurrentText = string.Empty;
        OriginalText = string.Empty;
        DetectedLanguageLabel = string.Empty;
        IsPartial = false;
        IsIdle = true;
    }

    public void ApplySettings(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FontSize = Math.Clamp(settings.OverlayFontSize, 16, 64);
        BackgroundOpacity = Math.Clamp(settings.OverlayOpacity, 0.3, 1);
        ShowOriginal = settings.ShowOriginalText;
        Width = Math.Clamp(settings.OverlayWidth, 480, 3000);
        // Eski sürüm konum seçilmemişken 100,100 kaydediyordu; bunu "konum yok" say ki şerit ekranın altına otursun.
        var legacyDefault = settings.OverlayLeft is 100 && settings.OverlayTop is 100;
        Left = legacyDefault ? double.NaN : settings.OverlayLeft ?? double.NaN;
        Top = legacyDefault ? double.NaN : settings.OverlayTop ?? double.NaN;
        TargetLanguageLabel = LanguageCatalog.BaseCode(settings.TargetLanguage).ToUpperInvariant();
    }

    public void WriteSettings(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.OverlayFontSize = FontSize;
        settings.OverlayOpacity = BackgroundOpacity;
        settings.ShowOriginalText = ShowOriginal;
        settings.OverlayWidth = Width;
        settings.OverlayLeft = double.IsNaN(Left) ? null : Left;
        settings.OverlayTop = double.IsNaN(Top) ? null : Top;
    }

    [RelayCommand]
    private void IncreaseFont() => FontSize = Math.Min(64, FontSize + 2);

    [RelayCommand]
    private void DecreaseFont() => FontSize = Math.Max(16, FontSize - 2);

    [RelayCommand]
    private void ToggleOriginal() => ShowOriginal = !ShowOriginal;

    [RelayCommand]
    private void ToggleBackground() => BackgroundOpacity = BackgroundOpacity > 0.6 ? 0.45 : 0.82;

    [RelayCommand]
    private void Hide() => HideRequested?.Invoke(this, EventArgs.Empty);

    private void ShowPartial(TranscriptSegment partial)
    {
        CurrentText = Display(partial);
        OriginalText = string.IsNullOrWhiteSpace(partial.TranslatedText) ? string.Empty : partial.OriginalText;
        IsPartial = true;
    }

    private static string Display(TranscriptSegment segment) =>
        string.IsNullOrWhiteSpace(segment.TranslatedText) ? segment.OriginalText : segment.TranslatedText;
}
