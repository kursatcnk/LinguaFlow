namespace LinguaFlow.Core;

public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Arayüzde gösterilen diller ve motorlar arası dil kodu dönüşümleri.
/// Kaynak kodları Whisper'ın ISO-639-1 kodlarıdır; hedef kodları çeviri servisinin kodlarıdır.
/// </summary>
public static class LanguageCatalog
{
    public const string Auto = "auto";

    private static readonly LanguageOption[] Common =
    [
        new("tr", "Türkçe"),
        new("en", "İngilizce"),
        new("de", "Almanca"),
        new("fr", "Fransızca"),
        new("es", "İspanyolca"),
        new("it", "İtalyanca"),
        new("pt", "Portekizce"),
        new("ru", "Rusça"),
        new("uk", "Ukraynaca"),
        new("ar", "Arapça"),
        new("fa", "Farsça"),
        new("az", "Azerbaycanca"),
        new("ja", "Japonca"),
        new("ko", "Korece"),
        new("hi", "Hintçe"),
        new("ur", "Urduca"),
        new("nl", "Felemenkçe"),
        new("pl", "Lehçe"),
        new("sv", "İsveççe"),
        new("no", "Norveççe"),
        new("da", "Danca"),
        new("fi", "Fince"),
        new("el", "Yunanca"),
        new("bg", "Bulgarca"),
        new("ro", "Romence"),
        new("hu", "Macarca"),
        new("cs", "Çekçe"),
        new("sk", "Slovakça"),
        new("sr", "Sırpça"),
        new("hr", "Hırvatça"),
        new("bs", "Boşnakça"),
        new("sq", "Arnavutça"),
        new("ka", "Gürcüce"),
        new("hy", "Ermenice"),
        new("kk", "Kazakça"),
        new("uz", "Özbekçe"),
        new("he", "İbranice"),
        new("id", "Endonezce"),
        new("ms", "Malayca"),
        new("vi", "Vietnamca"),
        new("th", "Tayca"),
        new("bn", "Bengalce"),
        new("ta", "Tamilce"),
        new("sw", "Svahili"),
        new("ca", "Katalanca"),
        new("lt", "Litvanca"),
        new("lv", "Letonca"),
        new("et", "Estonca"),
        new("sl", "Slovence"),
    ];

    public static IReadOnlyList<LanguageOption> SourceLanguages { get; } =
    [
        new(Auto, "Otomatik"),
        .. Common,
        new("zh", "Çince"),
    ];

    public static IReadOnlyList<LanguageOption> TargetLanguages { get; } =
    [
        .. Common,
        new("zh-CN", "Çince (Basitleştirilmiş)"),
        new("zh-TW", "Çince (Geleneksel)"),
    ];

    public static string BaseCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var separator = code.IndexOfAny(['-', '_']);
        return (separator > 0 ? code[..separator] : code).ToLowerInvariant();
    }

    /// <summary>Whisper/ISO kodunu çeviri servisinin anladığı koda çevirir. Otomatik için null döner.</summary>
    public static string? ToGoogleCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || string.Equals(code, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return code.ToLowerInvariant() switch
        {
            "zh" => "zh-CN",
            "zh-cn" => "zh-CN",
            "zh-tw" => "zh-TW",
            "he" => "iw",
            "nn" => "no",
            _ => code
        };
    }

    /// <summary>Hedef kodunu Whisper'ın dil koduna indirger (ör. zh-CN → zh).</summary>
    public static string ToWhisperCode(string code) =>
        string.Equals(code, Auto, StringComparison.OrdinalIgnoreCase) ? Auto : BaseCode(code);

    public static bool IsSameLanguage(string? first, string? second)
    {
        var a = BaseCode(first);
        return a.Length > 0 && a != Auto && a == BaseCode(second);
    }

    public static string DisplayName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        return SourceLanguages.Concat(TargetLanguages)
            .FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))?.Name
            ?? SourceLanguages.FirstOrDefault(language => language.Code == BaseCode(code))?.Name
            ?? code.ToUpperInvariant();
    }
}
