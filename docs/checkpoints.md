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

## Checkpoint 1: Bestehende Tests auf TUnit umstellen

- Werkzeug: `TUnitMigrator 0.2.0` (`tunit-migrate -t .`) für Attribute, Assertions, Paketverwaltung,
  `OutputType=Exe` und den `global.json`-Eintrag `"test": { "runner": "Microsoft.Testing.Platform" }`.
  Der Migrator wählte TUnit 1.68.4; zentral auf `TUnitVersion=1.67.0` festgelegt. xUnit, xUnit-Runner,
  `Microsoft.NET.Test.Sdk` und `coverlet.collector` aus `Directory.Packages.props` und dem Testprojekt entfernt.
- Nacharbeit von Hand (Build hatte 107 Fehler nach dem Migrator):
  - verschachtelte `Assert.Single(x).Y` → `x.Single().Y`; `Assert.Single(coll, pred)` → `Count(pred) == 1`
    bzw. `Single(pred)`; vom Migrator vertauschte `Contains(lambda)`-Argumente zurückgedreht.
  - `Assert.All(coll, ...)` war zu einer einzelnen Assertion ohne Schleife verflacht → `foreach`.
  - `Assert.Throws<T>`/`ThrowsAsync<T>` sind in xUnit typexakt → `Assert.ThrowsExactly<T>` /
    `ThrowsExactlyAsync<T>`; `ThrowsAnyAsync` → `Throws<T>()` (Untertypen erlaubt).
  - Sequenzvergleiche (`Assert.Equal([..], list)`) → `IsEquivalentTo([..], CollectionOrdering.Matching)`,
    da `IsEqualTo` in TUnit Referenzgleichheit prüft (11 Fehlschläge im ersten Lauf).
  - `Assert.Equal(a, b, precision: n)` → `.IsEqualTo(a).Within(1e-n)` (5 Stellen; 2 Fehlschläge).
  - `Record.Exception` → `ThrowsNothing()`; konstante `MovementEpsilon` über lokale Variable geprüft (TUnitAssertions0005).
  - `IsTypeOf<T>()`/`IsAssignableTo<T>()` liefern `T?` → `(await …)!`.
- Ressourcen: temporäre Verzeichnisse bleiben in `IDisposable`-Testklassen; TUnit entsorgt die Instanz pro
  Test auch bei Fehlschlag. Nachweis: Anzahl `dotcov-*`-Verzeichnisse in `$TMPDIR` vor/nach dem Lauf unverändert (14/14; Altbestand).
- Globale Zustände: `[NotInParallel(ProcessState.Environment)]` auf `AnsiTests` und `CliGitHubSummaryTests`
  (Schreiber und Leser der Prozessumgebung). Kultur pro Test über `Infrastructure/CultureScope.cs`
  (Klon der invarianten Kultur mit Komma-Dezimaltrenner, `try/finally`-Wiederherstellung, kein `await` im Scope);
  die drei bisherigen Helfer in `FormatterCultureTests`, `CrapFormatterTests`, `GateResultTests` ersetzt.
  Keine Assembly-Policies (`Retry`, `ParallelLimiter`) übernommen.
- Ergebnis: `dotnet test DotCov.slnx` (MTP): 679 entdeckt, 679 bestanden, 0 fehlgeschlagen — identisch zur
  Ausgangslage. Zusätzlich unter `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`: 679/679.
- Nicht umgestellt (gehört zu Checkpoint 8): Coverage-Aufruf in CI/README/`coverlet.runsettings`.

Checkpoint erfüllt.

## Checkpoint 2: Eingabeauflösung

- Neu in `src/DotCov`: `ReportInput` (Quellenname + Stream-Fabrik, `FromFile`/`FromBytes`, kein `Read<T>`,
  keine Fehlerformatierung), `ReportPattern` (unveränderlich, `FileName` + `Recursive`, `Parse`/`TryParse`,
  Zerlegung ausschließlich am literalen `**/`-Präfix, kein `Path.GetFileName`), `ReportResolver`
  (`Resolve(path[, pattern])`, `ResolveDirectory`; Datei → Einzeleingabe, Verzeichnis → ordinal sortierte
  Treffer, fehlender Pfad → `FileNotFoundException`/`DirectoryNotFoundException`, leeres Verzeichnis → leere Liste).
- Versteckte Verzeichnisse: `EnumerationOptions.AttributesToSkip = None` (Standard überspringt Hidden|System),
  `IgnoreInaccessible = false` (Zugriffsfehler werden gemeldet statt still übersprungen).
- `CoberturaParser.FindReports` delegiert jetzt an den Resolver; die alten `Parse*Directory/Path`-Einstiege
  bleiben bis Checkpoint 5 als Aufrufer bestehen. Reports ohne Messdaten werden weiterhin erst im Gate als `NoData` bewertet.
- Build: 0 Fehler. Tests: 679/679. Ad-hoc-Nachweis: `dotcov report <tmp>` findet `<tmp>/.hidden/sub/coverage.cobertura.xml`.

Checkpoint erfüllt.

## Checkpoint 3: Parser und Aggregation

- Eine Traversierung (`Visit` → `ConsumeSource`/`ConsumeClass`) speist beide Aggregationen; `XmlReader.Create`
  für Cobertura-Eingaben nur noch in `CreateReader`. Gemeinsame Dekodierung: `DecodeFileName` (Separator,
  Quellwurzel, Laufwerksbuchstabe), `TryDecodeLine` (Zeilennummer, saturierte Hits, `MalformedHits`-Warnung),
  `DecodeComplexity`.
- `FileCollector`/`LineAccumulator` behalten die Dateiregeln (Union mit `Math.Max`, Branch-/Condition-Dedup,
  2-Outcome-Konsistenz). Interner `MethodCollector` mit `Entry` übernimmt ausschließlich Methodenidentität,
  Zusammenführung (`Math.Max` je Zeile und Komplexität) und Ergebnisbildung; er sieht nur dekodierte Daten.
- String-Schlüssel `"{file}\n{class}\n{name}\n{sig}"` durch `internal readonly record struct MethodKey` ersetzt.
- Streaming (`ReadSubtree`, kein DOM), dokumentbezogene Quellwurzeln (`DocumentContext`) und `MaxCharactersInDocument` erhalten.
- Build: 0 Fehler. Tests: 679/679 unverändert.

Checkpoint erfüllt.

## Checkpoint 4: Ergebnisse und Fehler

- `MethodCoverageReport` (`Methods`, `Warnings`, `SourceRoots`); `ParseMethods` liefert ihn für Stream,
  `ReportInput` und `IEnumerable<ReportInput>`. Der `MethodCollector` sammelt Warnungen aller Dokumente
  (`Complete(document)`) und dedupliziert Quellwurzeln nach `PathIdentity.NormalizeRoot` wie `CoverageReport.Merge`.
- `ReportParseException` (`SourceName`, `LineNumber`, `LinePosition`, typisierte `InnerException`; `Message`
  ist die unveränderte Reader-Meldung). Regex `LocationSentencePattern` und die 4-Argument-`XmlException`-Rethrows entfernt.
- Formatierung an der Ausgabegrenze: CLI schreibt `error: {SourceName}: {Message}`; der CLI-CRAP-Befehl gibt
  alle Parserwarnungen als `warning: {file}:{line}: {detail}` auf stderr aus (`WriteWarnings`).
- Stream-Besitz: `Parse(ReportInput)`/`ParseMethods(ReportInput…)` öffnen per `OpenStream()` und schließen per
  `using`, auch bei Fehlern; `Parse(Stream)`/`ParseAsync(Stream)`/`ParseMethods(Stream)` lassen den Stream beim
  Aufrufer (`XmlReaderSettings.CloseInput` bleibt false).
- Neue Einstiege `Parse(ReportInput)`, `Parse(IEnumerable<ReportInput>)`; die alten Pfad-Einstiege delegieren
  bis Checkpoint 5 dorthin. Tests der Fehlerkontrakte (`CoreParserRobustnessTests`, `MethodCoverageParseTests`,
  `NukeCoverageReportHelpersTests`) auf `ReportParseException` umgestellt; `MethodCoverageParseTests` lesen `.Methods`.
- Build: 0 Fehler. Tests: 679/679.

Checkpoint erfüllt.
