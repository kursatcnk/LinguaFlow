using LinguaFlow.Core;

namespace LinguaFlow.Application;

public sealed class TranscriptProcessor : ITranscriptProcessor
{
    private readonly object _gate = new();
    private readonly int _maximumSegments;
    private readonly List<TranscriptSegment> _finalSegments = [];
    private TranscriptSegment? _partial;
    private string? _lastFinalFingerprint;

    public TranscriptProcessor(int maximumSegments = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSegments, 1);
        _maximumSegments = maximumSegments;
    }

    public event EventHandler<TranscriptSegment>? SegmentChanged;

    public IReadOnlyList<TranscriptSegment> FinalSegments
    {
        get
        {
            lock (_gate)
            {
                return _finalSegments.ToArray();
            }
        }
    }

    public TranscriptSegment? PartialSegment
    {
        get
        {
            lock (_gate)
            {
                return _partial;
            }
        }
    }

    public void Process(TranslationEvent translationEvent)
    {
        ArgumentNullException.ThrowIfNull(translationEvent);

        var original = NormalizeWhitespace(translationEvent.OriginalText);
        var translated = NormalizeWhitespace(translationEvent.TranslatedText);
        var segment = new TranscriptSegment(
            translationEvent.SessionId,
            translationEvent.SegmentId,
            original,
            translated,
            translationEvent.IsFinal,
            translationEvent.Offset,
            translationEvent.Duration,
            translationEvent.ReceivedAt,
            translationEvent.DetectedLanguage);

        if (string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(translated))
        {
            // Boş partial, sağlayıcının o segment için gösterdiği geçici metni geri çektiği anlamına gelir.
            lock (_gate)
            {
                if (segment.IsFinal || _partial?.SegmentId != segment.SegmentId)
                {
                    return;
                }

                _partial = null;
            }

            SegmentChanged?.Invoke(this, segment);
            return;
        }

        lock (_gate)
        {
            if (!segment.IsFinal)
            {
                if (_partial is not null &&
                    _partial.SegmentId == segment.SegmentId &&
                    _partial.OriginalText == segment.OriginalText &&
                    _partial.TranslatedText == segment.TranslatedText)
                {
                    return;
                }

                _partial = segment;
            }
            else
            {
                // Akış sağlayıcıları sonraki cümlenin partial'ını önceki cümlenin final'ından
                // önce gönderebilir; yalnızca aynı segmente ait partial temizlenir.
                if (_partial?.SegmentId == segment.SegmentId)
                {
                    _partial = null;
                }

                var fingerprint = $"{segment.OriginalText}\u001f{segment.TranslatedText}";
                if (string.Equals(_lastFinalFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                _lastFinalFingerprint = fingerprint;
                _finalSegments.Add(segment);
                if (_finalSegments.Count > _maximumSegments)
                {
                    _finalSegments.RemoveRange(0, _finalSegments.Count - _maximumSegments);
                }
            }
        }

        SegmentChanged?.Invoke(this, segment);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _partial = null;
            _lastFinalFingerprint = null;
            _finalSegments.Clear();
        }
    }

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
