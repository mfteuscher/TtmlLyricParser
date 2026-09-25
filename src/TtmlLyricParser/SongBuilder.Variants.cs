using System.Collections.Immutable;
using System.Xml.Linq;

namespace TtmlLyricParser;

internal sealed partial class SongBuilder {
    private ImmutableArray<LyricVariant> Variants(LyricSection? body) {
        var lines = (body?.Lines ?? []).Where(line => !string.IsNullOrEmpty(line.Key))
            .ToLookup(line => line.Key!, StringComparer.Ordinal);
        var variants = ImmutableArray.CreateBuilder<LyricVariant>();
        foreach (var container in root.Descendants().Where(e => e.Name.Namespace == XmlNames.Apple &&
                     e.Name.LocalName is "translations" or "transliterations")) {
            var kind = container.Name.LocalName == "translations"
                ? LyricVariantKind.Translation : LyricVariantKind.Transliteration;
            var name = XmlNames.Apple + (kind == LyricVariantKind.Translation ? "translation" : "transliteration");
            if (HasDirectText(container)) Uninterpreted(container);
            foreach (var track in container.Elements()) {
                var entries = ImmutableArray.CreateBuilder<LyricVariantEntry>();
                if (track.Name != name) Uninterpreted(track);
                else {
                    if (HasDirectText(track)) Uninterpreted(track);
                    foreach (var entry in track.Elements()) {
                        // Reject an unsupported entry as a whole rather than exposing truncated text.
                        if (entry.Name != XmlNames.Apple + "text" || entry.Descendants().Any(e =>
                                e.Name != XmlNames.Apple + "span" && e.Name != XmlNames.Tt + "span" &&
                                (e.Name != XmlNames.Tt + "br" || e.Nodes().Any()))) {
                            Uninterpreted(entry);
                            continue;
                        }
                        var key = (string?)entry.Attribute("for");
                        var matches = key is null ? [] : lines[key].Take(2).ToArray();
                        var line = matches.Length == 1 ? matches[0] : null;
                        if (key is not null && line is null)
                            Warn("UNRESOLVED_VARIANT_REFERENCE", $"Variant key '{key}' does not identify exactly one body line.", entry);
                        var text = NormalizeText(entry, XmlNames.Apple);
                        entries.Add(new(Id(entry), Language(entry), Project(entry), key, line, Source(entry)));

                        string Project(XElement e) => string.Concat(e.Nodes().Select(node => node switch {
                            XText t => text.GetValueOrDefault(t, ""),
                            XElement child when child.Name == XmlNames.Tt + "br" => "\n",
                            XElement child => Project(child),
                            _ => ""
                        }));
                    }
                }
                variants.Add(new(kind, Language(track), Source(track)) {
                    Type = (string?)track.Attribute("type"), Entries = entries.ToImmutable()
                });
            }
        }
        return variants.ToImmutable();
    }

    private static bool HasDirectText(XElement element) =>
        element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value));

    private void Uninterpreted(XElement element) => Warn("UNINTERPRETED_VARIANTS",
        "Unsupported variant content is preserved in Source without text interpretation.", element);
}
