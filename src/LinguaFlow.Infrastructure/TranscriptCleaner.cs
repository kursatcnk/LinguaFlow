using System.Text;
using System.Text.RegularExpressions;

namespace LinguaFlow.Infrastructure;

/// <summary>
/// Whisper çıktısındaki bilinen gürültüleri temizler: [Müzik] gibi etiketler, sessizlikte uydurulan
/// "izlediğiniz için teşekkürler" tarzı kalıplar ve döngüye giren tekrarlar.
/// </summary>
public static partial class TranscriptCleaner
{
    // Whisper'ın sessizlik/müzik üzerinde sık uydurduğu, gerçek konuşmada nadiren tek başına geçen ifadeler.
    private static readonly HashSet<string> Hallucinations = new(StringComparer.Ordinal)
    {
        "thanksforwatching",
        "thankyouforwatching",
        "thanksforwatchingandseeyounexttime",
        "pleasesubscribe",
        "pleasesubscribetomychannel",
        "likeandsubscribe",
        "you",
        "izlediğiniziçinteşekkürler",
        "İzlediğiniziçinteşekkürler",
        "altyazımk",
        "aboneolmayıunutmayın",
        "kanalımaaboneolmayıunutmayın",
        "продолжениеследует",
        "ご視聴ありがとうございました",
        "请不吝点赞订阅转发打赏支持明镜与点点栏目",
    };

    private static readonly string[] HallucinationPrefixes =
    [
        "subtitlesby",
        "subtitlesbytheamara",
        "transcriptionby",
        "soustitr",
        "untertitelimauftrag",
        "untertiteldeszdf",
        "субтитры",
        "字幕",
    ];

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = AnnotationRegex().Replace(text, " ");
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
        cleaned = cleaned.Trim('-', ' ', '>', '"').Trim();
        if (cleaned.Length == 0 || IsHallucination(cleaned))
        {
            return string.Empty;
        }

        return CollapseRepetitions(cleaned);
    }

    public static bool IsHallucination(string text)
    {
        var normalized = StreamingCommitPolicy.Normalize(text);
        return normalized.Length == 0 ||
            Hallucinations.Contains(normalized) ||
            HallucinationPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>"evet evet evet evet evet" gibi 3'ten fazla art arda tekrarı iki tekrara indirir.</summary>
    public static string CollapseRepetitions(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 6)
        {
            return text;
        }

        var result = new List<string>(words.Length);
        var index = 0;
        while (index < words.Length)
        {
            var collapsed = false;
            for (var size = 1; size <= 6 && index + size * 3 <= words.Length; size++)
            {
                var repeats = 1;
                while (index + (repeats + 1) * size <= words.Length && SameRun(words, index, index + repeats * size, size))
                {
                    repeats++;
                }

                if (repeats > 3)
                {
                    for (var copy = 0; copy < 2; copy++)
                    {
                        result.AddRange(words.AsSpan(index, size).ToArray());
                    }

                    index += repeats * size;
                    collapsed = true;
                    break;
                }
            }

            if (!collapsed)
            {
                result.Add(words[index]);
                index++;
            }
        }

        return string.Join(' ', result);
    }

    public static string Join(IEnumerable<string> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0 && !IsCjk(trimmed[0]))
            {
                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }

    private static bool SameRun(string[] words, int first, int second, int size)
    {
        for (var offset = 0; offset < size; offset++)
        {
            if (!string.Equals(
                    StreamingCommitPolicy.Normalize(words[first + offset]),
                    StreamingCommitPolicy.Normalize(words[second + offset]),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCjk(char character) =>
        character is >= '　' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯';

    // [Music], (applause), *laughs*, ♪ şarkı sözü ♪ gibi açıklama etiketleri.
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫♬]+[^♪♫♬]*[♪♫♬]*|[♪♫♬]")]
    private static partial Regex AnnotationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
