using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;

namespace TtmlLyricParser;

/// <summary>Validates TTML2 structure before interpreting song lyrics. Instances are thread safe.</summary>
public sealed class TtmlParser {
    /// <summary>Reads from the current position and leaves the caller's stream open.
    /// Invalid documents produce diagnostics; I/O and argument errors throw.</summary>
    public ParseResult Parse(Stream input, ParserOptions? options = null) {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxDepth);
        if (!Enum.IsDefined(options.TimingDialect)) throw new ArgumentOutOfRangeException(nameof(options));
        var diagnostics = new List<ParseDiagnostic>();
        try {
            var document = Load(input, options);
            if (document.Root is not { } root || root.Name != XmlNames.Tt + "tt") {
                diagnostics.Add(new("INVALID_ROOT", DiagnosticSeverity.Error, DiagnosticStage.Schema,
                    "Expected a tt root in the http://www.w3.org/ns/ttml namespace.", XmlNames.Location(document.Root)));
                return new(null, diagnostics.ToImmutableArray());
            }
            SchemaValidation.Validate(document, diagnostics);
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                return new(null, diagnostics.ToImmutableArray());

            var dialect = options.TimingDialect == TimingDialect.Auto
                ? root.Attribute(XmlNames.Apple + "timing") is not null ||
                  root.Descendants().Any(e => e.Attributes().Any(a => a.Name.Namespace == XmlNames.ITunes))
                    ? TimingDialect.AppleLyrics : TimingDialect.Standard
                : options.TimingDialect;
            if (dialect == TimingDialect.AppleLyrics)
                diagnostics.Add(new("APPLE_TIMING", DiagnosticSeverity.Warning, DiagnosticStage.Semantics,
                    "Using Apple lyric absolute timestamps and abbreviated clock expressions.", XmlNames.Location(root)));
            var lyrics = new SongBuilder(root, dialect, diagnostics).Build();
            return new(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? null : lyrics,
                diagnostics.ToImmutableArray());
        }
        catch (XmlException exception) {
            diagnostics.Add(new("INVALID_XML", DiagnosticSeverity.Error, DiagnosticStage.Input,
                exception.Message, new(exception.LineNumber, exception.LinePosition)));
        }
        catch (ParseFailure exception) {
            diagnostics.Add(new(exception.Code, DiagnosticSeverity.Error, DiagnosticStage.Semantics,
                exception.Message, XmlNames.Location(exception.SourceElement)));
        }
        catch (OverflowException) {
            diagnostics.Add(new("TIME_OVERFLOW", DiagnosticSeverity.Error, DiagnosticStage.Semantics,
                "A time value exceeds the supported numeric range.", new(0, 0)));
        }
        return new(null, diagnostics.ToImmutableArray());
    }

    public ParseResult ParseFile(string path, ParserOptions? options = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!new[] { ".ttml", ".dfxp", ".itt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return new(null, [new("UNSUPPORTED_EXTENSION", DiagnosticSeverity.Error, DiagnosticStage.Input,
                "Expected a .ttml, .dfxp, or .itt file.", new(0, 0))]);
        using var stream = File.OpenRead(path);
        return Parse(stream, options);
    }

    private static XDocument Load(Stream stream, ParserOptions options) {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
            MaxCharactersInDocument = options.MaxCharacters,
            IgnoreWhitespace = false
        });
        var document = new XDocument();
        var stack = new Stack<XElement>();
        while (reader.Read()) {
            if (reader.Depth >= options.MaxDepth)
                throw new XmlException($"Maximum XML depth ({options.MaxDepth}) exceeded.");
            switch (reader.NodeType) {
                case XmlNodeType.Element:
                    var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                    var info = (IXmlLineInfo)reader;
                    element.AddAnnotation(new SourceLocation(info.LineNumber, info.LinePosition));
                    if (reader.MoveToFirstAttribute()) {
                        do {
                            var name = reader.Name == "xmlns" ? XName.Get("xmlns") : XName.Get(reader.LocalName, reader.NamespaceURI);
                            element.Add(new XAttribute(name, reader.Value));
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                    if (stack.TryPeek(out var parent)) parent.Add(element); else document.Add(element);
                    if (!reader.IsEmptyElement) stack.Push(element);
                    break;
                case XmlNodeType.EndElement:
                    stack.Pop();
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    if (stack.TryPeek(out var container)) container.Add(new XText(reader.Value));
                    break;
            }
        }
        return document;
    }
}
