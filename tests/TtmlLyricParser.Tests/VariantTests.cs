using System.Text;
using System.Xml.Linq;

namespace TtmlLyricParser.Tests;

[TestClass]
public sealed class VariantTests {
    private static ParseResult Parse(string payload, string lines = "<p i:key='L1'>Original</p>") {
        var xml = $"""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:i="http://music.apple.com/lyric-ttml-internal" xmlns:z="urn:unknown" xml:lang="en">
              <head><metadata>{payload}</metadata></head>
              <body><div>{lines}</div></body>
            </tt>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return new TtmlParser().Parse(stream);
    }

    private static SongLyrics Success(ParseResult result) {
        Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        return result.Lyrics!;
    }

    [TestMethod]
    public void SidecarTracksExposeOrderedTextLanguageAndKeyedLines() {
        var result = new TtmlParser().ParseFile(Path.Combine(AppContext.BaseDirectory, "sample-files", "structured-variants.ttml"));
        var song = Success(result);
        Assert.HasCount(3, song.Variants);
        var french = song.Variants[0];
        Assert.AreEqual("fr", french.Language);
        Assert.AreEqual("subtitle", french.Type);
        Assert.HasCount(2, french.Entries);
        var entry = french.Entries[0];
        Assert.AreEqual("Bonjour (écho)", entry.Text);
        Assert.AreEqual("fr", entry.Language);
        Assert.AreEqual("translated", entry.Id);
        Assert.AreEqual("second", entry.SourceKey);
        Assert.AreSame(song.Lines.Last(), entry.Line);
        Assert.AreSame(french.Source.Children.OfType<SourceElement>().First(), entry.Source);
        Assert.AreEqual("es", french.Entries[1].Language);
        Assert.AreSame(song.Lines.First(), french.Entries[1].Line);
        Assert.AreEqual("replacement", song.Variants[1].Type);
        var roman = song.Variants[2];
        Assert.AreEqual(LyricVariantKind.Transliteration, roman.Kind);
        Assert.AreEqual("ja-Latn", roman.Entries[0].Language);
        Assert.AreEqual("konnichiwa", roman.Entries[0].Text);
        Assert.AreEqual("1s", roman.Entries[0].Source.Children.OfType<SourceElement>().First().Attributes[XName.Get("begin")]);
        Assert.DoesNotContain(d => d.Code is "UNINTERPRETED_VARIANTS" or "UNRESOLVED_VARIANT_REFERENCE", result.Diagnostics);
    }

    [TestMethod]
    [DataRow("", "<p i:key='L1'>Original</p>", false)]
    [DataRow("for='missing'", "<p xml:id='missing' i:key='L1'>Original</p>", true)]
    [DataRow("for='L1'", "<p i:key='L1'>One</p><p i:key='L1'>Two</p>", true)]
    [DataRow("for=''", "<p i:key=''>Original</p>", true)]
    [DataRow("for='l1'", "<p i:key='L1'>Original</p>", true)]
    public void DoesNotGuessAssociations(string attribute, string lines, bool warning) {
        var result = Parse($"<i:translations><i:translation><i:text {attribute}>Original</i:text></i:translation></i:translations>", lines);
        var entry = Assert.ContainsSingle(Assert.ContainsSingle(Success(result).Variants).Entries);
        Assert.IsNull(entry.Line);
        Assert.AreEqual("Original", entry.Text);
        Assert.AreEqual("en", entry.Language);
        Assert.AreEqual(warning, result.Diagnostics.Any(d => d.Code == "UNRESOLVED_VARIANT_REFERENCE"));
    }

    [TestMethod]
    public void MixedTextHonorsWhitespaceAndBreaksWithoutInventingTiming() {
        var result = Parse("""
            <i:translations><i:translation><i:text for="L1"> a <span>b</span><br/> c <i:span xml:space="preserve">  d  </i:span></i:text></i:translation></i:translations>
            """);
        Assert.AreEqual("a b\nc   d  ", Success(result).Variants[0].Entries[0].Text);
    }

    [TestMethod]
    [DataRow("<i:translation><z:line>unknown</z:line></i:translation>")]
    [DataRow("<i:translation>unstructured</i:translation>")]
    [DataRow("<z:translation><i:text>unknown track</i:text></z:translation>")]
    [DataRow("<i:translation><i:text>partial<z:span>unknown</z:span></i:text></i:translation>")]
    [DataRow("<i:translation><i:text><br>invalid</br></i:text></i:translation>")]
    public void UnsupportedEntriesRemainInSourceWithLocatedWarning(string payload) {
        var result = Parse($"<i:translations>{payload}</i:translations>");
        var variant = Assert.ContainsSingle(Success(result).Variants);
        Assert.IsEmpty(variant.Entries);
        Assert.IsNotEmpty(variant.Source.Children);
        Assert.Contains(d => d.Code == "UNINTERPRETED_VARIANTS" && d.Stage == DiagnosticStage.Extensions &&
            d.Severity == DiagnosticSeverity.Warning && d.Location.Line > 0, result.Diagnostics);
    }

    [TestMethod]
    public void PartialTrackKeepsKnownEntriesAndUnknownSource() {
        var result = Parse("<i:translations><i:translation><i:text for='L1'>Known</i:text><z:unknown>raw</z:unknown></i:translation></i:translations>");
        var track = Success(result).Variants[0];
        Assert.AreEqual("Known", Assert.ContainsSingle(track.Entries).Text);
        Assert.HasCount(2, track.Source.Children);
        Assert.Contains(d => d.Code == "UNINTERPRETED_VARIANTS", result.Diagnostics);
    }

    [TestMethod]
    [DataRow("<i:translations/>", 0, false)]
    [DataRow("<i:translations>raw</i:translations>", 0, true)]
    [DataRow("<i:translations><i:translation/></i:translations>", 1, false)]
    [DataRow("<i:translations><i:translation><i:text/></i:translation></i:translations>", 1, false)]
    public void EmptyPayloadsAndContainerText(string payload, int count, bool warning) {
        var result = Parse(payload);
        Assert.HasCount(count, Success(result).Variants);
        Assert.AreEqual(warning, result.Diagnostics.Any(d => d.Code == "UNINTERPRETED_VARIANTS"));
    }
}
