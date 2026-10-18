using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace LinguaFlow.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly OverlayWindow _overlayWindow;
    private bool _shutdownApproved;
    private bool _scrollQueued;

    public MainWindow(MainViewModel viewModel, OverlayWindow overlayWindow)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _overlayWindow = overlayWindow;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        _viewModel.OverlayVisibilityChanged += OnOverlayVisibilityChanged;
        _viewModel.TranscriptUpdated += OnTranscriptUpdated;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync().ConfigureAwait(true);
        AzureKeyBox.Password = _viewModel.AzureKey;
        if (_viewModel.IsOverlayVisible)
        {
            _overlayWindow.Show();
        }
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

    private void OnTranscriptUpdated(object? sender, EventArgs e)
    {
        // Kullanıcı geçmişi okumak için yukarı kaydırdıysa onu aşağı çekme.
        var nearBottom = TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 80;
        if (!nearBottom || _scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollQueued = false;
            TranscriptScroll.ScrollToEnd();
        });
    }

    private void OnAzureKeyChanged(object sender, RoutedEventArgs e) =>
        _viewModel.AzureKey = AzureKeyBox.Password;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // WindowStyle=None ile ekranı kaplayan pencere kenarlardan taşar; çerçeve kalınlığı kadar içeri al.
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOverlayVisibilityChanged(object? sender, bool visible)
    {
        if (!IsLoaded)
        {
            return;
        }

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
