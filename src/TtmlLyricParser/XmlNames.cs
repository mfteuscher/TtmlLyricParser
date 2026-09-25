using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;

namespace TtmlLyricParser;

internal static class XmlNames {
    internal static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";
    internal static readonly XNamespace Metadata = "http://www.w3.org/ns/ttml#metadata";
    internal static readonly XNamespace Parameter = "http://www.w3.org/ns/ttml#parameter";
    internal static readonly XNamespace Apple = "http://music.apple.com/lyric-ttml-internal";
    internal static readonly XNamespace ITunes = "http://itunes.apple.com/lyric-ttml-extensions";

    internal static SourceLocation Location(XObject? node) => node?.Annotation<SourceLocation>() ??
        (node is IXmlLineInfo info ? new(info.LineNumber, info.LinePosition) : new(0, 0));

    internal static string? Inherited(XElement element, XName attribute) =>
        element.AncestorsAndSelf().Select(e => (string?)e.Attribute(attribute)).FirstOrDefault(v => v is not null);

    internal static ImmutableArray<string> Tokens(string? value) =>
        (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
}
