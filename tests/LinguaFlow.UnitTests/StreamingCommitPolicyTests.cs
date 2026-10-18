using LinguaFlow.Infrastructure;

namespace LinguaFlow.UnitTests;

public sealed class StreamingCommitPolicyTests
{
    private readonly StreamingCommitPolicy _policy = new();

    [Fact]
    public void BufferWithoutSpeechIsDroppedExceptPreRoll()
    {
        var decision = _policy.Decide([], [], [], Seconds(3));

        Assert.Empty(decision.Committed);
        Assert.Empty(decision.Pending);
        Assert.True(decision.EndOfUtterance);
        Assert.Equal(Seconds(3) - _policy.KeepPreRoll, decision.TrimTo);
    }

    [Fact]
    public void ShortPauseAfterFinishedSentenceCommitsImmediately()
    {
        RecognizedSegment[] hypothesis = [Segment("How are you today?", 0, 1.6)];

        var decision = _policy.Decide(hypothesis, [], [Speech(0, 1.6)], Seconds(1.9));

        Assert.Single(decision.Committed);
        Assert.Empty(decision.Pending);
        Assert.True(decision.EndOfUtterance);
    }

    [Fact]
    public void ShortPauseInsideUnfinishedSentenceKeepsWaiting()
    {
        RecognizedSegment[] hypothesis = [Segment("The price of electricity has", 0, 1.6)];

        var decision = _policy.Decide(hypothesis, [], [Speech(0, 1.6)], Seconds(1.9));

        Assert.Empty(decision.Committed);
        Assert.Single(decision.Pending);
        Assert.Equal(TimeSpan.Zero, decision.TrimTo);
    }

    [Fact]
    public void LongPauseCommitsEvenWithoutPunctuation()
    {
        RecognizedSegment[] hypothesis = [Segment("and then we left", 0, 1.5)];

        var decision = _policy.Decide(hypothesis, [], [Speech(0, 1.5)], Seconds(1.5) + _policy.PauseToFinalize);

        Assert.Single(decision.Committed);
        Assert.True(decision.EndOfUtterance);
    }

    [Fact]
    public void StableFinishedSentenceIsCommittedWhileSpeakerContinues()
    {
        RecognizedSegment[] previous = [Segment("Good evening everyone.", 0, 2.0), Segment("Tonight we", 2.4, 3.0)];
        RecognizedSegment[] current = [Segment("Good evening, everyone.", 0, 2.0), Segment("Tonight we are going", 2.4, 3.6)];

        var decision = _policy.Decide(current, previous, [Speech(0, 3.6)], Seconds(3.6));

        var committed = Assert.Single(decision.Committed);
        Assert.Equal("Good evening, everyone.", committed.Text);
        Assert.Equal("Tonight we are going", Assert.Single(decision.Pending).Text);
        Assert.Equal(Seconds(2.2), decision.TrimTo);
        Assert.False(decision.EndOfUtterance);
    }

    [Fact]
    public void ChangingSentenceIsNotCommittedMidUtterance()
    {
        RecognizedSegment[] previous = [Segment("Good evening every one.", 0, 2.0), Segment("Tonight", 2.4, 2.8)];
        RecognizedSegment[] current = [Segment("Good evening, Ebrahim.", 0, 2.0), Segment("Tonight we are", 2.4, 3.4)];

        var decision = _policy.Decide(current, previous, [Speech(0, 3.4)], Seconds(3.4));

        Assert.Empty(decision.Committed);
        Assert.Equal(2, decision.Pending.Count);
    }

    [Fact]
    public void VeryLongBufferIsForcedToCommit()
    {
        RecognizedSegment[] hypothesis = [Segment("a never ending stream of words without any pause", 0, 26.5)];

        var decision = _policy.Decide(hypothesis, [], [Speech(0, 27)], _policy.MaximumBuffer);

        Assert.Single(decision.Committed);
        Assert.Equal(Seconds(26.5), decision.TrimTo);
    }

    [Theory]
    [InlineData("Hello there.", true)]
    [InlineData("Is it true?\"", true)]
    [InlineData("Wait...", false)]
    [InlineData("Well…", false)]
    [InlineData("and then", false)]
    [InlineData("你好。", true)]
    public void DetectsSentenceEndings(string text, bool expected) =>
        Assert.Equal(expected, StreamingCommitPolicy.EndsSentence(text));

    [Fact]
    public void StablePrefixIgnoresCaseAndPunctuation()
    {
        RecognizedSegment[] previous = [Segment("hello world", 0, 1), Segment("foo", 1, 2)];
        RecognizedSegment[] current = [Segment("Hello, world!", 0, 1), Segment("bar", 1, 2)];

        Assert.Equal(1, StreamingCommitPolicy.StablePrefixLength(current, previous));
    }

    private static RecognizedSegment Segment(string text, double start, double end) => new(text, Seconds(start), Seconds(end));

    private static SpeechSpan Speech(double start, double end) => new(Seconds(start), Seconds(end));

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);
}
