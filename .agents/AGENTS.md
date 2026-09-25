# Repository Guidelines

## Project Structure & Module Organization

`src/TtmlLyricParser/` contains the .NET 10 library. `TtmlParser.cs` handles XML input and diagnostics, `SchemaValidation.cs` checks TTML2 structure, `TimingResolver.cs` resolves time expressions, and `SongBuilder.cs` builds lyric models declared in `Models.cs`. The pinned W3C XSDs in `src/TtmlLyricParser/Schemas/` are embedded resources; consult that directory's README before changing them. `tests/TtmlLyricParser.Tests/` contains MSTest cases and TTML fixtures in `sample-files/`. The root `README.md` describes the public API and supported behavior.

## Build, Test, and Development Commands

- `dotnet build TtmlLyricParser.slnx` compiles the library and test project.
- `dotnet test TtmlLyricParser.slnx` runs the MSTest suite.
- `dotnet test tests/TtmlLyricParser.Tests/TtmlLyricParser.Tests.csproj --filter FullyQualifiedName~TimeExpressions` runs a focused group while changing timing behavior.

Use the .NET 10 SDK. This repository is a library and has no local executable. The test project currently links fixtures from `example-ttml-files/`, while the checked-in fixtures are under `tests/TtmlLyricParser.Tests/sample-files/`; align the project reference when working on fixture-based tests.

## Coding Style & Naming Conventions

Follow the existing C# style: four-space indentation, file-scoped namespaces, nullable reference types, and implicit usings. Use PascalCase for types, methods, and public properties; camelCase for parameters and locals. Keep XML namespace constants and parsing rules in their existing modules. No repository-wide formatter or lint configuration is checked in; keep formatting consistent with nearby code.

## Testing Guidelines

Use MSTest attributes such as `[TestMethod]` and `[DataRow]`. Name tests for the behavior they verify, as in `SchemaErrorsPreventInterpretation`. Add small inline TTML documents for focused edge cases and fixtures for realistic song examples. Check both parsed lyrics and diagnostic codes when changing validation or timing. There is no documented coverage threshold.

## Commit & Pull Request Guidelines

This repository has no commit history yet, so no commit message convention is established. Use a short imperative subject that identifies the change, such as `Handle nested span timing`. In pull requests, describe the behavior changed, note relevant TTML cases, include the test command and result, and link any related issue. Include before-and-after output when a model or diagnostic changes.
