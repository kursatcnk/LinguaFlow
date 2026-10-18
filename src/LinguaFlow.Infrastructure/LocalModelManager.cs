using LinguaFlow.Core;
using Microsoft.Extensions.Logging;

namespace LinguaFlow.Infrastructure;

/// <summary>Konuşma tanıma (Whisper) ve konuşma algılama (Silero VAD) modellerini yönetir.</summary>
public sealed class LocalModelManager : ILocalModelManager
{
    public const string VadFileName = "ggml-silero-v5.1.2.bin";
    private const string DefaultModelDirectory = @"D:\LinguaFlowModels";
    private const string WhisperBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    private static readonly WhisperModelInfo TurboModel =
        new("turbo", "Large v3 Turbo (en doğru)", "ggml-large-v3-turbo-q5_0.bin", 574_041_195);

    private static readonly WhisperModelInfo SmallModel =
        new("small", "Small (hafif)", "ggml-small-q5_1.bin", 190_085_487);

    private static readonly Dictionary<string, ModelDownload> WhisperDownloads = new(StringComparer.OrdinalIgnoreCase)
    {
        [TurboModel.Id] = new(TurboModel.FileName,
            new Uri(WhisperBaseUrl + TurboModel.FileName),
            TurboModel.SizeBytes,
            ["394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"],
            false),
        [SmallModel.Id] = new(SmallModel.FileName,
            new Uri(WhisperBaseUrl + SmallModel.FileName),
            SmallModel.SizeBytes,
            ["ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"],
            false)
    };

    private static readonly ModelDownload VadDownload = new(VadFileName,
        new Uri("https://huggingface.co/ggml-org/whisper-vad/resolve/main/" + VadFileName),
        885_000,
        ["29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf"],
        false);

    private readonly ILogger<LocalModelManager> _logger;
    private string _modelDirectory;
    private WhisperModelInfo _selectedWhisperModel = TurboModel;

    public LocalModelManager(ILogger<LocalModelManager> logger)
    {
        _logger = logger;
        _modelDirectory = Environment.GetEnvironmentVariable("LINGUAFLOW_MODEL_DIR") ?? DefaultModelDirectory;
    }

    public IReadOnlyList<WhisperModelInfo> AvailableWhisperModels { get; } = [TurboModel, SmallModel];

    public WhisperModelInfo SelectedWhisperModel
    {
        get => _selectedWhisperModel;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _selectedWhisperModel = AvailableWhisperModels.FirstOrDefault(model => model.Id == value.Id)
                ?? throw new ArgumentException($"Bilinmeyen Whisper modeli: {value.Id}", nameof(value));
        }
    }

    public string ModelDirectory
    {
        get => _modelDirectory;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            var fullPath = Path.GetFullPath(value);
            if (string.Equals(Path.GetPathRoot(fullPath), @"C:\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("LinguaFlow yerel modelleri C: sürücüsüne yazmaz. D: üzerinde bir klasör seçin.");
            }

            _modelDirectory = fullPath;
        }
    }

    public LocalModelStatus GetStatus()
    {
        var whisper = WhisperDownloads[SelectedWhisperModel.Id];
        var whisperPath = Path.Combine(ModelDirectory, whisper.RelativePath);
        var vadPath = Path.Combine(ModelDirectory, VadDownload.RelativePath);
        return new LocalModelStatus(
            ModelDirectory,
            SelectedWhisperModel,
            whisperPath,
            vadPath,
            ModelDownloader.IsComplete(whisperPath, whisper.MinimumBytes),
            ModelDownloader.IsComplete(vadPath, VadDownload.MinimumBytes));
    }

    public async Task DownloadModelsAsync(
        IProgress<LocalModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ModelDownload[] downloads = [WhisperDownloads[SelectedWhisperModel.Id], VadDownload];
        Directory.CreateDirectory(ModelDirectory);
        for (var index = 0; index < downloads.Length; index++)
        {
            var download = downloads[index];
            var destination = Path.Combine(ModelDirectory, download.RelativePath);
            // Büyük dosyalarda tam SHA-256 her açılışta pahalıdır; boyutu tamam olan
            // dosya daha önce doğrulanarak yerine taşınmıştır.
            if (ModelDownloader.IsComplete(destination, download.MinimumBytes))
            {
                var length = new FileInfo(destination).Length;
                progress?.Report(new(Path.GetFileName(destination), length, length, index + 1, downloads.Length));
                continue;
            }

            await ModelDownloader.DownloadAsync(download, destination, index, downloads.Length, progress, _logger, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
