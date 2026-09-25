using System.Net;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace TtmlLyricParser;

internal static class SchemaValidation {
    private static readonly Lazy<XmlSchemaSet> Schemas = new(CreateSchemas);
    private static readonly HashSet<string> StandardNamespaces =
    [
        XmlNames.Tt.NamespaceName, XmlNames.Metadata.NamespaceName, XmlNames.Parameter.NamespaceName,
        "http://www.w3.org/ns/ttml#styling", "http://www.w3.org/ns/ttml#audio",
        XNamespace.Xml.NamespaceName, "http://www.w3.org/1999/xlink", ""
    ];

    internal static void Validate(XDocument original, List<ParseDiagnostic> diagnostics) {
        // Preserve original line annotations by pruning a second load of the same infoset.
        // An annotation maps each validation node back to its original source node.
        var view = new XDocument(original);
        foreach (var pair in view.Descendants().Zip(original.Descendants()))
            pair.First.AddAnnotation(pair.Second);
        foreach (var element in view.Descendants().ToArray()) {
            if (!StandardNamespaces.Contains(element.Name.NamespaceName)) {
                element.Remove();
                continue;
            }
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration &&
                         !StandardNamespaces.Contains(a.Name.NamespaceName)).ToArray())
                attribute.Remove();
        }

        view.Validate(Schemas.Value, (sender, args) => {
            var node = sender as XObject;
            var element = node as XElement ?? (node as XAttribute)?.Parent;
            diagnostics.Add(new("TTML_SCHEMA", args.Severity == XmlSeverityType.Error
                    ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, DiagnosticStage.Schema,
                args.Message, XmlNames.Location(element?.Annotation<XElement>())));
        });
    }

    private static XmlSchemaSet CreateSchemas() {
        var resolver = new EmbeddedSchemaResolver();
        var schemas = new XmlSchemaSet { XmlResolver = resolver };
        using var stream = resolver.GetEntity(new Uri("https://schemas.local/ttml2.xsd"), null, typeof(Stream)) as Stream;
        using var reader = XmlReader.Create(stream!, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit },
            "https://schemas.local/ttml2.xsd");
        schemas.Add(null, reader);
        schemas.Compile();
        return schemas;
    }

    private sealed class EmbeddedSchemaResolver : XmlResolver {
        public override ICredentials? Credentials { set { } }

        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn) {
            if (absoluteUri.Scheme != "https" || absoluteUri.Host != "schemas.local" ||
                absoluteUri.Segments.Length != 2)
                throw new XmlException("Only bundled schema resources may be resolved.");
            return typeof(SchemaValidation).Assembly.GetManifestResourceStream(
                $"TtmlLyricParser.Schemas.{absoluteUri.Segments[^1]}")
                ?? throw new XmlException($"Missing bundled schema: {absoluteUri.Segments[^1]}");
        }
    }
}
