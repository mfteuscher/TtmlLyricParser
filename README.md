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
