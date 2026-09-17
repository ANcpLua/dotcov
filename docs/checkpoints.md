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

## Checkpoint 5: Alte API entfernen und Aufrufer umstellen

- Gelöscht: `ParseFile`, `ParseDirectory` (2 Overloads), `ParsePath` (2), `ParseMethodsFile`,
  `ParseMethodsDirectory` (2), `ParseMethodsPath` (2), `CoverageReportHelpers.LoadReport` (2).
- CLI: `ResolveInputs` (ein `ReportResolver.Resolve`-Aufruf für alle Befehle, fehlender Pfad → `CliError`),
  `--pattern` wird an der Optionsgrenze als `ReportPattern` validiert (`error: Unsupported pattern …`).
- Nuke-Target: `ReportResolver.ResolveDirectory` + `CoberturaParser.Parse(inputs)`; „keine Reports“ über
  `inputs.Count > 0` statt `ReferenceEquals(report, CoverageReport.Empty)`. `CoverageReportHelpers.ParsePattern`
  ersetzt die Pattern-Übersetzung von `LoadReport`.
- READMEs (Wurzel, `src/DotCov`) und `cref` in `CodeMetrics.cs` aktualisiert; Kommentare mit reiner
  Binärkompatibilitätsbegründung entfernt (Overload-statt-Default-Parameter, „compiled consumers“, Regex-Rethrow).
- Tests: Aufrufstellen mechanisch auf `Parse(ReportInput.FromFile(…))`, `Parse(ReportResolver.Resolve(…))`,
  `Parse(ReportResolver.ResolveDirectory(…, ReportPattern.Parse(…)))` (analog `ParseMethods`) umgestellt.
  Die 10 `LoadReport_*`-Tests entfernt (Vertrag existiert nicht mehr; Resolver-Verhalten wird in Checkpoint 6 als
  `ReportResolverTests` abgedeckt). `ParseDirectoryTests` bleiben bis Checkpoint 6 als Weiterleitungstests bestehen.
- Build: 0 Fehler. Tests: 669/669 (679 − 10 entfernte).

Checkpoint erfüllt.

## Checkpoint 5a: Fachliche Korrekturen

- Reihenfolge test-first: vier neue Tests zuerst gegen den unveränderten Code ausgeführt — Lauf mit
  4 Fehlschlägen (`EmbeddedComplexity_WinsOverMetrics_ButTheMatchingMemberStillCountsAsMatched`: 2 unmatched statt 1;
  `Compare_RemovedZeroRateFile_IsARegression…`: `IsNegative(-0.0)`; `FormatDiff_RemovedZeroRateFile_NeverRendersADoubleSign`:
  Tabelle enthielt `+-0.0%`; Klassifikationstabelle „removed 0%“: Negativ-Null). Danach Korrektur, Lauf 688/688.
- `CrapAnalysis.ResolveComplexity` in `MatchMetrics` (Zuordnung, markiert konsultierte Member als matched) und die
  Wertauswahl getrennt; eingebettete Komplexität hat weiterhin Vorrang. `UnmatchedMetricsMembers` enthält nur noch
  Methoden-/Accessor-Member ohne Coverage-Gegenstück.
- `FileDelta` neu: privater Konstruktor, Fabriken `Removed`/`Added`/`Compared`; `Delta`, `Change` (Unchanged/Modified über
  `MovementEpsilon`), `IsRegression`, `IsImprovement` werden aus Änderungsart und Raten abgeleitet. Removed-Delta ist
  `0.0 - Before` (positive Null bei 0 %). `CoverageDiffResult.Regressions`/`Improvements` filtern über dieselben Flags.
- Variante A: entfernte gemessene Datei = Regression (auch 0 %); hinzugefügte 0%-Datei weder Regression noch Verbesserung;
  `null`-Raten erzeugen keine Bewegung und kein Delta. Beidseitig: unter `MovementEpsilon` unverändert, ab der Grenze nach
  Richtung (Tabellentest mit 17 Fällen inkl. exakt ±Epsilon über 0/10000 → 1/10000).
- Bisheriger Test `Compare_AddedAndRemovedZeroRateFiles_AreNeitherRegressionsNorImprovements` auf Variante A geändert und
  umbenannt in `Compare_RemovedZeroRateFile_IsARegression_AddedZeroRateFile_IsNotAnImprovement`.
- Build: 0 Fehler. Tests: 688/688. Versteckte Verzeichnisse weiterhin über `ReportResolver` (Checkpoint 2).

Checkpoint erfüllt.

## Checkpoint 6: Tests an der Zielstruktur ausrichten

- Neu: `ReportPatternTests` (7 gültige, 9 ungültige Muster parametrisiert; Default, Gleichheit, null),
  `ReportResolverTests` (echte Temp-Verzeichnisse über `Infrastructure/TempWorkspace`: Datei, fehlender Pfad,
  fehlendes Verzeichnis, leere Treffermenge, Reports in `.hidden/` und `.artifacts/.cache/…`, ordinale Reihenfolge,
  nicht-rekursives Muster, `**name` ohne Rekursion, Fremd-Dateinamen, `ReportInput`-Streams),
  `ParserContractTests` (5 XML-Fälle × Stream sync/async, `FromBytes`, `FromFile`, Resolver → gleiche JSON-Projektion
  bzw. gleiche Methodenprojektion; Warnungsparität Datei-/Methodenpfad; Merge über mehrere Eingaben; leere Eingabemenge;
  Stream-Besitz: Aufruferstreams bleiben offen, Eingabestreams sind nach Erfolg und Fehler geschlossen (exklusives
  Öffnen); Fehlerkoordinaten in `ReportParseException`; Zeichenlimit pro Dokument, über Resolver, 0 = unbegrenzt;
  sync/async-Fehlerparität).
- `MethodCoverageParseTests` ergänzt: Methodenidentität `(Datei, Klasse, Name, Signatur)`, Quellwurzelregeln, vollständige
  Warnungen über mehrere Eingaben. `Cobertura`-Builder um `WithSource` erweitert.
- `CliCrapTests` ergänzt: Parserwarnungen auf stderr ohne Verdiktänderung, leeres Verzeichnis → `NODATA:` Exit 1,
  fehlerhafter Report → `error: {file}: …` genau einmal. Buildadapter-Integration folgt in Checkpoint 7.
- `CrapAnalysisTests`, `CoverageDiffTests` (positive Null, Regression, Formatierung mit Kontrollfall, `FileDelta`-Tabelle,
  umbenannter Variante-A-Test) wurden bereits in Checkpoint 5a angelegt und bleiben unverändert.
- Entfernte reine Weiterleitungstests: `ParseDirectoryTests` (8; Verzeichnis-/Musterfälle → `ReportResolverTests`,
  Merge-Fälle → `ParserContractTests.Parse_MultipleInputs_MergesInInputOrder`), `CoberturaParserTests.ParsePath_*` (2),
  `MethodCoverageParseTests.ParseMethodsPath_*`/`ParseMethodsDirectory_UnsupportedPattern_Throws` (3 → Resolver/Pattern),
  `CoreParserRobustnessTests.ParseDirectory_PatternWithDirectoryComponent_Throws` (6 Fälle → `ReportPatternTests`),
  `…_MaxCharsOverload_*` (2 → `ParserContractTests.MaxChars_*`), `…_SupportedShapes_StillWork` (→ `ReportResolverTests`).
- Corpus-Zuordnung: alle 13 `CorpusTests` und die `MethodCoverageParseTests` auf Corpus-Dateien laufen unverändert über
  `ReportResolver.Resolve`/`ReportInput.FromFile`; `MutationKills3/4`, `CoreMutationPinTests`, `CoreApiHardeningTests`,
  `CorePathIdentityTests`, `MergeConditionIdentityTests` unverändert (Stream-basiert). Keine fachliche Regression ersatzlos entfernt.
- Build: 0 Fehler. Tests: 725/725.

Checkpoint erfüllt.

## Checkpoint 7: NUKE durch Fallout ersetzen

- `src/DotCov.Nuke` → `src/DotCov.Fallout` (git mv): Projekt, Assembly-/Paketname, `RootNamespace DotCov.Fallout`,
  Beschreibung/Tags, README. `DotCov.slnx`, `Directory.Packages.props` (`FalloutVersion=10.4.0`: `Fallout.Common`,
  `Fallout.Components`; `Nuke.*` entfernt), Testprojektverweise, Wurzel-README und Familien-Links aktualisiert.
- API-Abgleich per Reflexion gegen die installierten Pakete: `Fallout.Common.IFalloutBuild`, `FalloutBuild`,
  `ParameterAttribute`, `ParameterPrefixAttribute`, `Target`, `ITargetDefinition.TryDependsOn<T>`, `Assert`,
  `Fallout.Common.IO.AbsolutePath`, `Fallout.Components.ICompile` (: IRestore, IHasSolution, IHasConfiguration).
  Kein `fallout :setup`, kein Migrator (Komponentenbibliothek direkt migriert); handgeschriebene CI unverändert.
- `ICoverageReport : IFalloutBuild`: Parameter bleiben Strings mit strikter Grammatik und werden einmal über das interne
  `CoverageParameters.Parse` validiert (invariante Zahlen, Ziffern-only-Cap, striktes Bool, `md`-Alias, `ReportPattern`).
  Ablauf: Verzeichnis prüfen → `ReportResolver.ResolveDirectory` → `inputs.Count > 0` → `CoberturaParser.Parse` →
  optional `Exclude` → alle Warnungen per `Log.Warning` → `Evaluate` → Markdown einmal (`Lazy`) für Terminal und Step-Summary →
  explizite Gate-Policy (`Pass` Erfolg; `Fail`/`NoData`/`Disabled` scheitern mit unterscheidbaren Meldungen).
  `ReportParseException` wird an der Target-Grenze mit Quelle und Koordinaten gerendert. Der Policy-Kommentar ist entfernt.
- `CoverageReportHelpers` (öffentlich) gelöscht: Eingabeauflösung liegt im `ReportResolver`, Parameterparsing im internen
  `CoverageParameters`, Step-Summary im internen `GitHubStepSummary` (`InternalsVisibleTo DotCov.Tests`). Keine neue
  öffentliche Sammelklasse, keine Weiterleitungsschicht.
- Konsumierender Fallout-Build `tests/DotCov.Fallout.TestBuild` (`Build : FalloutBuild, ICoverageReport`; `CompileBuild`
  zusätzlich `ICompile` mit eigenem `Compile`-Target). `FalloutBuildTests` starten ihn als Prozess (`dotnet exec` mit
  deps/runtimeconfig des Testprojekts, `--root <tmp>`): Pass/Fail/NoData/Disabled, fehlendes Verzeichnis, Verzeichnis ohne
  Treffer, versteckte Verzeichnisse + Fremdmuster, fehlerhafter Report, 6 ungültige Parameterwerte, Cap, `md`-Alias,
  `ExcludeGenerated` true/false, vollständige Warnungen, Step-Summary bei Pass und Fail, nicht beschreibbares Summary-Ziel
  (Warnung, Pass bleibt), ohne `ICompile` läuft `ReportCoverage` allein, mit `ICompile` läuft `Compile` davor
  (`.fallout/parameters.json` + Header-only `.sln`, da `IHasSolution.Solution` `[Required]` ist).
  Beobachtung: Fallouts Argumentgrammatik verschluckt einen Wert mit führendem `-` (`--coverage-max-chars-param -1`), der
  Wert kommt nicht in der Komponente an; die Ziffern-only-Ablehnung von `-1` ist im Unit-Test `CoverageParametersTests` gepinnt.
- Bisherige Helper-Tests verteilt: `CoverageParametersTests` (Grammatik, Defaults, Pattern, Attribution bei negativem Cap),
  `GitHubStepSummaryTests`, Verhalten im Build in `FalloutBuildTests`.
- Audit: beide `NuGetAuditMode=direct`-Ausnahmen samt Kommentaren entfernt (Ursache Nuke.Common 10.1.0 → NuGet.Packaging 6.12.1,
  System.Security.Cryptography.Xml 9.0.0; Fallout.Common 10.4.0 zieht NuGet.Packaging 6.14.3 und
  System.Security.Cryptography.Xml 10.0.10). `dotnet restore --force -p:NuGetAuditMode=all -p:NuGetAuditLevel=low`: keine
  NU19xx-Befunde; `dotnet list package --vulnerable --include-transitive` und `--deprecated`: keine Befunde in allen 5 Projekten.
- Build: 0 Fehler. Tests: 752/752 (davon 19 Prozessläufe des konsumierenden Builds).

Checkpoint erfüllt.

## Checkpoint 8: CI, Coverage und Abschlussprüfung

- Coverage: `coverlet.MTP 10.0.1` im Testprojekt; Aufruf in `.github/workflows/nuget-publish.yml` (Job `test`):
  `dotnet test DotCov.slnx -c Release --results-directory TestResults --coverlet --coverlet-output-format cobertura
  --coverlet-file-prefix dotcov --coverlet-include "[DotCov]*" "[DotCov.Tool]*" "[DotCov.Fallout]*"
  --coverlet-exclude-by-attribute CompilerGeneratedAttribute/GeneratedCodeAttribute --coverlet-exclude-by-file "**/obj/**/*.cs"`.
  Filter aus `coverlet.runsettings` übertragen; die Datei ist gelöscht. Ausgabe: `TestResults/dotcov.coverage.cobertura.<ts>.xml`
  (coverlet.MTP hängt immer einen Zeitstempel an). Ohne Include-Filter wurden Fallouts eigene Assemblies mitgemessen
  (1067 Dateien, 14.7 %); mit Filter 27 Dateien.
- Badge-Schritt: `dotcov report TestResults --pattern "**/dotcov.coverage.cobertura.*.xml" --format json`. Nachweis lokal:
  lineRate 99.08, branchRate 94.41, 0 Warnungen → Badge `99.1%` (brightgreen) über die unveränderte awk-Logik.
  Trigger, Berechtigungen, Release-Bedingungen unverändert; `pack-rest` packt `src/DotCov.Fallout`.
- READMEs (Wurzel, Tool): Testaufruf und `--pattern` für den zeitgestempelten Dateinamen. `CHANGELOG.md`: Eintrag Task 19.
- Build: 0 Warnungen, 0 Fehler. Tests: 752/752 (Debug und Release, letzterer mit Coverage-Instrumentierung).
- Gezielte Nachweise (Filterläufe, alle grün): versteckte Verzeichnisse — `ReportResolverTests.Resolve_RecursivePattern_FindsReportsInHiddenDirectories`
  und `FalloutBuildTests.Pattern_FindsNonCoverletNamesAndHiddenDirectories`; unmatched Metrics —
  `CrapAnalysisTests.EmbeddedComplexity_WinsOverMetrics_ButTheMatchingMemberStillCountsAsMatched` und
  `MetricsMemberMatchingNothing_ListedAsUnmatched`; Negativ-Null/Variante A —
  `CoverageDiffTests.Compare_RemovedZeroRateFile_IsARegression_AddedZeroRateFile_IsNotAnImprovement`,
  `FormatDiff_RemovedZeroRateFile_NeverRendersADoubleSign`, `FileDelta_ClassificationTable_…` (17 Fälle).
- Plattformen: alle Läufe auf macOS (arm64). Nicht ausgeführt: die Linux- und Windows-Jobs der CI-Matrix. Windows-relevante
  Stellen: Hidden-Attribut wird im Resolver-Test explizit gesetzt; `FileShare.None`-Nachweis; Prozessstart über `dotnet` im PATH.
- Verbleibende Fehler gegenüber der Ausgangslage: keine (Ausgang 679/679, Ende 752/752, Build 0/0).
- Abschlusssuche: keine `Nuke.*`-Pakete, keine `ParseFile/ParseDirectory/ParsePath/LoadReport`-Aufrufstellen, keine
  xUnit-/VSTest-Reste in aktiver Konfiguration. Historische Nennungen von `--collect:"XPlat Code Coverage"` und `DotCov.Nuke`
  verbleiben ausschließlich in älteren `CHANGELOG.md`-Einträgen und in `Task.md` (Aufgabenbeschreibung).

Checkpoint erfüllt.
