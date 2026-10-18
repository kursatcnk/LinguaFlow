using System.Globalization;
using System.Text.RegularExpressions;
using LinguaFlow.Core;

namespace LinguaFlow.Infrastructure;

/// <summary>
/// Konuşma dökümünü makine çevirisine hazırlar. Çeviri motorları yazılı metinle eğitilir; konuşmaya özgü
/// "um, uh, ııı" gibi dolgu sözcükleri, "wh- what" gibi yarım kalan başlangıçlar ve tamamı büyük harf
/// yazılmış cümleler (kısaltma sanılır) çeviriyi bozar.
/// </summary>
public static partial class TranslationTextPreparer
{
    // Yalnızca o dilde dolgu olan sözcükler: Portekizce "um" (bir), Almanca "er" (o) anlamlıdır.
    private static readonly Dictionary<string, HashSet<string>> Fillers = new(StringComparer.Ordinal)
    {
        ["en"] = new(StringComparer.Ordinal) { "um", "umm", "uh", "uhh", "uhm", "erm", "er", "ehm" },
        ["de"] = new(StringComparer.Ordinal) { "äh", "ähm", "öh", "öhm", "ehm" },
        ["fr"] = new(StringComparer.Ordinal) { "euh", "heu" },
        ["nl"] = new(StringComparer.Ordinal) { "eh", "ehm", "uh", "uhm" },
        ["it"] = new(StringComparer.Ordinal) { "ehm", "ehh" },
        ["es"] = new(StringComparer.Ordinal) { "eh", "ehh", "em", "emm" },
        ["tr"] = new(StringComparer.Ordinal) { "ı", "ıı", "ıh" },
        ["ru"] = new(StringComparer.Ordinal) { "э", "ээ", "эм", "мм" },
    };

    private static readonly HashSet<char> SentenceEnds = ['.', '?', '!', '…', '。', '？', '！'];

    public static string Prepare(string text, string? language)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var code = string.IsNullOrWhiteSpace(language) ? string.Empty : LanguageCatalog.BaseCode(language);
        var fillers = Fillers.GetValueOrDefault(code);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>(words.Length);
        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index];
            var core = Core(word).ToLowerInvariant();
            if (core.Length > 0 && (IsHesitation(core) || fillers?.Contains(core) == true))
            {
                CarrySentenceEnd(word, kept);
                continue;
            }

            // "wh- what", "I- I think": yarım bırakılan sözcüğü at.
            if (word.Length > 1 && word[^1] == '-' && word[^2] != '-' && index + 1 < words.Length)
            {
                var fragment = word[..^1].ToLowerInvariant();
                if (Core(words[index + 1]).StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            kept.Add(word);
        }

        if (kept.Count == 0)
        {
            return string.Empty;
        }

        var result = string.Join(' ', kept);
        result = LeadingNoiseRegex().Replace(result, string.Empty);
        result = SpaceBeforePunctuationRegex().Replace(result, "$1");
        if (result.Length == 0)
        {
            return string.Empty;
        }

        var culture = CultureFor(code);
        if (IsShouting(result))
        {
            result = result.ToLower(culture);
            if (code == "en")
            {
                result = EnglishIRegex().Replace(result, "I");
            }
        }

        // Dolgu sözcüğü atıldıysa cümle küçük harfle başlayabilir: "Um, so we..." → "so we..."
        var firstLetter = text.FirstOrDefault(char.IsLetter);
        if (char.IsLower(result[0]) && (char.IsUpper(firstLetter) || IsShouting(text)))
        {
            result = result[..1].ToUpper(culture) + result[1..];
        }

        return result;
    }

    /// <summary>"mmm", "eee", "ııı", "hmm": dilden bağımsız duraksama sesleri ("III" gibi Roma rakamları hariç).</summary>
    private static bool IsHesitation(string core) =>
        HesitationRegex().IsMatch(core) && core is not ("iii" or "xxx" or "ccc");

    private static void CarrySentenceEnd(string filler, List<string> kept)
    {
        var last = filler.TrimEnd('"', '\'', ')');
        if (kept.Count == 0 || last.Length == 0 || !SentenceEnds.Contains(last[^1]))
        {
            return;
        }

        var previous = kept[^1].TrimEnd(',', ';', ':');
        if (previous.Length > 0 && !SentenceEnds.Contains(previous[^1]))
        {
            kept[^1] = previous + last[^1];
        }
    }

    private static string Core(string word)
    {
        var start = 0;
        var end = word.Length;
        while (start < end && !char.IsLetterOrDigit(word[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetterOrDigit(word[end - 1]))
        {
            end--;
        }

        return word[start..end];
    }

    private static bool IsShouting(string text)
    {
        var letters = 0;
        var upper = 0;
        foreach (var character in text)
        {
            if (char.IsLetter(character))
            {
                letters++;
                if (char.IsUpper(character))
                {
                    upper++;
                }
            }
        }

        return letters >= 8 && upper >= letters * 0.85;
    }

    private static CultureInfo CultureFor(string code)
    {
        if (code.Length == 0)
        {
            return CultureInfo.InvariantCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    [GeneratedRegex(@"^(?:(\p{L})\1{2,}|h+m+|m{2,}h*)$")]
    private static partial Regex HesitationRegex();

    // Baştaki "...", "—", "," gibi kalıntılar (dolgu atılınca ya da önceki cümleden taşınca).
    [GeneratedRegex(@"^[\s.,;:…\-–—]+")]
    private static partial Regex LeadingNoiseRegex();

    [GeneratedRegex(@"\s+([,.;:!?…])")]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@"\bi\b(?='|’|\s|[,.!?]|$)")]
    private static partial Regex EnglishIRegex();
}
