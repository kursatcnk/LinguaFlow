using System.Text.Json;
using LinguaFlow.Core;
using LinguaFlow.Infrastructure;

namespace LinguaFlow.UnitTests;

public sealed class TranscriptCleanerTests
{
    [Theory]
    [InlineData("[Music] Hello there", "Hello there")]
    [InlineData("♪ la la la ♪ Welcome back", "Welcome back")]
    [InlineData("(applause) Thank you all", "Thank you all")]
    [InlineData("  - Hello  ", "Hello")]
    public void RemovesAnnotations(string input, string expected) =>
        Assert.Equal(expected, TranscriptCleaner.Clean(input));

    [Theory]
    [InlineData("Thanks for watching!")]
    [InlineData("Altyazı M.K.")]
    [InlineData("Untertitel im Auftrag des ZDF, 2017")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("you")]
    public void DropsKnownHallucinations(string input) =>
        Assert.Equal(string.Empty, TranscriptCleaner.Clean(input));

    [Fact]
    public void KeepsRealSentenceContainingCommonWords() =>
        Assert.Equal("Thank you for coming tonight.", TranscriptCleaner.Clean("Thank you for coming tonight."));

    [Fact]
    public void CollapsesRunawayRepetition() =>
        Assert.Equal("yes yes and then", TranscriptCleaner.CollapseRepetitions("yes yes yes yes yes yes and then"));

    [Fact]
    public void JoinsLatinWithSpacesAndCjkWithout()
    {
        Assert.Equal("Hello. How are you?", TranscriptCleaner.Join(["Hello.", " How are you?"]));
        Assert.Equal("你好。我很好。", TranscriptCleaner.Join(["你好。", "我很好。"]));
    }
}

public sealed class TranslationEngineTests
{
    [Fact]
    public void ParsesFreeTranslateResponseAcrossSentences()
    {
        using var document = JsonDocument.Parse("""
            [[["Merhaba. ","Hello. ",null,null,3],["Nasılsın?","How are you?",null,null,3]],null,"en"]
            """);

        Assert.Equal("Merhaba. Nasılsın?", GoogleFreeTranslator.ParseResponse(document.RootElement));
    }

    [Fact]
    public void UnexpectedResponseYieldsEmptyText()
    {
        using var document = JsonDocument.Parse("""{"error":"x"}""");

        Assert.Equal(string.Empty, GoogleFreeTranslator.ParseResponse(document.RootElement));
    }

    [Theory]
    [InlineData("en", "tr", new[] { "en-tr" })]
    [InlineData("de", "en", new[] { "de-en" })]
    [InlineData("de", "tr", new[] { "de-en", "en-tr" })]
    [InlineData("zh", "zh-TW", new[] { "zh-en", "en-zh_hant" })]
    [InlineData("tr", "tr", new string[0])]
    [InlineData("auto", "tr", new string[0])]
    public void RoutesThroughEnglishPivot(string source, string target, string[] expected) =>
        Assert.Equal(expected, BergamotModelStore.Route(source, target));

    [Fact]
    public void RegistryPrefersDesktopReleaseAndBaseArchitecture()
    {
        using var document = JsonDocument.Parse("""
            {
              "models": {
                "ja-en": [
                  { "architecture": "tiny", "releaseStatus": "", "files": {
                      "model": { "path": "models/ja-en/tiny/model.jaen.intgemm.alphas.bin.gz", "uncompressedSize": 10, "uncompressedHash": "aa" },
                      "vocab": { "path": "models/ja-en/tiny/vocab.jaen.spm.gz" } } },
                  { "architecture": "base", "releaseStatus": "Release Desktop", "files": {
                      "model": { "path": "models/ja-en/base/model.jaen.intgemm.alphas.bin.gz", "uncompressedSize": 20, "uncompressedHash": "bb" },
                      "vocab": { "path": "models/ja-en/base/vocab.jaen.spm.gz" },
                      "lexicalShortlist": { "path": "models/ja-en/base/lex.50.50.jaen.s2t.bin.gz" } } }
                ],
                "en-zh": [
                  { "architecture": "base-memory", "releaseStatus": "Release", "files": {
                      "model": { "path": "models/en-zh/m/model.enzh.intgemm.alphas.bin.gz" },
                      "srcVocab": { "path": "models/en-zh/m/srcvocab.enzh.spm.gz" },
                      "trgVocab": { "path": "models/en-zh/m/trgvocab.enzh.spm.gz" } } }
                ]
              }
            }
            """);

        var registry = BergamotModelStore.ParseRegistry(document.RootElement);

        var japanese = registry["ja-en"];
        Assert.Contains("/base/", japanese.Model.Path, StringComparison.Ordinal);
        Assert.Equal("bb", japanese.Model.Sha256);
        Assert.Equal(3, japanese.ToDownloads("ja-en").Count);
        Assert.Contains("shortlist:", japanese.ToConfig(), StringComparison.Ordinal);

        var chinese = registry["en-zh"];
        var config = chinese.ToConfig();
        Assert.Contains("- srcvocab.enzh.spm", config, StringComparison.Ordinal);
        Assert.Contains("- trgvocab.enzh.spm", config, StringComparison.Ordinal);
        Assert.DoesNotContain("shortlist:", config, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zh", "zh-CN")]
    [InlineData("he", "iw")]
    [InlineData("tr", "tr")]
    [InlineData("auto", null)]
    public void MapsLanguageCodesForOnlineTranslation(string input, string? expected) =>
        Assert.Equal(expected, LanguageCatalog.ToGoogleCode(input));

    [Fact]
    public void TreatsRegionalVariantsAsSameLanguage()
    {
        Assert.True(LanguageCatalog.IsSameLanguage("zh", "zh-CN"));
        Assert.False(LanguageCatalog.IsSameLanguage("auto", "tr"));
        Assert.False(LanguageCatalog.IsSameLanguage("en", "tr"));
    }
}
