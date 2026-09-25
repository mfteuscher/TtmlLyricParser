using System.Text;
using System.Xml.Linq;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace TtmlLyricParser.Tests;

[TestClass]
public sealed class ParserTests {
    private const string Ns = "http://www.w3.org/ns/ttml";
    private const string Apple = "http://music.apple.com/lyric-ttml-internal";
    private static ParseResult Parse(string xml, ParserOptions? options = null) {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return new TtmlParser().Parse(stream, options);
    }
    private static string Document(string body, string parameters = "", string head = "") =>
        $"<tt xmlns='{Ns}' xmlns:ttp='{Ns}#parameter' xmlns:ttm='{Ns}#metadata' xml:lang='en' {parameters}>{head}<body>{body}</body></tt>";
    private static SongLyrics Success(ParseResult result) {
        Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result.Lyrics!;
    }

    [TestMethod]
    [DataRow("ellie-goulding-for-your-entertainment.ttml", 48, 10, 1, 3.901)]
    [DataRow("twenty-one-pilots-city-walls.ttml", 57, 9, 1, 65.129)]
    [DataRow("twenty-one-pilots-tally.ttml", 44, 8, 1, 13.750)]
    [DataRow("huntr-x-ejae-audrey-nuna-rei-ami-kpop-demon-hunters-cast-golden.ttml", 49, 10, 1, 16.058)]
    [DataRow("twenty-one-pilots-routines-in-the-night.ttml", 41, 9, 1, 1.653)]
    [DataRow("the-weeknd-ariana-grande-save-your-tears-remix.ttml", 37, 11, 4, 7.352)]
    [DataRow("twenty-one-pilots-one-way.ttml", 41, 7, 1, 8.309)]
    [DataRow("twenty-one-pilots-drag-path.ttml", 39, 7, 1, 10.014)]
    public void SuppliedSamples(string name, int lines, int sections, int singers, double firstBegin) {
        var path = Path.Combine(AppContext.BaseDirectory, "sample-files", name);
        var song = Success(new TtmlParser().ParseFile(path));
        Assert.HasCount(lines, song.Lines);
        Assert.HasCount(sections, song.Body!.Children);
        Assert.HasCount(singers, song.Singers);
        Assert.AreEqual(TimingDialect.AppleLyrics, song.TimingDialect);
        Assert.IsNotEmpty(song.Songwriters);
        Assert.IsEmpty(song.Variants);
        // The sample source is the oracle for absolute timestamps and exact mixed text.
        var source = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var original = source.Descendants(XName.Get("p", Ns)).ToArray();
        foreach (var (line, element) in song.Lines.Zip(original)) {
            Assert.AreEqual(element.Value, line.Text);
            Assert.AreEqual((string?)element.Attribute(XName.Get("key", Apple)), line.Key);
            Assert.AreEqual((string?)element.Attribute(XName.Get("agent", Ns + "#metadata")), Assert.ContainsSingle(line.SingerIds));
        }
        Assert.AreEqual(TimeSpan.FromTicks((long)Math.Round(firstBegin * TimeSpan.TicksPerSecond)), song.Lines.First().Timing.Begin);
    }

    [TestMethod]
    public void SchemaErrorsPreventInterpretation() {
        var result = Parse(Document("<div><invalid begin='nonsense'/></div>"));
        Assert.IsNull(result.Lyrics);
        Assert.Contains(d => d.Code == "TTML_SCHEMA" && d.Location.Line > 0, result.Diagnostics);
        Assert.DoesNotContain(d => d.Code == "INVALID_TIME", result.Diagnostics);
    }

    [TestMethod]
    [DataRow("<tt/>")]
    [DataRow("<tt xmlns='http://www.w3.org/ns/ttml' />")]
    [DataRow("<tt")]
    [DataRow("<!DOCTYPE tt [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><tt>&x;</tt>")]
    public void InvalidInputFails(string xml) => Assert.IsFalse(Parse(xml).Success);

    [TestMethod]
    public void NestedStandardTimingUsesParentOriginAndClips() {
        var song = Success(Parse(Document("<div begin='10s' end='20s'><p begin='2s' dur='3s'><span begin='1s' end='9s'>word</span></p></div>")));
        var line = Assert.ContainsSingle(song.Lines);
        Assert.AreEqual(TimeSpan.FromSeconds(12), line.Timing.Begin);
        Assert.AreEqual(TimeSpan.FromSeconds(15), line.Timing.End);
        var span = Assert.IsInstanceOfType<LyricSpan>(Assert.ContainsSingle(line.Content));
        Assert.AreEqual(TimeSpan.FromSeconds(13), span.Timing.Begin);
        Assert.AreEqual(TimeSpan.FromSeconds(15), span.Timing.End);
    }

    [TestMethod]
    public void SequentialTimingUsesPreviousEnd() {
        var song = Success(Parse(Document("<div begin='10s' timeContainer='seq'><p dur='2s'>a</p><p begin='1s' dur='3s'>b</p></div>")));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(13) }, song.Lines.Select(l => l.Timing.Begin).ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(16), song.Body!.Timing.End);
    }

    [TestMethod]
    [DataRow("00:00:01.500", "", 1.5)]
    [DataRow("00:00:01:15", "ttp:frameRate='30'", 1.5)]
    [DataRow("00:00:00:15.1", "ttp:frameRate='30' ttp:subFrameRate='2'", 0.5166667)]
    [DataRow("30f", "ttp:frameRate='30' ttp:frameRateMultiplier='1000 1001'", 1.001)]
    [DataRow("150t", "ttp:tickRate='100'", 1.5)]
    [DataRow("1500ms", "", 1.5)]
    public void TimeExpressions(string begin, string parameters, double seconds) {
        var song = Success(Parse(Document($"<div><p begin='{begin}' dur='1s'>a</p></div>", parameters)));
        Assert.AreEqual(TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)), Assert.ContainsSingle(song.Lines).Timing.Begin);
    }

    [TestMethod]
    public void AppleDialectIsExplicitAndDoesNotChangeStandardTiming() {
        var xml = Document("<div begin='10.0'><p begin='11.0' end='12.0'>a</p></div>", $"xmlns:i='{Apple}' i:timing='Word'");
        Assert.AreEqual(TimeSpan.FromSeconds(11), Success(Parse(xml)).Lines.First().Timing.Begin);
        Assert.IsFalse(Parse(xml, new() { TimingDialect = TimingDialect.Standard }).Success);
    }

    [TestMethod]
    public void PreservesSyllablesBackgroundAndSingerOverrides() {
        var song = Success(Parse(Document("<div><p ttm:agent='v1'> <span>hel</span><span>lo</span> <span ttm:role='x-bg' ttm:agent='v2'><span>echo</span></span></p></div>",
            head: "<head><metadata><ttm:agent type='person' xml:id='v1'/><ttm:agent type='group' xml:id='v2'/></metadata></head>")));
        var line = Assert.ContainsSingle(song.Lines);
        Assert.AreEqual("hello echo", line.Text);
        var background = Assert.ContainsSingle(line.BackgroundVocals);
        Assert.AreEqual("echo", background.Text);
        Assert.AreEqual("v2", Assert.ContainsSingle(background.SingerIds));
        Assert.IsTrue(Assert.IsInstanceOfType<LyricSpan>(Assert.ContainsSingle(background.Content)).IsBackground);
    }

    [TestMethod]
    public void WhitespaceLanguageAndLineBreaks() {
        var song = Success(Parse(Document("<div><p> \n a <span xml:lang='ko'> b </span> c <br/> d <span xml:space='preserve'>  e  </span></p></div>")));
        var line = Assert.ContainsSingle(song.Lines);
        Assert.AreEqual("a b c\nd   e  ", line.Text);
        Assert.AreEqual("ko", line.Content.OfType<LyricSpan>().First().Language);
    }

    [TestMethod]
    [DataRow("<div xml:id='notSinger'><p ttm:agent='notSinger'>a</p></div>", "INVALID_REFERENCE")]
    [DataRow("<div><p begin='3s' end='2s'>a</p></div>", "INVALID_INTERVAL")]
    [DataRow("<div><p begin='00:61:00'>a</p></div>", "INVALID_TIME")]
    [DataRow("<div timeContainer='seq'><p>a</p><p>b</p></div>", "UNRESOLVED_SEQUENCE")]
    public void SemanticErrorsAreReported(string body, string code) {
        var result = Parse(Document(body));
        Assert.IsFalse(result.Success);
        Assert.Contains(d => d.Code == code, result.Diagnostics);
    }

    [TestMethod]
    public void UnknownMetadataAndVariantsSurviveSchemaPruning() {
        var song = Success(Parse(Document("<div><p>original</p></div>", $"xmlns:i='{Apple}' xmlns:z='urn:test'",
            "<head><metadata><z:custom z:value='42'>retained</z:custom><i:translations><i:translation xml:lang='fr'><z:line>bonjour</z:line></i:translation></i:translations><i:transliterations><i:transliteration xml:lang='ko-Latn'>annyeong</i:transliteration></i:transliterations></metadata></head>")));
        Assert.AreEqual(2, song.Variants.Length);
        Assert.AreEqual("fr", song.Variants[0].Language);
        Assert.AreEqual(LyricVariantKind.Transliteration, song.Variants[1].Kind);
        Assert.Contains(e => e.Name == XName.Get("custom", "urn:test") && e.Attributes[XName.Get("value", "urn:test")] == "42", Descendants(song.Source));
    }

    [TestMethod]
    public void ResourcesAreBoundedAndStreamRemainsOpen() {
        var xml = Document("<div><p>a</p></div>");
        Assert.IsFalse(Parse(xml, new() { MaxCharacters = 32 }).Success);
        Assert.IsFalse(Parse(xml, new() { MaxDepth = 2 }).Success);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Success(new TtmlParser().Parse(stream));
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    [DataRow(".ttml")]
    [DataRow(".dfxp")]
    [DataRow(".itt")]
    public void AcceptsFileExtensions(string extension) {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + extension);
        try {
            File.WriteAllText(path, Document("<div><p begin='00:00:01:15' dur='1s'>a</p></div>", "ttp:timeBase='smpte' ttp:frameRate='30'"));
            Assert.AreEqual(TimeSpan.FromSeconds(1.5), Success(new TtmlParser().ParseFile(path)).Lines.First().Timing.Begin);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow("00:01:00:00", "nonDrop", 600600000)]
    [DataRow("00:01:00:02", "dropNTSC", 600600000)]
    [DataRow("00:10:00:00", "dropNTSC", 5999994000)]
    [DataRow("00:02:00:04", "dropPAL", 1201200000)]
    public void SmpteClockUsesNominalFrameCount(string begin, string drop, long ticks) {
        var song = Success(Parse(Document($"<div><p begin='{begin}'>a</p></div>",
            $"ttp:timeBase='smpte' ttp:frameRate='30' ttp:frameRateMultiplier='1000 1001' ttp:dropMode='{drop}'")));
        Assert.AreEqual(TimeSpan.FromTicks(ticks), song.Lines.First().Timing.Begin);
    }

    [TestMethod]
    [DataRow("00:01:00:00", "dropNTSC")]
    [DataRow("00:02:00:03", "dropPAL")]
    public void DroppedFrameLabelsAreRejected(string begin, string drop) {
        var result = Parse(Document($"<div><p begin='{begin}'>a</p></div>",
            $"ttp:timeBase='smpte' ttp:dropMode='{drop}'"));
        Assert.Contains(d => d.Code == "INVALID_TIME", result.Diagnostics);
    }

    [TestMethod]
    public void StreamNeedNotSupportSeeking() {
        using var stream = new NonSeekingStream(Encoding.UTF8.GetBytes(Document("<div><p>text</p></div>")));
        Assert.AreEqual("text", Assert.ContainsSingle(Success(new TtmlParser().Parse(stream)).Lines).Text);
    }

    private sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes) {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    private static IEnumerable<SourceElement> Descendants(SourceElement e) =>
        e.Children.OfType<SourceElement>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
}
