using System.Net;
using System.Text;
using LinguaFlow.Infrastructure;

namespace LinguaFlow.UnitTests;

public sealed class SpeechFocusFilterTests
{
    private const int Rate = SpeechFocusFilter.SampleRate;

    [Theory]
    [InlineData(110)]
    [InlineData(200)]
    [InlineData(320)]
    public void EstimatesPitchOfVoicedFrame(double frequency)
    {
        var frame = new float[256];
        for (var index = 0; index < frame.Length; index++)
        {
            frame[index] = (float)Harmonics(2 * Math.PI * frequency * index / 8000, 0.3);
        }

        Assert.InRange(SpeechFocusFilter.EstimatePitch(frame), frequency * 0.97, frequency * 1.03);
    }

    [Fact]
    public void NoiseIsUnvoiced()
    {
        var random = new Random(3);
        var frame = Enumerable.Range(0, 256).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();

        Assert.Equal(0, SpeechFocusFilter.EstimatePitch(frame));
    }

    [Fact]
    public void SustainedNotesAreTreatedAsSinging()
    {
        var filter = new SpeechFocusFilter();
        filter.Append(Singing(TimeSpan.FromSeconds(3), 0.25));

        Assert.Equal(FocusVerdict.Singing, filter.Evaluate(0, 3 * Rate, 0.9));
    }

    [Fact]
    public void GlidingSyllablesAreTreatedAsSpeech()
    {
        var filter = new SpeechFocusFilter();
        filter.Append(Speech(TimeSpan.FromSeconds(3), 0.25));

        Assert.Equal(FocusVerdict.Accepted, filter.Evaluate(0, 3 * Rate, 0.9));
    }

    [Fact]
    public void SpeechOverQuieterMusicIsStillSpeech()
    {
        var filter = new SpeechFocusFilter();
        var speech = Speech(TimeSpan.FromSeconds(3), 0.25);
        var music = Singing(TimeSpan.FromSeconds(3), 0.035);
        filter.Append(speech.Zip(music, (a, b) => a + b).ToArray());

        Assert.Equal(FocusVerdict.Accepted, filter.Evaluate(0, 3 * Rate, 0.85));
    }

    [Fact]
    public void LowConfidenceTextIsDropped()
    {
        var filter = new SpeechFocusFilter();
        filter.Append(Speech(TimeSpan.FromSeconds(2), 0.25));

        Assert.Equal(FocusVerdict.LowConfidence, filter.Evaluate(0, 2 * Rate, 0.2));
        Assert.Equal(FocusVerdict.Accepted, filter.Evaluate(0, 2 * Rate, 0));
    }

    [Fact]
    public void FarQuieterVoiceThanMainSpeakerIsBackground()
    {
        var filter = new SpeechFocusFilter();
        filter.Append(Speech(TimeSpan.FromSeconds(2), 0.3));
        filter.ObserveMainSpeaker(0, 2 * Rate);
        filter.Append(Speech(TimeSpan.FromSeconds(2), 0.02));

        Assert.Equal(FocusVerdict.Background, filter.Evaluate(2 * Rate, 4 * Rate, 0.9));
    }

    [Fact]
    public void MainSpeakerLevelRelaxesOverTime()
    {
        var filter = new SpeechFocusFilter();
        filter.Append(Speech(TimeSpan.FromSeconds(2), 0.3));
        filter.ObserveMainSpeaker(0, 2 * Rate);
        filter.Append(new float[20 * Rate]);
        filter.Append(Speech(TimeSpan.FromSeconds(2), 0.02));

        // 20 sn sonra kullanıcı sesi kısmış olabilir: aynı kısık ses artık ana konuşmacı sayılır.
        Assert.Equal(FocusVerdict.Accepted, filter.Evaluate(22 * Rate, 24 * Rate, 0.9));
    }

    [Fact]
    public void BandFilterRemovesBassAndKeepsVoice()
    {
        Assert.True(Gain(40) < 0.25);
        Assert.True(Gain(1000) > 0.95);
    }

    private static double Gain(double frequency)
    {
        var filter = new SpeechBandFilter(Rate);
        double input = 0, output = 0;
        for (var index = 0; index < Rate; index++)
        {
            var sample = (float)Math.Sin(2 * Math.PI * frequency * index / Rate);
            var filtered = filter.Process(sample);
            if (index >= Rate / 2)
            {
                input += sample * sample;
                output += filtered * filtered;
            }
        }

        return Math.Sqrt(output / input);
    }

    /// <summary>Legato şarkı: her biri 500 ms tutulan notalar, hafif vibrato.</summary>
    private static float[] Singing(TimeSpan duration, double amplitude)
    {
        double[] notes = [220, 247, 262, 294, 262, 247];
        var samples = new float[(int)(duration.TotalSeconds * Rate)];
        var phase = 0d;
        for (var index = 0; index < samples.Length; index++)
        {
            var time = index / (double)Rate;
            var note = notes[(int)(time / 0.5) % notes.Length];
            var frequency = note * Math.Pow(2, 0.25 * Math.Sin(2 * Math.PI * 5.5 * time) / 12);
            phase += 2 * Math.PI * frequency / Rate;
            samples[index] = (float)Harmonics(phase, amplitude);
        }

        return samples;
    }

    /// <summary>Konuşma benzeri: perdesi sürekli kayan 150 ms'lik heceler, aralarında 100 ms ünsüz/sessizlik.</summary>
    private static float[] Speech(TimeSpan duration, double amplitude)
    {
        var samples = new float[(int)(duration.TotalSeconds * Rate)];
        var phase = 0d;
        for (var index = 0; index < samples.Length; index++)
        {
            var time = index / (double)Rate;
            var syllable = time % 0.25;
            var frequency = 150 + (45 * Math.Sin(2 * Math.PI * 1.7 * time)) + (20 * Math.Sin(2 * Math.PI * 4.3 * time));
            phase += 2 * Math.PI * frequency / Rate;
            var envelope = syllable < 0.15 ? Math.Sin(Math.PI * syllable / 0.15) : 0;
            samples[index] = (float)(Harmonics(phase, amplitude) * envelope);
        }

        return samples;
    }

    private static double Harmonics(double phase, double amplitude)
    {
        var value = 0d;
        for (var harmonic = 1; harmonic <= 6; harmonic++)
        {
            value += Math.Sin(harmonic * phase) / harmonic;
        }

        return value * amplitude / 1.5;
    }
}

public sealed class TranslationTextPreparerTests
{
    [Theory]
    [InlineData("Um, I think we should go.", "en", "I think we should go.")]
    [InlineData("So, uh, we went there, um.", "en", "So, we went there.")]
    [InlineData("Hmm... okay, let's start.", "en", "Okay, let's start.")]
    [InlineData("Ee ııı bence bu doğru.", "tr", "Ee bence bu doğru.")]
    [InlineData("Ähm, das ist gut.", "de", "Das ist gut.")]
    public void RemovesFillers(string input, string language, string expected) =>
        Assert.Equal(expected, TranslationTextPreparer.Prepare(input, language));

    [Fact]
    public void KeepsWordsThatAreFillersOnlyInOtherLanguages()
    {
        Assert.Equal("Eu comprei um livro.", TranslationTextPreparer.Prepare("Eu comprei um livro.", "pt"));
        Assert.Equal("Er kommt morgen.", TranslationTextPreparer.Prepare("Er kommt morgen.", "de"));
    }

    [Fact]
    public void DropsFalseStarts() =>
        Assert.Equal("What are you doing? I think so.",
            TranslationTextPreparer.Prepare("Wh- what are you doing? I- I think so.", "en"));

    [Fact]
    public void LowersShoutedSentences() =>
        Assert.Equal("I can't believe what i see", TranslationTextPreparer.Prepare("I CAN'T BELIEVE WHAT I SEE", "fr"));

    [Fact]
    public void LowersShoutedEnglishKeepingPronounI() =>
        Assert.Equal("I can't believe what I see!", TranslationTextPreparer.Prepare("I CAN'T BELIEVE WHAT I SEE!", "en"));

    [Fact]
    public void KeepsRomanNumeralsAndAcknowledgements()
    {
        Assert.Equal("Henry III was king.", TranslationTextPreparer.Prepare("Henry III was king.", "en"));
        Assert.Equal("Mhm, right.", TranslationTextPreparer.Prepare("Mhm, right.", "en"));
    }

    [Fact]
    public void ContextTailKeepsWholeWords()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 100));
        var tail = LocalWhisperTranslationProvider.ContextTail(text)!;

        Assert.True(tail.Length <= 280);
        Assert.StartsWith("word", tail, StringComparison.Ordinal);
        Assert.Null(LocalWhisperTranslationProvider.ContextTail("  "));
    }
}

public sealed class ContextualTranslationTests
{
    [Fact]
    public async Task SendsContextAndReturnsOnlyTheNewSentence()
    {
        var handler = new RecordingHandler("""[[["Filmi gördüm.\n","I saw the movie.\n",null,null,3],["Harikaydı.","It was great.",null,null,3]],null,"en"]""");
        using var translator = new GoogleFreeTranslator(new HttpClient(handler));

        var result = await translator.TranslateAsync("It was great.", "I saw the movie.", "en", "tr", CancellationToken.None);

        Assert.Equal("Harikaydı.", result);
        Assert.Single(handler.Requests);
        Assert.Contains("I saw the movie.\nIt was great.", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallsBackToSentenceAloneWhenLinesAreMerged()
    {
        var handler = new RecordingHandler(
            """[[["Filmi gördüm, harikaydı.","I saw the movie. It was great.",null,null,3]],null,"en"]""",
            """[[["Harikaydı.","It was great.",null,null,3]],null,"en"]""");
        using var translator = new GoogleFreeTranslator(new HttpClient(handler));

        var result = await translator.TranslateAsync("It was great.", "I saw the movie.", "en", "tr", CancellationToken.None);

        Assert.Equal("Harikaydı.", result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Filmi gördüm.\nHarikaydı.", "Harikaydı.")]
    [InlineData("Filmi gördüm. Harikaydı.", null)]
    [InlineData("A\nB\nC", null)]
    public void ExtractsSecondLine(string translated, string? expected) =>
        Assert.Equal(expected, GoogleFreeTranslator.ExtractLastLine(translated));

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(WebUtility.UrlDecode(body));
            var response = responses[Math.Min(Requests.Count - 1, responses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
