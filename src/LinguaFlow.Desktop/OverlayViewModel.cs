using CommunityToolkit.Mvvm.ComponentModel;
using LinguaFlow.Core;

namespace LinguaFlow.Desktop;

public sealed partial class OverlayViewModel : ObservableObject
{
    [ObservableProperty] private string _originalText = string.Empty;
    [ObservableProperty] private string _translatedText = "LinguaFlow hazır";
    [ObservableProperty] private bool _isDemo;
    [ObservableProperty] private double _opacity = 0.9;
    [ObservableProperty] private double _fontSize = 26;
    [ObservableProperty] private double _left = 100;
    [ObservableProperty] private double _top = 100;
    [ObservableProperty] private double _width = 900;

    public void Update(TranscriptSegment segment)
    {
        OriginalText = segment.OriginalText;
        TranslatedText = segment.TranslatedText;
    }

    public void Clear()
    {
        OriginalText = string.Empty;
        TranslatedText = "LinguaFlow hazır";
    }

    public void ApplySettings(ApplicationSettings settings)
    {
        Opacity = settings.OverlayOpacity;
        FontSize = settings.OverlayFontSize;
        Left = settings.OverlayLeft ?? 100;
        Top = settings.OverlayTop ?? 100;
        Width = settings.OverlayWidth;
    }
}
