using System.ComponentModel;
using System.Windows;
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
        LocationChanged += OnLocationChanged;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
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
        if (WindowState == WindowState.Normal)
        {
            _viewModel.Width = ActualWidth;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && Owner?.IsVisible == true)
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
