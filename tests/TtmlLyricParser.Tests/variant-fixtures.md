# Variant fixture provenance

`sample-files/structured-variants.ttml` is a synthetic conformance example with original placeholder text, not an Apple download. Its structure was checked against the [AMLL sidecar guide](https://amll.dev/en/guides/lyric/ttml#apple-music-style-translationtransliteration-sidecar) and [AMLL TTML specification, section 5.4](https://github.com/amll-dev/amll-ttml-db/blob/main/instructions/ttml-specification-en.md) on 2026-09-24.

These project-maintained sources document Apple internal namespace tracks, `text` entries, unqualified `for` references to body `itunes:key` values, translation `type`, language, and nested spans. They are community implementation documentation, not an Apple normative schema. The fixture deliberately reverses entry order to exercise key association independently of position. Other synthetic cases in `VariantTests.cs` exercise missing/ambiguous keys and unsupported payloads.

The checked-in Golden sample was inspected and still contains only an empty `translations` container, with no `transliterations`. The other seven original sample tests likewise provide no nonempty variant coverage. No production Apple variant export was available for verification. Timing and inline roles in variant spans are retained as source data; word-level alignment is not inferred.
