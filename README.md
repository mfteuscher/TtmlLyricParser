# TtmlLyricParser

A .NET 10 library that validates TTML2 structure and extracts structured song lyrics from `.ttml`, `.dfxp`, and `.itt` files.

```csharp
using TtmlLyricParser;

var result = new TtmlParser().ParseFile("song.ttml");
if (!result.Success)
{
    foreach (var diagnostic in result.Diagnostics)
        Console.WriteLine($"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}");
    return;
}

SongLyrics song = result.Lyrics!;
foreach (var line in song.Lines)
{
    Console.WriteLine($"{line.Timing.Begin}: {line.ForegroundVocals}");
    foreach (var background in line.BackgroundVocals)
        Console.WriteLine($"  Background: {background.Text}");
}
```

`Parse(Stream, ParserOptions?)` accepts non-seekable streams, starts at the current position, and leaves the stream open. `ParseFile` owns and closes its file stream. File extension matching is case insensitive. Invalid documents return diagnostics and no song; argument and I/O errors throw. Parser instances can be reused concurrently.

## Validation

1. Load XML with DTDs and external resources disabled, bounded to 16 Mi characters and 128 levels by default.
2. Validate a view of the document against the bundled W3C TTML2 XSDs. Foreign elements and attributes are pruned from this view; their original data remains available in the result.
3. Check references and supported timing semantics, then extract lyrics. Schema errors prevent lyric interpretation.

Schemas are embedded, pinned to the [8 November 2018 W3C Recommendation](https://www.w3.org/TR/2018/REC-ttml2-20181108/), and resolved offline. Document-supplied schema locations are never fetched. XSD validation is **not** full TTML2 or iTT profile conformance; many lexical and semantic constraints are not expressed by these XSDs. This release implements the checks described below, not a complete TTML processor.

## Data and timing

- `SongLyrics` exposes language, titles, songwriters, singers, body sections, lines, variants, and an immutable source tree.
- Sections retain nested ordering. Lines and spans retain singer IDs, roles, language, timing, source identifiers, and Apple line keys. A singer can be unnamed; IDs are not artist names.
- Mixed text, nested syllable spans, explicit line breaks, and `xml:space` are preserved in the lyric model. Default XML whitespace is normalized. `Text` includes background vocals; `ForegroundVocals` excludes spans with the `x-bg` role. No spaces are inserted between adjacent syllables.
- Standard TTML timing uses parent-relative coordinates and sequential/parallel containers. Explicit end and duration constraints are intersected with ancestor intervals. Inactive intervals are retained with zero duration. Untimed content starts at its inherited origin and may have a null end.
- Clock, offset, frames, subframes, ticks, frame-rate multipliers, and continuous SMPTE clock timecodes (nonDrop, dropNTSC, dropPAL) are supported. Decimal arithmetic is converted to `TimeSpan` ticks with midpoint-to-even rounding.
- The supplied Apple files use song-absolute timestamps and shortened clocks. Auto mode selects `AppleLyrics` when the root has Apple's internal `timing` attribute or Apple iTunes extension attributes occur. Every such parse emits `APPLE_TIMING`. Use `ParserOptions.TimingDialect` to force `Standard` or `AppleLyrics`; the filename does not select timing semantics.
- Apple section names, line keys, songwriters, agent references, and nested background vocals are interpreted. Apple audio metadata, including offsets, is retained in source data without changing timestamps.

`SourceElement` retains expanded XML names, attributes, text, children, and element line/column locations. Source trees share immutable nodes with projected lyrics. Comments, processing instructions, original prefix spelling, and byte formatting are not retained; this is not a byte-for-byte XML round-trip serializer.

## Current limits

| Feature | Behavior |
| --- | --- |
| Translation/transliteration payloads | Apple internal containers are exposed as `LyricVariant` payloads with language and source data. Alignment/text projection is not implemented; nonempty payloads emit a warning. All eight provided samples have empty translation containers. Other encodings remain in `Source`. |
| Styling, regions, ruby, animation, embedded media | Preserved in `Source`; no rendering, computed styling, or media fetching. |
| Declared TTML/iTT profiles | Preserved; profile conformance is not checked. |
| Wall-clock and discontinuous SMPTE | Fail explicitly because they require an external time mapping. |
| Drop-frame SMPTE offset expressions | Fail explicitly; use clock timecodes. |
| Conditional content | Fails explicitly; conditions are not evaluated. |
| Sequential sibling with unresolved duration | Fails explicitly; the parser does not invent a following start time. |
| Legacy TTML namespace aliases | Rejected; the TTML namespace must be `http://www.w3.org/ns/ttml`. |

Next work: verified translation/transliteration examples and alignment, complete TTML timing edge cases and profile checks, then computed presentation where needed. The parser currently favors explicit diagnostics over silently interpreting unsupported timing.

## Tests

```sh
dotnet test TtmlLyricParser.slnx
```

Tests use Microsoft's MSTest framework. They cover all eight files in `example-ttml-files/`, schema failures, timing, frame/drop-frame conversion, singer/background handling, whitespace, source preservation, resource limits, non-seekable streams, and all three extensions. The library itself has no NuGet dependencies.
