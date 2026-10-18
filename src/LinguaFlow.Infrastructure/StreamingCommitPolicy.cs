using System.Text;

namespace LinguaFlow.Infrastructure;

/// <summary>Tampon başlangıcına göre zamanlanmış bir Whisper segmenti.</summary>
public sealed record RecognizedSegment(string Text, TimeSpan Start, TimeSpan End);

/// <summary>VAD'ın tampon içinde konuşma bulduğu aralık.</summary>
public sealed record SpeechSpan(TimeSpan Start, TimeSpan End);

/// <param name="Committed">Kesinleşen, artık değişmeyecek segmentler (final çeviri gönderilir).</param>
/// <param name="Pending">Hâlâ değişebilecek segmentler (partial altyazı).</param>
/// <param name="TrimTo">Ses tamponunun başından atılacak süre.</param>
/// <param name="EndOfUtterance">Konuşmacı sustu; dil kilidi gibi ifade düzeyindeki durum sıfırlanır.</param>
public sealed record CommitDecision(
    IReadOnlyList<RecognizedSegment> Committed,
    IReadOnlyList<RecognizedSegment> Pending,
    TimeSpan TrimTo,
    bool EndOfUtterance);

/// <summary>
/// Kayan pencereli Whisper çıktısından hangi metnin kesinleşeceğine karar verir.
/// Bir cümle şu durumlarda kesinleşir:
/// <list type="bullet">
/// <item>konuşmacı belirgin şekilde durdu (<see cref="PauseToFinalize"/>),</item>
/// <item>kısa bir duraksama var ve son cümle noktalama ile bitiyor (<see cref="SentencePause"/>),</item>
/// <item>konuşma sürerken, tamamlanmış bir cümle ardışık iki tahminde aynı kaldı.</item>
/// </list>
/// </summary>
public sealed class StreamingCommitPolicy
{
    public TimeSpan PauseToFinalize { get; init; } = TimeSpan.FromMilliseconds(650);
    public TimeSpan SentencePause { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ClauseCommitAfter { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ForceCommitAfter { get; init; } = TimeSpan.FromSeconds(18);
    public TimeSpan MaximumBuffer { get; init; } = TimeSpan.FromSeconds(27);
    public TimeSpan KeepPreRoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public CommitDecision Decide(
        IReadOnlyList<RecognizedSegment> hypothesis,
        IReadOnlyList<RecognizedSegment> previous,
        IReadOnlyList<SpeechSpan> speech,
        TimeSpan bufferDuration)
    {
        ArgumentNullException.ThrowIfNull(hypothesis);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(speech);

        var silenceTrim = Max(TimeSpan.Zero, bufferDuration - KeepPreRoll);
        if (speech.Count == 0)
        {
            return new([], [], silenceTrim, true);
        }

        var silenceTail = bufferDuration - speech[^1].End;
        if (hypothesis.Count == 0)
        {
            // VAD bir şey duydu ama Whisper metin çıkarmadı (müzik, gürültü, nefes).
            return silenceTail >= PauseToFinalize || bufferDuration >= MaximumBuffer
                ? new([], [], silenceTrim, true)
                : new([], [], TimeSpan.Zero, false);
        }

        if (silenceTail >= PauseToFinalize || (silenceTail >= SentencePause && EndsSentence(hypothesis[^1].Text)))
        {
            return new(hypothesis, [], silenceTrim, true);
        }

        if (bufferDuration >= MaximumBuffer)
        {
            var forcedTrim = Min(bufferDuration, Max(hypothesis[^1].End, TimeSpan.Zero));
            return new(hypothesis, [], forcedTrim > TimeSpan.Zero ? forcedTrim : silenceTrim, false);
        }

        if (hypothesis.Count >= 2)
        {
            var stable = Math.Min(StablePrefixLength(hypothesis, previous), hypothesis.Count - 1);
            var commitThrough = -1;
            for (var index = stable - 1; index >= 0; index--)
            {
                if (EndsSentence(hypothesis[index].Text) ||
                    (bufferDuration >= ClauseCommitAfter && EndsClause(hypothesis[index].Text)))
                {
                    commitThrough = index;
                    break;
                }
            }

            if (commitThrough < 0 && bufferDuration >= ForceCommitAfter)
            {
                // Noktalamasız uzun akış: kararlı kısmı, o da yoksa son segment dışındakileri kesinleştir.
                commitThrough = stable > 0 ? stable - 1 : hypothesis.Count - 2;
            }

            if (commitThrough >= 0)
            {
                var committed = hypothesis.Take(commitThrough + 1).ToArray();
                var next = hypothesis[commitThrough + 1];
                var cut = committed[^1].End;
                if (next.Start > cut)
                {
                    cut += (next.Start - cut) / 2;
                }

                cut = Min(cut, bufferDuration);
                var pending = hypothesis.Skip(commitThrough + 1).ToArray();
                return new(committed, pending, cut, false);
            }
        }

        return new([], hypothesis, TimeSpan.Zero, false);
    }

    public static bool EndsSentence(string text)
    {
        var trimmed = TrimClosers(text);
        if (trimmed.Length == 0 || trimmed.EndsWith("...", StringComparison.Ordinal) || trimmed.EndsWith('…'))
        {
            return false;
        }

        return trimmed[^1] is '.' or '?' or '!' or '。' or '？' or '！' or '؟' or '।' or '۔';
    }

    public static bool EndsClause(string text)
    {
        var trimmed = TrimClosers(text);
        return trimmed.Length > 0 && trimmed[^1] is ',' or ';' or ':' or '、' or '，' or '；' or '：' or '—' or '…';
    }

    /// <summary>Önceki tahminle metni (büyük/küçük harf ve noktalama hariç) aynı olan baştaki segment sayısı.</summary>
    public static int StablePrefixLength(IReadOnlyList<RecognizedSegment> current, IReadOnlyList<RecognizedSegment> previous)
    {
        var count = 0;
        while (count < current.Count && count < previous.Count &&
               Normalize(current[count].Text) == Normalize(previous[count].Text))
        {
            count++;
        }

        return count;
    }

    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string TrimClosers(string text) =>
        text.Trim().TrimEnd('"', '\'', '”', '’', '»', ')', ']', '」', '』', ' ');

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
}
