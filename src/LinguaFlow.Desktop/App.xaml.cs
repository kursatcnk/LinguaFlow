using System.Windows;
using LinguaFlow.Application;
using LinguaFlow.Core;
using LinguaFlow.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinguaFlow.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IAudioCaptureService, WindowsWasapiCaptureService>();
                services.AddSingleton<IAudioProcessingService, AudioProcessingService>();
                services.AddSingleton<ITranscriptProcessor, TranscriptProcessor>();
                services.AddSingleton<ISettingsService, JsonSettingsService>();
                services.AddSingleton<ILocalModelManager, LocalModelManager>();
                services.AddSingleton<IRealtimeTranslationProvider, LocalWhisperTranslationProvider>();
                services.AddSingleton<IRealtimeTranslationProvider, AzureSpeechTranslationProvider>();
                services.AddSingleton<IRealtimeTranslationProvider, MockTranslationProvider>();
                services.AddSingleton<ITranslationSessionService, TranslationSessionService>();
                services.AddSingleton<OverlayViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<OverlayWindow>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync().ConfigureAwait(true);
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (_host is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else
            {
                _host.Dispose();
            }
        }

        base.OnExit(e);
    }
}
