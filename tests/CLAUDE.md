# Tests, Coverage und Fallout in diesem Repo

Runner ist **Microsoft.Testing.Platform** (`global.json` → `"test.runner"`), Framework TUnit 1.67.
SDK 10: MTP-Optionen gehen direkt an `dotnet test`; ein zusätzlicher `--`-Trenner ist
hier nicht erforderlich. Die ältere VSTest-Integration hat andere Aufrufregeln.

## Befehle

Für normale Verifikation die [Fallout-Targets](../src/DotCov.Fallout/AGENTS.md) verwenden.
Direkte Runner-Aufrufe bleiben für Discovery und gezielte Diagnosen verfügbar:

```bash
dotnet test --project tests/DotCov.Tests                                   # Suite
dotnet test --project tests/DotCov.Tests --treenode-filter "/*/*/ReportPatternTests/*"
dotnet test --project tests/DotCov.Tests --treenode-filter "/*/*/*/*[Category=Integration]"
dotnet test --project tests/DotCov.Tests --list-tests                      # Discovery-Gate
```

Treenode-Pfad: `/<Assembly>/<Namespace>/<Klasse>/<Test>`; Namespace ist `DotCov.Tests`.

Neue Tests zuerst mit `--list-tests` prüfen. Für TUnit `--treenode-filter` verwenden;
das gleichnamig wirkende `--filter` am Fallout-Wrapper wird dorthin übersetzt.

MTP-Exitcodes: **5** ungültige Argumente, **8** keine Tests, **9** Mindestanzahl nicht
erreicht, **13** Abbruch am Fehlerlimit. Bei Fallout zusätzlich den Kindprozess-Exitcode
im Log lesen; der Wrapper muss nicht denselben Zahlenwert zurückgeben.
Siehe [MTP-Diagnose](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-troubleshooting).

`--info` zeigt Version und registrierte Provider, `--help` deren Optionen. Für tiefere
Diagnose: `--diagnostic --diagnostic-verbosity Trace --diagnostic-output-directory <dir>`;
bei Buildproblemen `-bl`. `TestingPlatformCaptureOutput` und
`TestingPlatformShowTestsFailure` gehören zur alten VSTest-Integration und werden im
[nativen MTP-Modus](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test#mtp-mode-of-dotnet-test)
nicht verwendet.

## Coverage

Coverage ist **coverlet.MTP** (`--coverlet`), eingeschaltet mit `-p:Coverage=true`. Die
Coverlet-Argumente stehen nur in `tests/DotCov.Tests/DotCov.Tests.csproj`
(`TestingPlatformCommandLineArguments`); Fallout-Wrapper und CI setzen nur die Eigenschaft.
Ohne Aktivierung bleiben normale Testläufe uninstrumentiert.

```bash
dotnet tool restore
dotnet fallout Coverage                                         # Wrapper
dotnet test --project tests/DotCov.Tests -p:Coverage=true         # direkt
```

- Der Wrapper schreibt jeden Lauf in `TestResults/<run-id>` und liest nur dieses Verzeichnis.
  Bei `--reports` ein konkretes Laufverzeichnis wählen, nicht den gesamten alten Ergebnisbaum.
- Die Include-/Exclude-Filter gehören zum Messvertrag: gleiche Filter für lokale und
  CI-Läufe. Für DotCov-Zahlen `-p:Coverage=true` verwenden, nicht zusätzlich den ebenfalls
  registrierten Microsoft-Provider `--coverage` aktivieren.
- Das Default-Pattern `**/*cobertura*.xml` findet klassische und zeitgestempelte Cobertura-
  Reports. Generische Namen wie `coverage.xml` brauchen weiterhin ein explizites Pattern.

## Fallout

`build/Build.cs` bündelt Tests und DotCov-Aufrufe als lokale Fallout-Targets. CI nutzt
weiterhin direktes `dotnet` mit demselben `-p:Coverage=true`. Davon getrennt bleiben:

- `src/DotCov.Fallout`: die ausgelieferte Komponente `ICoverageReport` (Target
  `ReportCoverage`, Parameter-Präfix `Coverage`, sucht in `RootDirectory/TestResults`).
- `tests/DotCov.Fallout.TestBuild`: ein minimaler Consumer, den `FalloutBuildTests` als echten
  Prozess startet. Modus per `DOTCOV_TESTBUILD` (`compile`, `wait`, sonst plain).

Das Komponentenverhalten wird über diesen TestBuild geprüft. `RepositoryBuildTests` prüft
den Repository-Wrapper ebenfalls als echten Prozess: Parameter, Eingaben und Exit-Verhalten.

## TUnit-Regeln für diese Suite

- Tests, die Prozess-Umgebungsvariablen oder Kultur ändern, tragen
  `[NotInParallel(ProcessState.Environment)]` und nutzen `EnvScope` / `CultureScope`
  (`Infrastructure/`). Betroffene Leser müssen dieselbe Isolation beachten.
- Assertions müssen ausgeführt und ihre Ergebnisse beobachtet werden, gewöhnlich mit
  `await`. Fehlendes `await` auf derselben Zeile allein beweist keinen unwirksamen Test.
  Exception-Assertions prüfen den zugesagten Fehlertyp; `Throws<Exception>` allein schützt
  keinen spezifischen Fehlervertrag.
- Geteilte Fixtures nur für bewusst gemeinsam genutzten, parallel sicheren Zustand.
  Für einfache zustandslose Objekte keine künstliche asynchrone Initialisierung einführen.
- Temp-Verzeichnisse über `TempWorkspace`, Kindprozesse über `ChildProcess`.

## Isolation messen

Gleichen Build, Testumfang und dieselben Einstellungen vergleichen. Retries auf allen
Ebenen ausschalten; weder `--fail-fast`, Fehlerlimits noch unterdrückte Exitcodes verwenden.
Wiederholungen wie `[Repeat(n)]` sind zusätzliche Beobachtungen, keine Heilung eines Fehlers.
Normale Tests behalten ihre Parallelität; Messläufe setzen ein explizites Limit.

Nach einem Release-Build die Platzhalter für Klasse und Test ersetzen. Jeder Vergleich
erhält ein frisches Ergebnisverzeichnis; Coverage bleibt für diese Zeit-/Isolationsprobe aus:

```bash
measurement_dir=$(mktemp -d "${TMPDIR:-/tmp}/dotcov-isolation.XXXXXX")
dotnet test --project tests/DotCov.Tests -c Release --no-build -p:Coverage=false \
  --treenode-filter "/*/*/<Klasse>/<Test>" --maximum-parallel-tests 1 \
  --minimum-expected-tests 1 --results-directory "$measurement_dir/solo" --report-trx
dotnet test --project tests/DotCov.Tests -c Release --no-build -p:Coverage=false \
  --maximum-parallel-tests 1 --minimum-expected-tests 1 \
  --results-directory "$measurement_dir/seq" --report-trx
dotnet test --project tests/DotCov.Tests -c Release --no-build -p:Coverage=false \
  --maximum-parallel-tests 4 --minimum-expected-tests 1 \
  --results-directory "$measurement_dir/par" --report-trx
```

Solo↔sequenziell liefert Hinweise auf Reihenfolge-/Umgebungsabhängigkeit;
sequenziell↔parallel auf Nebenläufigkeit oder Ressourcenkonkurrenz. Ein Unterschied allein
beweist keinen geteilten Zustand. Zeitzone und Kultur separat variieren, nicht gleichzeitig
mit der Parallelität. TRX-Dauer ist keine isolierte Setup-Zeit; dafür eigene Messgrenzen setzen.

Für maschinenlesbare Discovery unterstützt der aktuelle Runner `--list-tests json`;
ermittelte UIDs nur für den zugehörigen Build verwenden. `--filter-uid` und
`--treenode-filter` nicht kombinieren. Optionen zuerst mit `--help` prüfen.
