using LinguaFlow.Application;
using LinguaFlow.Core;

namespace LinguaFlow.UnitTests;

public sealed class TranscriptProcessorTests
{
    [Fact]
    public void PartialUpdatesReplaceCurrentSegmentAndFinalCommitsOnce()
    {
        var processor = new TranscriptProcessor();
        processor.Process(Event("s1", "I don't think", "Bence", false));
        processor.Process(Event("s1", "I don't think this will work", "Bunun işe yarayacağını sanmıyorum", false));

        Assert.NotNull(processor.PartialSegment);
        Assert.Equal("I don't think this will work", processor.PartialSegment.OriginalText);
        Assert.Empty(processor.FinalSegments);

        processor.Process(Event("s1", "I don't think this will work.", "Bunun işe yarayacağını sanmıyorum.", true));

        Assert.Null(processor.PartialSegment);
        Assert.Single(processor.FinalSegments);
    }

    [Fact]
    public void ConsecutiveDuplicateFinalIsIgnored()
    {
        var processor = new TranscriptProcessor();
        var item = Event("s1", "Hello", "Merhaba", true);

        processor.Process(item);
        processor.Process(item with { SegmentId = "s2" });

        Assert.Single(processor.FinalSegments);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var processor = new TranscriptProcessor(2);
        processor.Process(Event("1", "One", "Bir", true));
        processor.Process(Event("2", "Two", "İki", true));
        processor.Process(Event("3", "Three", "Üç", true));

        Assert.Equal(2, processor.FinalSegments.Count);
        Assert.Equal("Two", processor.FinalSegments[0].OriginalText);
    }

    private static TranslationEvent Event(string segment, string original, string translated, bool final) =>
        new("session", segment, original, translated, final, TimeSpan.Zero, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow);
}
