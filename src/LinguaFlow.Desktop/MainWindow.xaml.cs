using System.ComponentModel;
using System.Windows;

namespace LinguaFlow.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly OverlayWindow _overlayWindow;
    private bool _shutdownApproved;

    public MainWindow(MainViewModel viewModel, OverlayWindow overlayWindow)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _overlayWindow = overlayWindow;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        _viewModel.OverlayVisibilityChanged += OnOverlayVisibilityChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync().ConfigureAwait(true);
        AzureKeyBox.Password = _viewModel.AzureKey;
        _overlayWindow.Owner = this;
        _overlayWindow.Show();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownApproved)
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        try
        {
            await _viewModel.ShutdownAsync().ConfigureAwait(true);
            _shutdownApproved = true;
            _overlayWindow.CloseForShutdown();
            Close();
        }
        catch (Exception exception)
        {
            IsEnabled = true;
            MessageBox.Show(this, exception.Message, "LinguaFlow kapatılamadı", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnAzureKeyChanged(object sender, RoutedEventArgs e) =>
        _viewModel.AzureKey = AzureKeyBox.Password;

    private void OnTitleBarMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOverlayVisibilityChanged(object? sender, bool visible)
    {
        if (visible)
        {
            _overlayWindow.Show();
        }
        else
        {
            _overlayWindow.Hide();
        }
    }
}
