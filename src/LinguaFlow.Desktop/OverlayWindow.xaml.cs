using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LinguaFlow.Desktop;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _viewModel;
    private bool _allowClose;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        LocationChanged += OnLocationChanged;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var area = SystemParameters.WorkArea;
        var left = _viewModel.Left;
        var top = _viewModel.Top;
        var onScreen = !double.IsNaN(left) && !double.IsNaN(top) &&
            left > SystemParameters.VirtualScreenLeft - 100 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top > SystemParameters.VirtualScreenTop - 20 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;
        if (!onScreen)
        {
            // Varsayılan: birincil ekranın altında, ortalanmış — film altyazısı gibi.
            Width = Math.Min(_viewModel.Width, area.Width - 80);
            left = area.Left + ((area.Width - Width) / 2);
            top = area.Bottom - 220;
        }

        Left = left;
        Top = top;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Thumb)
        {
            DragMove();
        }
    }

    private void OnResizeRight(object sender, DragDeltaEventArgs e) =>
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);

    private void OnResizeLeft(object sender, DragDeltaEventArgs e)
    {
        var width = Math.Max(MinWidth, Width - e.HorizontalChange);
        Left += Width - width;
        Width = width;
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _viewModel.Left = Left;
            _viewModel.Top = Top;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState == WindowState.Normal && e.WidthChanged)
        {
            _viewModel.Width = ActualWidth;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(Hide);
        }
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }
}
