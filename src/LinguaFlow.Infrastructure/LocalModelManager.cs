using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LinguaFlow.Core;
using Microsoft.Extensions.Logging;

namespace LinguaFlow.Infrastructure;

public sealed partial class LocalModelManager : ILocalModelManager
{
    public const string WhisperFileName = "ggml-base.en.bin";
    public const string TranslationDirectoryName = "bergamot-en-tr";
    public const string TranslationConfigFileName = "config.entr.yml";
    private const string DefaultModelDirectory = @"D:\LinguaFlowModels";
    private const string MozillaBaseUrl = "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/";

    private static readonly ModelDownload[] Downloads =
    [
        new(WhisperFileName,
            new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin"),
            140_000_000,
            ["a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002", "e111a865d56afc4adf1379a2028544b3275dd25839c460953ed9a126632dcda2"],
            false),
        new(Path.Combine(TranslationDirectoryName, "model.entr.intgemm.alphas.bin"),
            new Uri(MozillaBaseUrl + "models/en-tr/spring-2024_LiFGeNrEQpKdNlziG2qP_A/exported/model.entr.intgemm.alphas.bin.gz"),
            17_000_000,
            ["ea3cd4ee09de190df265c78d84260e277a9a8a3ddff05eb55d72afe7fc0fdbf4"],
            true),
        new(Path.Combine(TranslationDirectoryName, "vocab.entr.spm"),
            new Uri(MozillaBaseUrl + "models/en-tr/spring-2024_LiFGeNrEQpKdNlziG2qP_A/exported/vocab.entr.spm.gz"),
            100_000,
            ["a220363d60391f7ce3df8ec9785424dbc00c24d5786d1a4308cf8ca3ed3385c2"],
            true),
        new(Path.Combine(TranslationDirectoryName, "lex.50.50.entr.s2t.bin"),
            new Uri(MozillaBaseUrl + "models/en-tr/spring-2024_LiFGeNrEQpKdNlziG2qP_A/exported/lex.50.50.entr.s2t.bin.gz"),
            100_000,
            ["3aba0be842bd4c192659c781e6fdce8eb9b2b1fff29a40d22f75da369d59f46f"],
            true)
    ];

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly ILogger<LocalModelManager> _logger;
    private string _modelDirectory;

    public LocalModelManager(ILogger<LocalModelManager> logger)
    {
        _logger = logger;
        _modelDirectory = Environment.GetEnvironmentVariable("LINGUAFLOW_MODEL_DIR") ?? DefaultModelDirectory;
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
        var whisperPath = Path.Combine(ModelDirectory, WhisperFileName);
        var configPath = Path.Combine(ModelDirectory, TranslationDirectoryName, TranslationConfigFileName);
        var translationReady = File.Exists(configPath) && Downloads.Skip(1).All(download =>
            IsComplete(Path.Combine(ModelDirectory, download.RelativePath), download.MinimumBytes));
        return new LocalModelStatus(ModelDirectory, whisperPath, configPath,
            IsComplete(whisperPath, Downloads[0].MinimumBytes), translationReady);
    }

    public async Task DownloadModelsAsync(
        IProgress<LocalModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelDirectory);
        for (var index = 0; index < Downloads.Length; index++)
        {
            var download = Downloads[index];
            var destination = Path.Combine(ModelDirectory, download.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (await IsVerifiedAsync(destination, download, cancellationToken).ConfigureAwait(false))
            {
                var length = new FileInfo(destination).Length;
                progress?.Report(new(Path.GetFileName(destination), length, length, index + 1, Downloads.Length));
                continue;
            }

            await DownloadFileAsync(download, destination, index, progress, cancellationToken).ConfigureAwait(false);
        }

        await WriteTranslationConfigAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadFileAsync(
        ModelDownload download,
        string destination,
        int completedFiles,
        IProgress<LocalModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(destination);
        var transferPath = destination + (download.IsGZip ? ".gz.partial" : ".partial");
        var verifiedPath = download.IsGZip ? destination + ".partial" : transferPath;
        var readyFromExistingArchive = false;
        if (download.IsGZip && File.Exists(transferPath))
        {
            try
            {
                await DecompressGZipAsync(transferPath, verifiedPath, cancellationToken).ConfigureAwait(false);
                readyFromExistingArchive = await IsVerifiedAsync(verifiedPath, download, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                TryDelete(verifiedPath);
            }
        }

        for (var attempt = 1; !readyFromExistingArchive; attempt++)
        {
            try
            {
                await DownloadAttemptAsync(download, transferPath, completedFiles, progress, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException && attempt < 4)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    LogDownloadRetry(_logger, fileName, attempt, exception.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        if (download.IsGZip && !readyFromExistingArchive)
        {
            await DecompressGZipAsync(transferPath, verifiedPath, cancellationToken).ConfigureAwait(false);
        }

        if (!await IsVerifiedAsync(verifiedPath, download, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(verifiedPath);
            throw new InvalidDataException($"İndirilen model doğrulanamadı: {fileName}");
        }

        File.Move(verifiedPath, destination, true);
        TryDelete(transferPath);
        LogModelReady(_logger, fileName, destination);
        var finalLength = new FileInfo(destination).Length;
        progress?.Report(new(fileName, finalLength, finalLength, completedFiles + 1, Downloads.Length));
    }

    private static async Task DownloadAttemptAsync(
        ModelDownload download,
        string partialPath,
        int completedFiles,
        IProgress<LocalModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, download.Uri);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var isResume = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!isResume)
        {
            existingBytes = 0;
        }

        var contentLength = response.Content.Headers.ContentLength;
        long? totalBytes = contentLength.HasValue ? existingBytes + contentLength.Value : null;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(partialPath, isResume ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var received = existingBytes;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new(Path.GetFileName(download.RelativePath), received, totalBytes, completedFiles, Downloads.Length));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteTranslationConfigAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(ModelDirectory, TranslationDirectoryName);
        var configPath = Path.Combine(directory, TranslationConfigFileName);
        const string content = """
            relative-paths: true
            models:
            - model.entr.intgemm.alphas.bin
            vocabs:
            - vocab.entr.spm
            - vocab.entr.spm
            shortlist:
            - lex.50.50.entr.s2t.bin
            - false
            beam-size: 1
            normalize: 1.0
            word-penalty: 0
            max-length-break: 128
            mini-batch-words: 1024
            workspace: 128
            max-length-factor: 2.0
            skip-cost: true
            cpu-threads: 0
            quiet: true
            quiet-translation: true
            gemm-precision: int8shiftAlphaAll
            """;
        await File.WriteAllTextAsync(configPath, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DecompressGZipAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await gzip.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsComplete(string path, long minimumBytes) =>
        File.Exists(path) && new FileInfo(path).Length >= minimumBytes;

    private static async Task<bool> IsVerifiedAsync(string path, ModelDownload download, CancellationToken cancellationToken)
    {
        if (!IsComplete(path, download.MinimumBytes))
        {
            return false;
        }

        if (download.AllowedSha256.Length == 0)
        {
            return true;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
        return download.AllowedSha256.Contains(actual, StringComparer.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ModelDownload(string RelativePath, Uri Uri, long MinimumBytes, string[] AllowedSha256, bool IsGZip);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LinguaFlow/1.0");
        return client;
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Yerel model hazır: {FileName} ({Path})")]
    private static partial void LogModelReady(ILogger logger, string fileName, string path);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "{FileName} indirmesi yeniden denenecek. Deneme: {Attempt}. Hata: {Message}")]
    private static partial void LogDownloadRetry(ILogger logger, string fileName, int attempt, string message);
}
