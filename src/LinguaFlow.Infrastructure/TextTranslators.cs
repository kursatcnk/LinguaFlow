using System.Net;
using System.Text;
using System.Text.Json;
using BergamotTranslatorSharp;
using LinguaFlow.Core;

namespace LinguaFlow.Infrastructure;

/// <summary>
/// Google Translate'in anahtarsız, ücretsiz web uç noktası. Tüm dil çiftlerini destekler ve
/// tipik olarak 100–250 ms içinde yanıt verir. Metin (ses değil) Google'a gönderilir.
/// </summary>
public sealed class GoogleFreeTranslator : ITextTranslator, IDisposable
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
    private readonly HttpClient _httpClient;

    public GoogleFreeTranslator()
        : this(CreateHttpClient())
    {
    }

    public GoogleFreeTranslator(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string Id => "google-free";

    public bool CanTranslate(string sourceLanguage, string targetLanguage) => true;

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var source = LanguageCatalog.ToGoogleCode(sourceLanguage) ?? "auto";
        var target = LanguageCatalog.ToGoogleCode(targetLanguage) ?? targetLanguage;
        var url = $"{Endpoint}?client=gtx&dt=t&ie=UTF-8&oe=UTF-8&sl={Uri.EscapeDataString(source)}&tl={Uri.EscapeDataString(target)}";
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]);
        using var response = await _httpClient.PostAsync(new Uri(url), content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Çeviri servisi istek sınırına ulaştı.", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseResponse(document.RootElement);
    }

    internal static string ParseResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var sentence in root[0].EnumerateArray())
        {
            if (sentence.ValueKind == JsonValueKind.Array &&
                sentence.GetArrayLength() > 0 &&
                sentence[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(sentence[0].GetString());
            }
        }

        return builder.ToString().Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Bağlantıyı sıcak tut: her cümlede TLS el sıkışması gecikme ekler.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LinguaFlow/2.0");
        return client;
    }

    public void Dispose() => _httpClient.Dispose();
}

/// <summary>
/// Mozilla Bergamot ile tamamen cihazda çeviri. Gerekli dil paketi yoksa arka planda indirilir ve
/// hazır olana kadar <see cref="CanTranslate"/> false döner.
/// </summary>
public sealed class BergamotOfflineTranslator : ITextTranslator, IDisposable
{
    private readonly BergamotModelStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, BlockingService> _services = new(StringComparer.Ordinal);

    public BergamotOfflineTranslator(BergamotModelStore store)
    {
        _store = store;
    }

    public string Id => "bergamot";

    /// <summary>Paketler kurulu mu? Eksikse indirme başlatmaz.</summary>
    public bool IsReady(string sourceLanguage, string targetLanguage)
    {
        var route = BergamotModelStore.Route(sourceLanguage, targetLanguage);
        return route.Count > 0 && route.All(_store.IsInstalled);
    }

    /// <summary>Paketler kurulu mu? Eksik paketleri arka planda indirmeye başlar.</summary>
    public bool CanTranslate(string sourceLanguage, string targetLanguage)
    {
        var route = BergamotModelStore.Route(sourceLanguage, targetLanguage);
        if (route.Count == 0)
        {
            return false;
        }

        var ready = true;
        foreach (var pair in route)
        {
            if (!_store.IsInstalled(pair))
            {
                ready = false;
                _ = _store.EnsureInstalledAsync(pair);
            }
        }

        return ready;
    }

    public Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(string.Empty);
        }

        var current = text;
        lock (_gate)
        {
            foreach (var pair in BergamotModelStore.Route(sourceLanguage, targetLanguage))
            {
                if (!_services.TryGetValue(pair, out var service))
                {
                    service = new BlockingService(_store.ConfigPath(pair));
                    _services[pair] = service;
                }

                current = CleanTranslation(service.Translate(current));
            }
        }

        return Task.FromResult(current);
    }

    internal static string CleanTranslation(string value) => value.Trim().Trim('"', '“', '”').Trim();

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var service in _services.Values)
            {
                service.Dispose();
            }

            _services.Clear();
        }
    }
}
