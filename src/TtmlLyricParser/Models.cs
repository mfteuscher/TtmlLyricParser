using System.Collections.Immutable;
using System.Xml.Linq;

namespace TtmlLyricParser;

public enum DiagnosticSeverity { Warning, Error }
public enum DiagnosticStage { Input, Schema, Semantics, Extensions }
public enum TimingDialect { Auto, Standard, AppleLyrics }
public enum LyricVariantKind { Translation, Transliteration }

public sealed record SourceLocation(int Line, int Column);
public sealed record ParseDiagnostic(string Code, DiagnosticSeverity Severity,
    DiagnosticStage Stage, string Message, SourceLocation Location);

public sealed record ParserOptions {
    public TimingDialect TimingDialect { get; init; } = TimingDialect.Auto;
    public long MaxCharacters { get; init; } = 16 * 1024 * 1024;
    public int MaxDepth { get; init; } = 128;
}

public sealed record ParseResult(SongLyrics? Lyrics, ImmutableArray<ParseDiagnostic> Diagnostics) {
    public bool Success => Lyrics is not null;
}

/// <summary>Null end denotes an unresolved or unbounded end, not zero duration.</summary>
public sealed record LyricTiming(TimeSpan Begin, TimeSpan? End,
    string? SourceBegin, string? SourceEnd, string? SourceDuration);

/// <summary>Immutable XML infoset snapshot. Attribute names use expanded namespace names.</summary>
public abstract record SourceNode;
public sealed record SourceText(string Value) : SourceNode;
public sealed record SourceElement(XName Name, ImmutableDictionary<XName, string> Attributes,
    ImmutableArray<SourceNode> Children, SourceLocation Location) : SourceNode;

public sealed record Singer(string Id, string Type, ImmutableArray<string> Names, SourceElement Source);

public abstract record LyricContent;
public sealed record LyricText(string Value) : LyricContent;
public sealed record LyricBreak : LyricContent;
public sealed record LyricSpan(string? Id, string? Language, ImmutableArray<string> SingerIds,
    ImmutableArray<string> Roles, LyricTiming Timing, ImmutableArray<LyricContent> Content,
    SourceElement Source) : LyricContent {
    public bool IsBackground => Roles.Contains("x-bg");
    public string Text => LyricTextProjection.GetText(Content);
}

public abstract record LyricBlock;
public sealed record LyricLine(string? Id, string? Key, string? Language,
    ImmutableArray<string> SingerIds, ImmutableArray<string> Roles, LyricTiming Timing,
    ImmutableArray<LyricContent> Content, SourceElement Source) : LyricBlock {
    public string Text => LyricTextProjection.GetText(Content);
    public string ForegroundVocals => LyricTextProjection.GetText(Content, excludeBackground: true);
    public IEnumerable<LyricSpan> BackgroundVocals => LyricTextProjection.Background(Content);
}

public sealed record LyricSection(string? Id, string? Label, LyricTiming Timing,
    ImmutableArray<LyricBlock> Children, SourceElement Source) : LyricBlock {
    public IEnumerable<LyricLine> Lines => Children.SelectMany(block => block switch {
        LyricLine line => [line],
        LyricSection section => section.Lines,
        _ => Enumerable.Empty<LyricLine>()
    });
}

/// <summary>A variant track. Entries retain source order, which does not imply line alignment.</summary>
public sealed record LyricVariant(LyricVariantKind Kind, string? Language, SourceElement Source) {
    public string? Type { get; init; }
    public ImmutableArray<LyricVariantEntry> Entries { get; init; } = [];
}

/// <summary>
/// Interpreted text with its original identifier and unqualified "for" key.
/// Line is set only when SourceKey exactly matches one body line's Apple key.
/// Text includes background vocals; inline attributes and timing remain in Source.
/// </summary>
public sealed record LyricVariantEntry(string? Id, string? Language, string Text,
    string? SourceKey, LyricLine? Line, SourceElement Source);

public sealed record SongLyrics(string? Language, ImmutableArray<string> Titles,
    ImmutableArray<string> Songwriters, ImmutableArray<Singer> Singers, LyricSection? Body,
    ImmutableArray<LyricVariant> Variants, TimingDialect TimingDialect, SourceElement Source) {
    public IEnumerable<LyricLine> Lines => Body?.Lines ?? [];
}

internal static class LyricTextProjection {
    internal static string GetText(ImmutableArray<LyricContent> content, bool excludeBackground = false) =>
        string.Concat(content.Select(node => node switch {
            LyricText text => text.Value,
            LyricBreak => "\n",
            LyricSpan span when !excludeBackground || !span.IsBackground => GetText(span.Content, excludeBackground),
            _ => ""
        }));

    internal static IEnumerable<LyricSpan> Background(ImmutableArray<LyricContent> content) =>
        content.OfType<LyricSpan>().SelectMany(span => span.IsBackground ? [span] : Background(span.Content));
}
