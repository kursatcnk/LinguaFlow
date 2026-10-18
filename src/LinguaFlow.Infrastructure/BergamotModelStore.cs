using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LinguaFlow.Core;
using Microsoft.Extensions.Logging;

namespace LinguaFlow.Infrastructure;

/// <summary>
/// Firefox çevirisinin kullandığı Mozilla Bergamot modellerini dil çifti bazında indirir ve kurar.
/// Modeller "dil ↔ İngilizce" yönündedir; diğer çiftler İngilizce üzerinden iki adımda çevrilir.
/// Her çift ~20–40 MB'tır ve CPU'da cümle başına birkaç on milisaniyede çalışır.
/// </summary>
public sealed partial class BergamotModelStore : IDisposable
{
    private const string BaseUrl = "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/";
    private const string RegistryFileName = "models.json";
    private static readonly TimeSpan RegistryMaxAge = TimeSpan.FromDays(7);
    private static readonly HttpClient RegistryClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly ILocalModelManager _models;
    private readonly ILogger<BergamotModelStore> _logger;
    private readonly SemaphoreSlim _registryGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task<bool>> _installs = new(StringComparer.Ordinal);
    private Dictionary<string, PairFiles>? _registry;

    public BergamotModelStore(ILocalModelManager models, ILogger<BergamotModelStore> logger)
    {
        _models = models;
        _logger = logger;
    }

    public event EventHandler<string>? PairInstalled;

    public string Root => Path.Combine(_models.ModelDirectory, "translation");

    /// <summary>Arayüz/Whisper dil kodunu model kayıt defterindeki koda çevirir.</summary>
    public static string? ToModelCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || string.Equals(language, LanguageCatalog.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return language.ToLowerInvariant() switch
        {
            "zh-tw" or "zh_hant" => "zh_hant",
            "zh-cn" or "zh" => "zh",
            "iw" => "he",
            var code => LanguageCatalog.BaseCode(code)
        };
    }

    /// <summary>Kaynaktan hedefe gereken model çiftleri (İngilizce pivot). Çeviri gerekmiyorsa boş.</summary>
    public static IReadOnlyList<string> Route(string? source, string? target)
    {
        var from = ToModelCode(source);
        var to = ToModelCode(target);
        if (from is null || to is null || from == to)
        {
            return [];
        }

        if (from == "en")
        {
            return [$"en-{to}"];
        }

        return to == "en" ? [$"{from}-en"] : [$"{from}-en", $"en-{to}"];
    }

    public string ConfigPath(string pair) => Path.Combine(Root, pair, "config.yml");

    public bool IsInstalled(string pair) => File.Exists(ConfigPath(pair));

    public IReadOnlyList<string> InstalledPairs()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(Root)
            .Select(Path.GetFileName)
            .Where(pair => pair is not null && IsInstalled(pair))
            .Select(pair => pair!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Çifti kurar. Aynı çift için eşzamanlı çağrılar tek indirmeyi paylaşır.</summary>
    public Task<bool> EnsureInstalledAsync(string pair, IProgress<LocalModelDownloadProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pair);
        if (IsInstalled(pair))
        {
            return Task.FromResult(true);
        }

        return _installs.GetOrAdd(pair, key => Task.Run(() => InstallAsync(key, progress)));
    }

    /// <summary>Kaynak → hedef yolundaki tüm çiftleri kurar.</summary>
    public async Task<bool> EnsureRouteAsync(string? source, string? target, IProgress<LocalModelDownloadProgress>? progress = null)
    {
        var ok = true;
        foreach (var pair in Route(source, target))
        {
            ok &= await EnsureInstalledAsync(pair, progress).ConfigureAwait(false);
        }

        return ok;
    }

    private async Task<bool> InstallAsync(string pair, IProgress<LocalModelDownloadProgress>? progress)
    {
        try
        {
            var registry = await GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            if (!registry.TryGetValue(pair, out var files))
            {
                LogPairUnsupported(_logger, pair);
                return false;
            }

            var downloads = files.ToDownloads(pair);
            for (var index = 0; index < downloads.Count; index++)
            {
                var download = downloads[index];
                var destination = Path.Combine(_models.ModelDirectory, download.RelativePath);
                if (!ModelDownloader.IsComplete(destination, download.MinimumBytes))
                {
                    await ModelDownloader.DownloadAsync(download, destination, index, downloads.Count, progress, _logger, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            // Yapılandırma en son yazılır; varlığı çiftin eksiksiz kurulduğunu gösterir.
            var config = ConfigPath(pair);
            var temporary = config + ".tmp";
            await File.WriteAllTextAsync(temporary, files.ToConfig(), Encoding.UTF8).ConfigureAwait(false);
            File.Move(temporary, config, true);
            LogPairInstalled(_logger, pair);
            PairInstalled?.Invoke(this, pair);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
                                              or JsonException or TaskCanceledException or UnauthorizedAccessException)
        {
            LogPairInstallFailed(_logger, pair, exception);
            // Başarısız kurulum bir sonraki istekte yeniden denensin.
            _installs.TryRemove(pair, out _);
            return false;
        }
    }

    private async Task<Dictionary<string, PairFiles>> GetRegistryAsync(CancellationToken cancellationToken)
    {
        if (_registry is not null)
        {
            return _registry;
        }

        await _registryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_registry is not null)
            {
                return _registry;
            }

            Directory.CreateDirectory(Root);
            var cachePath = Path.Combine(Root, RegistryFileName);
            var fresh = File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < RegistryMaxAge;
            if (!fresh)
            {
                try
                {
                    var json = await RegistryClient.GetStringAsync(new Uri(BaseUrl + "db/models.json"), cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(cachePath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (File.Exists(cachePath) && exception is HttpRequestException or TaskCanceledException)
                {
                    // Eski kayıt defteri yine de kullanılabilir.
                }
            }

            await using var stream = File.OpenRead(cachePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            _registry = ParseRegistry(document.RootElement);
            return _registry;
        }
        finally
        {
            _registryGate.Release();
        }
    }

    internal static Dictionary<string, PairFiles> ParseRegistry(JsonElement root)
    {
        var result = new Dictionary<string, PairFiles>(StringComparer.Ordinal);
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var pair in models.EnumerateObject())
        {
            var candidates = pair.Value.ValueKind == JsonValueKind.Array ? pair.Value.EnumerateArray().ToArray() : [pair.Value];
            var best = candidates
                .Where(entry => entry.TryGetProperty("files", out _))
                .OrderBy(entry => StatusRank(ReadString(entry, "releaseStatus")))
                .ThenBy(entry => ArchitectureRank(ReadString(entry, "architecture")))
                .Select(entry => PairFiles.TryParse(entry.GetProperty("files")))
                .FirstOrDefault(files => files is not null);
            if (best is not null)
            {
                result[pair.Name] = best;
            }
        }

        return result;
    }

    private static int StatusRank(string status) => status switch
    {
        "Release Desktop" => 0,
        "Release" => 1,
        "Release Android" => 2,
        "Nightly" => 3,
        _ => 4
    };

    private static int ArchitectureRank(string architecture) => architecture switch
    {
        "base" => 0,
        "base-memory" => 1,
        "tiny" => 2,
        _ => 3
    };

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    internal sealed record RemoteFile(string Path, long MinimumBytes, string? Sha256)
    {
        public string LocalName => System.IO.Path.GetFileName(Path).Replace(".gz", string.Empty, StringComparison.Ordinal);
    }

    internal sealed record PairFiles(RemoteFile Model, RemoteFile SourceVocab, RemoteFile TargetVocab, RemoteFile? Shortlist)
    {
        public static PairFiles? TryParse(JsonElement files)
        {
            var model = ReadFile(files, "model");
            var vocab = ReadFile(files, "vocab");
            var sourceVocab = ReadFile(files, "srcVocab") ?? vocab;
            var targetVocab = ReadFile(files, "trgVocab") ?? vocab;
            return model is null || sourceVocab is null || targetVocab is null
                ? null
                : new PairFiles(model, sourceVocab, targetVocab, ReadFile(files, "lexicalShortlist"));
        }

        public IReadOnlyList<ModelDownload> ToDownloads(string pair)
        {
            RemoteFile?[] all = [Model, SourceVocab, TargetVocab, Shortlist];
            return all
                .Where(file => file is not null)
                .Select(file => file!)
                .DistinctBy(file => file.Path)
                .Select(file => new ModelDownload(
                    System.IO.Path.Combine("translation", pair, file.LocalName),
                    new Uri(BaseUrl + file.Path),
                    file.MinimumBytes,
                    file.Sha256 is null ? [] : [file.Sha256],
                    file.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        public string ToConfig()
        {
            var builder = new StringBuilder();
            builder.AppendLine("relative-paths: true");
            builder.AppendLine("models:");
            builder.Append("- ").AppendLine(Model.LocalName);
            builder.AppendLine("vocabs:");
            builder.Append("- ").AppendLine(SourceVocab.LocalName);
            builder.Append("- ").AppendLine(TargetVocab.LocalName);
            if (Shortlist is not null)
            {
                builder.AppendLine("shortlist:");
                builder.Append("- ").AppendLine(Shortlist.LocalName);
                builder.AppendLine("- false");
            }

            builder.AppendLine("""
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
                """);
            return builder.ToString();
        }

        private static RemoteFile? ReadFile(JsonElement files, string name)
        {
            if (!files.TryGetProperty(name, out var file) || !file.TryGetProperty("path", out var path) ||
                path.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var size = file.TryGetProperty("uncompressedSize", out var sizeElement) && sizeElement.TryGetInt64(out var value) ? value : 1;
            var hash = file.TryGetProperty("uncompressedHash", out var hashElement) && hashElement.ValueKind == JsonValueKind.String
                ? hashElement.GetString()
                : null;
            return new RemoteFile(path.GetString()!, size, hash);
        }
    }

    public void Dispose() => _registryGate.Dispose();

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information, Message = "Çevrimdışı çeviri paketi kuruldu: {Pair}")]
    private static partial void LogPairInstalled(ILogger logger, string pair);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Information, Message = "Çevrimdışı çeviri paketi yok: {Pair}")]
    private static partial void LogPairUnsupported(ILogger logger, string pair);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Warning, Message = "Çevrimdışı çeviri paketi kurulamadı: {Pair}")]
    private static partial void LogPairInstallFailed(ILogger logger, string pair, Exception exception);
}
