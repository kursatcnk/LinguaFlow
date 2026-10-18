using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LinguaFlow.Core;
using Microsoft.Extensions.Logging;

namespace LinguaFlow.Infrastructure;

/// <param name="RelativePath">Model klasörüne göre hedef dosya yolu.</param>
/// <param name="AllowedSha256">Boşsa yalnızca boyut kontrol edilir.</param>
/// <param name="IsGZip">Kaynak .gz ise indirildikten sonra açılır ve açılmış dosya doğrulanır.</param>
internal sealed record ModelDownload(string RelativePath, Uri Uri, long MinimumBytes, string[] AllowedSha256, bool IsGZip);

/// <summary>Kaldığı yerden devam eden, yeniden deneyen ve SHA-256 ile doğrulayan model indirici.</summary>
internal static partial class ModelDownloader
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    public static bool IsComplete(string path, long minimumBytes) =>
        File.Exists(path) && new FileInfo(path).Length >= minimumBytes;

    public static async Task DownloadAsync(
        ModelDownload download,
        string destination,
        int completedFiles,
        int totalFiles,
        IProgress<LocalModelDownloadProgress>? progress,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
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
                await DownloadAttemptAsync(download, transferPath, completedFiles, totalFiles, progress, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException && attempt < 4)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    LogDownloadRetry(logger, fileName, attempt, exception.Message);
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
        LogModelReady(logger, fileName, destination);
        var finalLength = new FileInfo(destination).Length;
        progress?.Report(new(fileName, finalLength, finalLength, completedFiles + 1, totalFiles));
    }

    private static async Task DownloadAttemptAsync(
        ModelDownload download,
        string partialPath,
        int completedFiles,
        int totalFiles,
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
                progress?.Report(new(Path.GetFileName(download.RelativePath), received, totalBytes, completedFiles, totalFiles));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DecompressGZipAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await gzip.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LinguaFlow/2.0");
        return client;
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Yerel model hazır: {FileName} ({Path})")]
    private static partial void LogModelReady(ILogger logger, string fileName, string path);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "{FileName} indirmesi yeniden denenecek. Deneme: {Attempt}. Hata: {Message}")]
    private static partial void LogDownloadRetry(ILogger logger, string fileName, int attempt, string message);
}
