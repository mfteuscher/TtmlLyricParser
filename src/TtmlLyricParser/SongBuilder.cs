using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;

namespace TtmlLyricParser;

internal sealed partial class SongBuilder(XElement root, TimingDialect dialect, List<ParseDiagnostic> diagnostics) {
    private TimingResolver timing = null!;
    private readonly Dictionary<XElement, SourceElement> snapshots = [];
    private SourceElement Source(XElement element) {
        if (snapshots.TryGetValue(element, out var cached)) return cached;
        var source = new SourceElement(element.Name, element.Attributes().ToImmutableDictionary(a => a.Name, a => a.Value),
            element.Nodes().SelectMany<XNode, SourceNode>(n => n switch {
                XElement child => [Source(child)],
                XText text => [new SourceText(text.Value)],
                _ => []
            }).ToImmutableArray(), XmlNames.Location(element));
        snapshots.Add(element, source);
        return source;
    }

    internal SongLyrics Build() {
        ValidateReferences();
        ValidateFeatures();
        timing = new(root, dialect);
        var singers = root.Descendants(XmlNames.Metadata + "agent").Select(e => new Singer(
            (string?)e.Attribute(XNamespace.Xml + "id") ?? "", (string?)e.Attribute("type") ?? "other",
            e.Elements(XmlNames.Metadata + "name").Select(n => n.Value).ToImmutableArray(), Source(e))).ToImmutableArray();
        var body = root.Element(XmlNames.Tt + "body") is { } element ? Section(element) : null;
        var variants = Variants(body);
        return new((string?)root.Attribute(XNamespace.Xml + "lang"),
            root.Element(XmlNames.Tt + "head")?.Descendants(XmlNames.Metadata + "title").Select(e => e.Value).ToImmutableArray() ?? [],
            root.Descendants(XmlNames.Apple + "songwriter").Select(e => e.Value).ToImmutableArray(), singers,
            body, variants, dialect, Source(root));
    }

    private LyricSection Section(XElement element) => new(Id(element),
        (string?)element.Attribute(XmlNames.Apple + "songPart") ?? (string?)element.Attribute(XmlNames.ITunes + "song-part"),
        timing.Get(element), element.Elements().SelectMany<XElement, LyricBlock>(e =>
            e.Name == XmlNames.Tt + "div" ? [Section(e)] : e.Name == XmlNames.Tt + "p" ? [Line(e)] : []).ToImmutableArray(),
        Source(element));

    private LyricLine Line(XElement element) {
        var text = NormalizeText(element);
        return new(Id(element), (string?)element.Attribute(XmlNames.Apple + "key"), Language(element),
            Agents(element), Roles(element), timing.Get(element), Content(element, text), Source(element));
    }

    private ImmutableArray<LyricContent> Content(XElement element, Dictionary<XText, string> text) =>
        element.Nodes().SelectMany<XNode, LyricContent>(node => node switch {
            XText t => [new LyricText(text.GetValueOrDefault(t, ""))],
            XElement e when e.Name == XmlNames.Tt + "br" => [new LyricBreak()],
            XElement e when e.Name == XmlNames.Tt + "span" => [new LyricSpan(Id(e), Language(e), Agents(e), Roles(e),
                timing.Get(e), Content(e, text), Source(e))],
            _ => []
        }).ToImmutableArray();

    private void ValidateReferences() {
        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.DescendantsAndSelf())
            if (Id(e) is { } id && !ids.TryAdd(id, e))
                throw new ParseFailure("DUPLICATE_ID", $"Duplicate xml:id '{id}'.", e);
        foreach (var e in root.DescendantsAndSelf()) {
            Check(e, (string?)e.Attribute(XmlNames.Metadata + "agent"), XmlNames.Metadata + "agent");
            Check(e, (string?)e.Attribute("style"), XmlNames.Tt + "style");
            Check(e, (string?)e.Attribute("region"), XmlNames.Tt + "region");
            if (e.Name == XmlNames.Metadata + "actor") Check(e, (string?)e.Attribute("agent"), XmlNames.Metadata + "agent");
        }
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        foreach (var e in ids.Values.Where(e => e.Name == XmlNames.Tt + "style")) Visit(e);
        return;

        void Check(XElement e, string? value, XName expected) {
            foreach (var id in XmlNames.Tokens(value))
                if (!ids.TryGetValue(id, out var target) || target.Name != expected)
                    throw new ParseFailure("INVALID_REFERENCE", $"Reference '{id}' does not identify a {expected.LocalName}.", e);
        }
        void Visit(XElement e) {
            var id = Id(e)!;
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new ParseFailure("CYCLIC_STYLE", "Style references contain a cycle.", e);
            // Keep reference graph traversal bounded independently of XML nesting depth.
            if (visiting.Count > 128) throw new ParseFailure("STYLE_DEPTH", "Style reference chain exceeds 128 entries.", e);
            foreach (var reference in XmlNames.Tokens((string?)e.Attribute("style"))) Visit(ids[reference]);
            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private void ValidateFeatures() {
        foreach (var e in root.DescendantsAndSelf()) {
            if (e.Attribute("condition") is not null)
                throw new ParseFailure("UNSUPPORTED_CONDITION", "Conditional TTML content is not yet supported.", e);
            if (e.Name == XmlNames.Tt + "set" || e.Name == XmlNames.Tt + "animate")
                Warn("PRESERVED_ANIMATION", "Animation is preserved in Source but is not evaluated.", e);
        }
        if (root.Element(XmlNames.Tt + "head") is { } head &&
            (head.Element(XmlNames.Tt + "styling") is not null || head.Element(XmlNames.Tt + "layout") is not null))
            Warn("PRESERVED_PRESENTATION", "Styles and layout are preserved in Source; computed presentation is not resolved.", head);
        if (root.Attributes().Any(a => a.Name.Namespace == XmlNames.Parameter && a.Name.LocalName.Contains("Profile", StringComparison.OrdinalIgnoreCase)) ||
            root.Descendants(XmlNames.Parameter + "profile").Any())
            Warn("PROFILE_NOT_VALIDATED", "TTML2 structure is validated; declared profile conformance is not checked.", root);
        var unknown = root.Descendants().Where(e => e.Name.Namespace != XmlNames.Tt && e.Name.Namespace != XmlNames.Metadata &&
            e.Name.Namespace != XmlNames.Parameter && e.Name.Namespace != XmlNames.Apple).Select(e => e.Name).Distinct();
        foreach (var name in unknown) Warn("PRESERVED_EXTENSION", $"Extension {name} is preserved in Source without interpretation.", root);
    }

    private void Warn(string code, string message, XElement source) => diagnostics.Add(new(code, DiagnosticSeverity.Warning,
        DiagnosticStage.Extensions, message, XmlNames.Location(source)));
    private static string? Id(XElement element) => (string?)element.Attribute(XNamespace.Xml + "id");
    private static string? Language(XElement element) => XmlNames.Inherited(element, XNamespace.Xml + "lang");
    private static ImmutableArray<string> Agents(XElement element) => XmlNames.Tokens(XmlNames.Inherited(element, XmlNames.Metadata + "agent"));
    private static ImmutableArray<string> Roles(XElement element) => XmlNames.Tokens(XmlNames.Inherited(element, XmlNames.Metadata + "role"));

    private static Dictionary<XText, string> NormalizeText(XElement line, XNamespace? additionalInlineNamespace = null) {
        var values = new Dictionary<XText, StringBuilder>();
        StringBuilder? pendingSpace = null;
        var hasText = false;
        Walk(line);
        return values.ToDictionary(p => p.Key, p => p.Value.ToString());

        void Walk(XElement element) {
            foreach (var node in element.Nodes()) {
                if (node is XElement child) {
                    if (child.Name == XmlNames.Tt + "br") { pendingSpace = null; hasText = false; }
                    else if (child.Name == XmlNames.Tt + "span" ||
                        (additionalInlineNamespace is not null && child.Name == additionalInlineNamespace + "span")) Walk(child);
                    continue;
                }
                if (node is not XText text) continue;
                var builder = new StringBuilder();
                values.Add(text, builder);
                var preserve = XmlNames.Inherited(element, XNamespace.Xml + "space") == "preserve";
                foreach (var character in text.Value) {
                    if (!preserve && character is ' ' or '\t' or '\r' or '\n') {
                        if (hasText) pendingSpace ??= builder;
                        continue;
                    }
                    if (pendingSpace is not null) { pendingSpace.Append(' '); pendingSpace = null; }
                    builder.Append(character);
                    hasText = character != '\n';
                }
            }
        }
    }
}
