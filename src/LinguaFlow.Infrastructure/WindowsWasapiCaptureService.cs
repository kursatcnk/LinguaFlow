using System.Threading.Channels;
using LinguaFlow.Core;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LinguaFlow.Infrastructure;

public sealed partial class WindowsWasapiCaptureService : IAudioCaptureService
{
    private const int ChannelCapacity = 64;
    private readonly ILogger<WindowsWasapiCaptureService> _logger;
    private readonly object _gate = new();
    private Channel<AudioFrame> _channel = CreateChannel();
    private WasapiRecorder? _capture;
    private MMDevice? _activeDevice;
    private TaskCompletionSource? _stoppedSignal;
    private long _sequence;
    private long _capturedFrames;
    private long _droppedFrames;
    private bool _stopRequested;

    public WindowsWasapiCaptureService(ILogger<WindowsWasapiCaptureService> logger)
    {
        _logger = logger;
    }

    public ChannelReader<AudioFrame> Frames => _channel.Reader;
    public AudioFormat? CurrentFormat { get; private set; }
    public long CapturedFrames => Interlocked.Read(ref _capturedFrames);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public int BufferedFrames => _channel.Reader.CanCount ? _channel.Reader.Count : 0;

    public event EventHandler<string>? CaptureFaulted;

    public IReadOnlyList<AudioDevice> GetPlaybackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultDevice.ID))
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public Task StartAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("Ses yakalama zaten çalışıyor.");
            }

            var enumerator = new MMDeviceEnumerator();
            try
            {
                _activeDevice = string.IsNullOrWhiteSpace(deviceId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : enumerator.GetDevice(deviceId);
            }
            finally
            {
                enumerator.Dispose();
            }

            _channel = CreateChannel();
            _sequence = 0;
            Interlocked.Exchange(ref _capturedFrames, 0);
            Interlocked.Exchange(ref _droppedFrames, 0);
            _stopRequested = false;
            _stoppedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _capture = new WasapiRecorderBuilder()
                .WithDevice(_activeDevice)
                .WithLoopbackCapture()
                .WithBufferLength(50)
                .Build();
            var waveFormat = _capture.WaveFormat;
            CurrentFormat = new AudioFormat(
                waveFormat.SampleRate,
                waveFormat.BitsPerSample,
                waveFormat.Channels,
                waveFormat.AsStandardWaveFormat().Encoding == WaveFormatEncoding.IeeeFloat);

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            try
            {
                _capture.StartRecording();
                LogCaptureStarted(_logger, _activeDevice.FriendlyName, waveFormat);
            }
            catch
            {
                CleanupCapture();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        WasapiRecorder? capture;
        Task? stoppedTask;
        lock (_gate)
        {
            capture = _capture;
            if (capture is null)
            {
                return;
            }

            _stopRequested = true;
            stoppedTask = _stoppedSignal?.Task;
            capture.StopRecording();
        }

        if (stoppedTask is not null)
        {
            await stoppedTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        if (buffer.IsEmpty || CurrentFormat is null)
        {
            return;
        }

        var data = buffer.ToArray();
        var frame = new AudioFrame(
            data,
            CurrentFormat,
            DateTimeOffset.UtcNow,
            Interlocked.Increment(ref _sequence));

        if (_channel.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _capturedFrames);
            return;
        }

        var dropped = Interlocked.Increment(ref _droppedFrames);
        if (dropped == 1 || dropped % 25 == 0)
        {
            var message = $"Ses tamponu dolu; {dropped} çerçeve reddedildi. Oturum güvenilir değil.";
            LogBufferOverflow(_logger, message);
            CaptureFaulted?.Invoke(this, message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        var unexpected = !_stopRequested;
        var message = args.Exception?.Message ?? "Ses cihazı yakalamayı durdurdu.";
        lock (_gate)
        {
            _channel.Writer.TryComplete(args.Exception);
            CleanupCapture();
            _stoppedSignal?.TrySetResult();
        }

        if (unexpected)
        {
            LogCaptureStoppedUnexpectedly(_logger, args.Exception);
            CaptureFaulted?.Invoke(this, message);
        }
    }

    private void CleanupCapture()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _activeDevice?.Dispose();
        _activeDevice = null;
        CurrentFormat = null;
    }

    private static Channel<AudioFrame> CreateChannel() => Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "WASAPI loopback başladı. Device={Device}; Format={Format}")]
    private static partial void LogCaptureStarted(ILogger logger, string device, WaveFormat format);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogBufferOverflow(ILogger logger, string message);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "WASAPI loopback beklenmedik biçimde durdu.")]
    private static partial void LogCaptureStoppedUnexpectedly(ILogger logger, Exception? exception);
}
