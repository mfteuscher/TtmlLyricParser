using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TtmlLyricParser;

internal sealed partial class TimingResolver {
    private readonly TimingDialect dialect;
    private readonly decimal frameRate;
    private readonly decimal nominalFrameRate;
    private readonly decimal subFrameRate;
    private readonly decimal tickRate;
    private readonly bool smpte;
    private readonly string dropMode;
    private readonly Dictionary<XElement, Interval> intervals = [];

    private sealed record Interval(decimal Begin, decimal? End);

    internal TimingResolver(XElement root, TimingDialect dialect) {
        this.dialect = dialect;
        var timeBase = (string?)root.Attribute(XmlNames.Parameter + "timeBase") ?? "media";
        smpte = timeBase == "smpte";
        dropMode = smpte ? (string?)root.Attribute(XmlNames.Parameter + "dropMode") ?? "nonDrop" : "nonDrop";
        if (timeBase == "clock")
            throw new ParseFailure("UNSUPPORTED_TIME_BASE", "Wall-clock timing requires an external clock mapping.", root);
        if (smpte && (string?)root.Attribute(XmlNames.Parameter + "markerMode") == "discontinuous")
            throw new ParseFailure("UNSUPPORTED_MARKER_MODE", "Discontinuous SMPTE timing requires marker information.", root);
        if (smpte && dialect == TimingDialect.AppleLyrics)
            throw new ParseFailure("INCOMPATIBLE_TIMING_DIALECT", "Apple lyric timing requires the media time base.", root);

        nominalFrameRate = Positive(root, "frameRate", 30);
        subFrameRate = Positive(root, "subFrameRate", 1);
        var multiplier = XmlNames.Tokens((string?)root.Attribute(XmlNames.Parameter + "frameRateMultiplier"));
        frameRate = nominalFrameRate;
        if (multiplier.Length != 0) {
            if (multiplier.Length != 2 || !decimal.TryParse(multiplier[0], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var numerator) || numerator <= 0 ||
                !decimal.TryParse(multiplier[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) || denominator <= 0)
                throw new ParseFailure("INVALID_FRAME_RATE", "Frame-rate multiplier must contain two positive integers.", root);
            frameRate *= numerator / denominator;
        }
        tickRate = Positive(root, "tickRate", root.Attribute(XmlNames.Parameter + "frameRate") is not null
            ? frameRate * subFrameRate : 1);
        if (root.Element(XmlNames.Tt + "body") is { } body) {
            Resolve(body, 0);
            Clip(body, 0, null);
        }
    }

    internal LyricTiming Get(XElement element) {
        var interval = intervals[element];
        return new(ToTimeSpan(interval.Begin), interval.End is { } end ? ToTimeSpan(end) : null,
            (string?)element.Attribute("begin"), (string?)element.Attribute("end"), (string?)element.Attribute("dur"));
    }

    private Interval Resolve(XElement element, decimal syncBase) {
        var beginValue = Read(element, "begin");
        var endValue = Read(element, "end");
        var duration = Read(element, "dur");
        var origin = dialect == TimingDialect.AppleLyrics ? 0 : syncBase;
        var begin = beginValue is { } b ? origin + b : syncBase;
        decimal? end = endValue is { } e ? origin + e : null;
        if (duration is { } d) end = Minimum(end, begin + d);
        if (end < begin)
            throw new ParseFailure("INVALID_INTERVAL", "The end precedes the begin time.", element);

        var sequential = (string?)element.Attribute("timeContainer") == "seq";
        if (sequential && dialect == TimingDialect.AppleLyrics)
            throw new ParseFailure("UNSUPPORTED_APPLE_SEQUENCE", "Apple absolute timing cannot be combined with sequential containers.", element);
        decimal? cursor = begin;
        var children = new List<Interval>();
        foreach (var child in element.Elements().Where(IsTimedContent)) {
            if (sequential && cursor is null)
                throw new ParseFailure("UNRESOLVED_SEQUENCE", "A sequential sibling has no resolved end time.", child);
            var childInterval = Resolve(child, sequential ? cursor!.Value : begin);
            children.Add(childInterval);
            cursor = childInterval.End;
        }
        // A container with only timed children derives its implicit duration from them.
        // Text and br content have indefinite intrinsic duration.
        if (end is null && children.Count > 0 && children.All(c => c.End is not null) &&
            !element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)) &&
            !element.Elements(XmlNames.Tt + "br").Any())
            end = sequential ? children[^1].End : children.Max(c => c.End);
        var interval = new Interval(begin, end);
        intervals.Add(element, interval);
        return interval;
    }

    private void Clip(XElement element, decimal parentBegin, decimal? parentEnd) {
        var interval = intervals[element];
        var begin = Math.Max(parentBegin, interval.Begin);
        var end = Minimum(parentEnd, interval.End);
        // Inactive content is retained as an empty active interval.
        if (end < begin) begin = end.Value;
        intervals[element] = new(begin, end);
        foreach (var child in element.Elements().Where(IsTimedContent)) Clip(child, begin, end);
    }

    private static bool IsTimedContent(XElement element) => element.Name.Namespace == XmlNames.Tt &&
        element.Name.LocalName is "body" or "div" or "p" or "span";

    private decimal? Read(XElement element, string attribute) {
        var value = (string?)element.Attribute(attribute);
        if (value is null) return null;
        var offset = Offset().Match(value);
        if (offset.Success) {
            var number = Number(offset.Groups[1].Value);
            if (smpte && dropMode != "nonDrop")
                throw new ParseFailure("UNSUPPORTED_DROP_OFFSET", "Use clock timecodes for drop-frame SMPTE timing.", element);
            if (smpte && offset.Groups[2].Value == "t") throw Invalid(element, value);
            var seconds = offset.Groups[2].Value switch {
                "h" => number * 3600,
                "m" => number * 60,
                "s" => number,
                "ms" => number / 1000,
                "f" => number / frameRate,
                "t" => number / tickRate,
                _ => throw new InvalidOperationException()
            };
            return smpte && offset.Groups[2].Value != "f" ? seconds * nominalFrameRate / frameRate : seconds;
        }
        var clock = Clock().Match(value);
        if (clock.Success) {
            var hours = Number(clock.Groups[1].Value);
            var minutes = Number(clock.Groups[2].Value);
            var seconds = Number(clock.Groups[3].Value);
            if (minutes >= 60 || seconds >= 60) throw Invalid(element, value);
            var result = hours * 3600 + minutes * 60 + seconds;
            var frames = 0m;
            var subframes = 0m;
            if (clock.Groups[4].Success) {
                if (clock.Groups[3].Value.Contains('.')) throw Invalid(element, value);
                frames = Number(clock.Groups[4].Value);
                subframes = clock.Groups[5].Success ? Number(clock.Groups[5].Value) : 0;
                if (frames >= nominalFrameRate || subframes >= subFrameRate) throw Invalid(element, value);
            }
            if (smpte) {
                // TTML2 Appendix I.3: the seconds field counts nominal frames.
                if (seconds == 0 && ((dropMode == "dropNTSC" && minutes % 10 != 0 && frames < 2) ||
                    (dropMode == "dropPAL" && minutes % 2 == 0 && minutes % 20 != 0 && frames < 4)))
                    throw Invalid(element, value);
                var dropped = dropMode switch {
                    "dropNTSC" => (hours * 54 + minutes - decimal.Floor(minutes / 10)) * 2,
                    "dropPAL" => (hours * 27 + decimal.Floor(minutes / 2) - decimal.Floor(minutes / 20)) * 4,
                    _ => 0
                };
                return (result * nominalFrameRate - dropped + frames + subframes / subFrameRate) / frameRate;
            }
            return result + (frames + subframes / subFrameRate) / frameRate;
        }
        if (dialect == TimingDialect.AppleLyrics && AppleClock().IsMatch(value)) {
            var parts = value.Split(':');
            var result = 0m;
            foreach (var part in parts) result = result * 60 + Number(part);
            if (parts.Length > 1 && Number(parts[^1]) >= 60) throw Invalid(element, value);
            return result;
        }
        throw Invalid(element, value);
    }

    private static ParseFailure Invalid(XElement element, string value) =>
        new("INVALID_TIME", $"Unsupported or invalid time expression '{value}'.", element);
    private static decimal Number(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static decimal Positive(XElement root, string name, decimal fallback) {
        if (root.Attribute(XmlNames.Parameter + name) is not { } attribute) return fallback;
        if (!decimal.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ParseFailure("INVALID_TIME_PARAMETER", $"{name} must be a positive integer.", root);
        return number;
    }
    private static decimal? Minimum(decimal? a, decimal? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
    private static TimeSpan ToTimeSpan(decimal seconds) =>
        TimeSpan.FromTicks((long)decimal.Round(seconds * TimeSpan.TicksPerSecond, 0, MidpointRounding.ToEven));

    [GeneratedRegex(@"\A([0-9]+(?:\.[0-9]+)?)(h|m|s|ms|f|t)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
    [GeneratedRegex(@"\A([0-9]{2,}):([0-9]{2}):([0-9]{2}(?:\.[0-9]+)?)(?::([0-9]{2})(?:\.([0-9]+))?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Clock();
    [GeneratedRegex(@"\A(?:[0-9]+:)?[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex AppleClock();
}

internal sealed class ParseFailure(string code, string message, XElement source) : Exception(message) {
    internal string Code { get; } = code;
    internal XElement SourceElement { get; } = source;
}
