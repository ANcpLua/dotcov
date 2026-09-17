# Umbau-Protokoll (Task.md)

Jeder Checkpoint ist ein lokaler Commit. Build-/Testläufe sind Protokoll, kein Gate.

## Checkpoint 0: Ausgangslage

- Ausgangscommit: `23e9cb1` (`for documentation`) auf `main`.
- Bereits vorhandene, nicht committete Änderungen: `Directory.Packages.props` (xUnit-Versionen
  entfernt, `TUnit 1.67.0` als PackageVersion ergänzt) und `tests/DotCov.Tests/DotCov.Tests.csproj`
  (`<PackageReference Include="TUnit" />` neben den noch vorhandenen xUnit-Referenzen). Beide
  gehören zu Checkpoint 1 und werden dort committet.
- Build (`dotnet build DotCov.slnx`, sauberer Stand `23e9cb1`): erfolgreich, 0 Warnungen, 0 Fehler.
- Tests (`dotnet test DotCov.slnx`, VSTest/xUnit): 679 bestanden, 0 fehlgeschlagen, 0 übersprungen.
- Testfälle: 31 Testdateien, 595 `[Fact]`/`[Theory]`-Methoden, 679 entdeckte Fälle (Theory-Expansion).
  Testdaten: `tests/DotCov.Tests/Fixtures/sample.cobertura.xml` und der Emitter-Corpus unter
  `Fixtures/Corpus/**` (cover2cover, coverage.py, gcovr, grcov, ReportGenerator, Cobertura-DTD-Referenz,
  Monorepo-, PathIdentity- und Edge-Fälle); Fixtures werden per `None Update` ins Ausgabeverzeichnis kopiert.
  In-Memory-Builder: `Infrastructure/Cobertura.cs`, fertige Reports: `Infrastructure/Reports.cs`.
- Globale Zustände: Prozess-Umgebungsvariablen (`GITHUB_STEP_SUMMARY` im CLI/Nuke-Adapter,
  `NO_COLOR`/`FORCE_COLOR`/`TERM`/`CI` in `Ansi`) über `EnvScope` in der xUnit-Collection `EnvCollection`
  (serialisiert); `CultureInfo.CurrentCulture` in `FormatterCultureTests`, `GateResultTests`,
  `CrapFormatterTests`; temporäre Verzeichnisse per `Directory.CreateTempSubdirectory` in
  `IDisposable`-Testklassen (Cli*, ParseDirectory, NukeCoverageReportHelpers, FileHasher).
- Bisheriger Coverage-Aufruf (`.github/workflows/nuget-publish.yml`, Job `test`):
  `dotnet test DotCov.slnx -c Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory TestResults`,
  danach `dotnet run --project src/DotCov.Tool -c Release -- report TestResults --format json` für das Badge.
  Filter in `coverlet.runsettings`: Format cobertura, `ExcludeByAttribute` CompilerGenerated/GeneratedCode,
  `ExcludeByFile **/obj/**/*.cs`.

Checkpoint erfüllt.
