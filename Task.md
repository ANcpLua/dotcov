## Task

`CoberturaParser` als Parserkern behalten. Eingabeauflösung vereinheitlichen, Datei- und Methodenaggregation trennen,
Diagnosen vollständig weiterreichen. Dateisystem-Fassade und Kompatibilitäts-Shims entfernen.
Das bestehende Testprojekt vollständig auf TUnit 1.67.0 umstellen und seine Tests an der Zielstruktur ausrichten.
Die bisherige NUKE-Komponente vollständig durch `DotCov.Fallout` ersetzen und ihre Implementierung vereinfachen.
Die bestehende direkte `dotnet`-Pipeline bleibt erhalten; eine eigene Fallout-Build-Orchestrierung ist kein Teil dieses Umbaus.

- Es gibt keine externen Consumer, deren Binärkompatibilität erhalten werden muss.
- Ausgangspunkt ist `main` im vorhandenen Repository. Genannte Zieltypen bei Bedarf neu einführen; vorhandene Implementierungen auf die beschriebenen Zuständigkeiten umbauen.
- Vorhandene fachliche Regressionstests übernehmen; neue Verträge gezielt ergänzen.
- Die lokalen Beispiele dienen als Referenz für TUnit-APIs und Testaufbau. Nur tatsächlich benötigte Abhängigkeiten übernehmen.
- Checkpoints erst nach der angegebenen Prüfung abhaken; nicht ausgeführte Prüfungen ausdrücklich kennzeichnen.

### Checkpoint 0: Ausgangslage

- [x] Ausgangscommit auf `main` und bestehende Änderungen erfassen.
- [x] Build und Tests als Ausgangslage ausführen; vorhandene Fehler festhalten.
- [x] Entdeckte Testfälle, Testdaten, globale Zustände und den bisherigen Coverage-Aufruf erfassen.

Checkpoint erfüllt: Änderungen und Ergebnisse sind einer dokumentierten Ausgangslage zuordenbar.

### Checkpoint 1: Bestehende Tests auf TUnit umstellen

- [x] `tests/DotCov.Tests/DotCov.Tests.csproj` auf TUnit 1.67.0 und `OutputType=Exe` umstellen; `net10.0` beibehalten.
- [x] Zentrale Paketverwaltung anpassen; xUnit, xUnit-Runner, `Microsoft.NET.Test.Sdk` und `coverlet.collector` entfernen.
- [x] Microsoft.Testing.Platform für `dotnet test` in `global.json` konfigurieren.
- [x] `Fact`/`Theory` und `InlineData` auf `Test` und `Arguments` umstellen; komplexe Fälle über typisierte Datenquellen ausdrücken.
- [x] Assertions auf TUnit übertragen und erforderliche Assertions abwarten. Präzision, Reihenfolge, Referenzgleichheit und exakte Exception-Typen prüfen.
- [x] Temporäre Verzeichnisse und Streams pro Test verwalten; Aufräumen auch bei Fehlern sicherstellen.
- [x] Tests mit Prozess-Umgebungsvariablen einschließlich betroffener Leser koordinieren. Kulturänderungen pro Test kapseln und wiederherstellen; Tests unter invariantem Globalisierungsmodus weiterhin ermöglichen.
- [x] Parser- und Vertragstests unabhängig und ohne automatische Retries ausführen; Assembly-Policies aus den Beispielen nicht pauschal übernehmen.
- [x] Bestehende Testfälle unter TUnit entdecken und ausführen; Abweichungen zur Ausgangslage erklären, bevor der Parserumbau beginnt.

Checkpoint erfüllt: Die bestehende Suite läuft über TUnit; Testentdeckung, Assertions und Ressourcenlebensdauer sind geprüft.

### Checkpoint 2: Eingabeauflösung

- [x] `ReportInput` auf Quellenname, Stream-Fabrik und `FromFile`/`FromBytes` begrenzen.
- [x] `Read<T>`, `Read(Action)` und XML-Fehlerformatierung aus `ReportInput` entfernen beziehungsweise dort nicht neu einführen.
- [x] `ReportPattern` auf ein unveränderliches, validiertes Muster mit Dateiname und Rekursionsangabe begrenzen.
- [x] Muster ohne betriebssystemabhängige Pfadzerlegung auswerten.
- [x] Datei-, Verzeichnis- und Musterauflösung in einem gemeinsamen `ReportResolver` bündeln; vorhandene `Find`-/`Locate`-Logik dorthin verlagern.
- [x] `ReportResolver` deterministisch geordnete `ReportInput`-Objekte liefern lassen; Datei- oder Methodenabdeckung erst anschließend im Parser auswählen.
- [x] Bei der Dateisuche auch versteckte Verzeichnisse berücksichtigen.
- [x] Fehlender Pfad oder Zugriffsfehler: Fehler melden.
- [x] Vorhandenes Verzeichnis ohne Treffer: leere Eingabemenge liefern.
- [x] Erfolgreich gelesene Reports ohne Messdaten: im Gate als `NoData` auswerten; der Resolver entscheidet nur über die Eingabemenge.

Checkpoint erfüllt: Datei-, Verzeichnis- und Musterauflösung liegen an einer Stelle.

### Checkpoint 3: Parser und Aggregation

- [x] Dateinamen, Zeilennummern und Hits gemeinsam dekodieren.
- [x] Datei- und Methodenaggregation getrennt halten.
- [x] XML-Traversierung und `XmlReader.Create` im Parser konzentrieren.
- [x] Die Methodenaggregation in einem internen `MethodCollector` bündeln; diesen bei Bedarf aus der bestehenden Methodenverarbeitung herauslösen. Er verarbeitet ausschließlich bereits dekodierte Methoden- und Zeilendaten.
- [x] Dem Sammler nur Methodenidentität, Zusammenführung und Ergebnisbildung zuordnen; seine Existenz im Ausgangsstand ist keine Voraussetzung.
- [x] Zusammengesetzten String-Schlüssel durch ein Tupel oder einen internen `record struct` aus `(Datei, Klasse, Methodenname, Signatur)` ersetzen.
- [x] Streaming, dokumentbezogene Quellwurzeln und Zeichenlimit pro Dokument erhalten.

Checkpoint erfüllt: Gemeinsame XML-Regeln sind einmal implementiert; die Aggregationen behalten ihre jeweiligen Regeln.

### Checkpoint 4: Ergebnisse und Fehler

- [x] `ParseMethods` einen `MethodCoverageReport` mit `Methods`, `Warnings` und `SourceRoots` liefern lassen.
- [x] Methodenwarnungen vollständig sammeln und bis zu den konsumierenden CLI-/Buildadapter-Ausgaben weiterreichen, einschließlich des CLI-CRAP-Befehls.
- [x] Quellenname, XML-Koordinaten und Originalausnahme strukturiert transportieren.
- [x] Fehlertexte an der Ausgabegrenze formatieren.
- [x] Regex-basierte Bearbeitung von Fehlermeldungen entfernen.
- [x] Über `ReportInput` geöffnete Streams im Parser zuverlässig schließen, auch bei Fehlern.
- [x] Direkt übergebene Streams beim Aufrufer belassen.

Checkpoint erfüllt: Ergebnisse und Fehler enthalten ihre Diagnosen; Stream-Besitz ist eindeutig.

### Checkpoint 5: Alte API entfernen und Aufrufer umstellen

- [x] `ParseFile`, `ParseDirectory` und `ParsePath` samt Overloads löschen.
- [x] `ParseMethodsFile`, `ParseMethodsDirectory` und `ParseMethodsPath` samt Overloads löschen.
- [x] CLI und Buildadapter auf denselben `ReportResolver` plus `Parse` beziehungsweise `ParseMethods` umstellen.
- [x] `LoadReport`-Kompatibilitäts-Overload entfernen.
- [x] Erkennung fehlender Reports über `ReferenceEquals(..., CoverageReport.Empty)` durch Prüfung der Eingabemenge ersetzen.
- [x] Fehlerbehandlung und Diagnoseausgabe von CLI und Buildadapter anpassen.
- [x] Betroffene Tests, Hilfsfunktionen, Dokumentation und `cref`-Signaturen aktualisieren.
- [x] Kommentare entfernen, deren einzige Begründung Binärkompatibilität ist.
- [x] Keine Alias- oder Übergangs-Wrapper hinzufügen.

Checkpoint erfüllt: Keine Aufrufstelle oder Dokumentationsreferenz benötigt die entfernte API.

### Checkpoint 5a: Fachliche Korrekturen ausdrücklich umsetzen

- [x] Für noch fehlerhaftes Verhalten zunächst reproduzierende TUnit-Tests ergänzen und fehlschlagen sehen; anschließend die Korrektur nachweisen. Bereits korrektes Verhalten mit denselben Verträgen absichern.
- [x] In `CrapAnalysis.Analyze` Metrics-Zuordnung und Auswahl des Komplexitätswerts trennen: Eingebettete Komplexität hat weiterhin Vorrang, ein passender Metrics-Member gilt trotzdem als zugeordnet.
- [x] `UnmatchedMetricsMembers` nur mit tatsächlich unzugeordneten Methoden-/Accessor-Membern befüllen. „unmatched“ darf nicht „wegen eingebetteter Komplexität nicht benötigt“ bedeuten.
- [x] In `CoverageDiff` beziehungsweise `FileDelta` Negativ-Null beim Entfernen einer 0%-Datei vermeiden: Das numerische Delta ist positive Null. Tabellen- und Markdown-Ausgabe dürfen kein `+-0.0%` erzeugen; den Fehler nicht nur im formatierten Text kaschieren.
- [x] Variante A ausdrücklich abbilden: Eine entfernte gemessene Datei ist unabhängig von ihrer bisherigen Rate eine Regression, auch bei 0 %. Eine hinzugefügte 0%-Datei ist weder Regression noch Verbesserung. Fehlende Messdaten (`null`) nicht mit gemessenen 0 % gleichsetzen.
- [x] `FileDelta` als gemeinsame Quelle für Delta und Klassifikation gestalten: aus Änderungsart und Vorher-/Nachher-Raten ableiten, widersprüchliche separat gesetzte Werte verhindern. `IsRegression`, `IsImprovement` sowie `CoverageDiffResult.Regressions` und `Improvements` müssen dieselben Regeln verwenden.
- [x] Für beidseitig vorhandene Dateien `MovementEpsilon` erhalten: unterhalb der Grenze unverändert und weder Regression noch Verbesserung; ab der Grenze nach Richtung der Änderung klassifizieren. Ohne vergleichbare Messdaten keine Bewegung behaupten.

Checkpoint erfüllt: Metrics-Zuordnung und Diff-Klassifikation erfüllen die genannten Verträge; die gezielten Tests
belegen die Korrekturen zusätzlich zum Parserumbau. Die Dateisuche berücksichtigt weiterhin versteckte Verzeichnisse gemäß Checkpoint 2.

### Checkpoint 6: Tests an der Zielstruktur ausrichten

- [ ] Reine Weiterleitungstests entfernen; Verhalten an den neuen Zuständigkeiten prüfen.
- [ ] `ReportPatternTests`: gültige und ungültige Muster als parametrisierte Fälle prüfen.
- [ ] `ReportResolverTests`: echte temporäre Verzeichnisse einschließlich passender Reports in versteckten Unterverzeichnissen, Reihenfolge, fehlende Pfade und leere Ergebnisse prüfen.
- [ ] Parser-Vertragstests: dieselben relevanten XML-Fälle für Speicher- und Dateiquellen verwenden; gleiche fachliche Ergebnisse erwarten.
- [ ] Datei- und Methodenaggregation gezielt auf ihre jeweiligen Zusammenführungsregeln prüfen.
- [ ] `MethodCoverageReport` auf Methodenidentität, Quellwurzeln und vollständige Diagnosen prüfen.
- [ ] Fehlerkoordinaten, Stream-Lebensdauer und Zeichenlimits an den verantwortlichen Schnittstellen testen.
- [ ] Vorhandene synchrone und asynchrone Parserpfade auf gleiches fachliches Verhalten prüfen.
- [ ] CLI-/Buildadapter-Integration auf Fehlerausgabe, Warnungen und `NoData` prüfen; den CLI-CRAP-Pfad gesondert einbeziehen.
- [ ] `CrapAnalysisTests`: Methode mit eingebetteter Komplexität 3 und passendem Metrics-Wert 7 bleibt mit 3 bewertet und gilt als zugeordnet; ein zusätzlicher Metrics-Member ohne Coverage-Gegenstück bleibt als einziger unmatched-Eintrag sichtbar.
- [ ] `CoverageDiffTests`: Entfernen einer gemessenen 0%-Datei ergibt positive Null und eine Regression; Hinzufügen einer 0%-Datei ergibt keine Verbesserung. Tabellen- und Markdown-Formatierung auf doppelte Vorzeichen prüfen; eine entfernte Datei mit positiver Rate als Kontrollfall verwenden.
- [ ] `FileDelta` tabellarisch auf hinzugefügte, entfernte und beidseitig vorhandene Dateien prüfen: 0 %, positive Raten, fehlende Messdaten, positive/negative Änderungen sowie Werte unter und genau auf `MovementEpsilon`. Einzelklassifikation und Ergebnisfilter müssen übereinstimmen.
- [ ] Den bisherigen Test `Compare_AddedAndRemovedZeroRateFiles_AreNeitherRegressionsNorImprovements` gezielt auf Variante A ändern und passend umbenennen; seinen bisherigen Vertrag nicht unverändert migrieren oder ersatzlos löschen.
- [ ] Bestehende Corpus- und Regressionstestfälle den neuen Tests zuordnen; entfernte oder zusammengeführte Fälle begründen.

Checkpoint erfüllt: Die Suite prüft die Zielverträge; vorhandene fachliche Regressionen sind weiterhin abgedeckt.

### Checkpoint 7: NUKE durch Fallout ersetzen und die Komponente bereinigen

- [ ] `src/DotCov.Nuke` nach `src/DotCov.Fallout` überführen; Projekt, Assembly-/Paketname, Namespace und Metadaten konsistent auf `DotCov.Fallout` umstellen.
- [ ] `Nuke.Common`/`Nuke.Components` und die verwendeten Framework-Typen durch die passenden Fallout-APIs ersetzen, insbesondere `INukeBuild` durch `IFalloutBuild`. Die tatsächlich benötigten Pakete in einer .NET-10-kompatiblen Version zentral festlegen.
- [ ] `Directory.Packages.props`, `DotCov.slnx`, Projektverweise, Tests, Paketierung, Beispiele, README und `cref` aktualisieren. Alte NUKE-Abhängigkeiten und Kompatibilitäts-Overloads vollständig entfernen; keine Alias-Pakete oder Übergangs-Shims behalten.
- [ ] Die bestehende Komponentenbibliothek direkt migrieren. Kein neues Build-Projekt durch `fallout :setup` anlegen und die handgeschriebene CI nicht allein wegen der Adapter-Migration neu generieren.
- [ ] `ICoverageReport` auf die Framework-Anbindung konzentrieren: Parameter entgegennehmen und validieren, gemeinsame Coverage-Verarbeitung aufrufen, Diagnosen ausgeben und das Gate-Ergebnis in Target-Erfolg oder -Fehler übersetzen.
- [ ] Die öffentliche Sammelklasse `CoverageReportHelpers` abbauen: gemeinsame Eingabeauflösung in `ReportResolver`, gemeinsame Fachlogik in den Parser-/Coverage-Kern, reine Framework-Details intern halten. Keine neue öffentliche Sammelklasse oder zusätzliche Weiterleitungsschicht als Ersatz schaffen.
- [ ] Parameter einmal an der Eingabegrenze validieren. Namen, Defaults, invariante Zahleninterpretation, strikte Bool-Werte, Format-Aliase und Zeichenlimit-Vertrag durch Verhaltenstests absichern; typisierte Framework-Parameter nur verwenden, soweit sie diese Verträge erhalten.
- [ ] Fehlenden/unzugänglichen Pfad, vorhandenes Verzeichnis ohne Treffer und gelesene Reports ohne Messdaten ausdrücklich unterscheiden. Keine Objektidentität und keine mehrdeutige `CoverageReport.Empty`-Markierung zur Steuerung verwenden.
- [ ] Die Gate-Policy explizit abbilden und testen: `Pass` erfolgreich; `Fail`, `NoData` und `Disabled` scheitern wie bisher, mit unterscheidbaren Diagnosen. Die offene Policy-Diskussion aus dem Target-Kommentar entfernen.
- [ ] Parserwarnungen vollständig ausgeben. Markdown für Terminalausgabe und GitHub-Step-Summary bei Wiederverwendung nur einmal erzeugen. Ein nicht beschreibbares optionales Summary-Ziel bleibt eine Warnung und verändert ein sonst bestandenes Coverage-Gate nicht.
- [ ] Die lose Abhängigkeit zu `ICompile` erhalten und am tatsächlichen Fallout-Target-Verhalten prüfen, statt sie unbemerkt in eine Pflichtabhängigkeit zu verwandeln.
- [ ] Kommentare und XML-Dokumentation gezielt bereinigen: Historien, Implementierungsnacherzählung, erledigte Entwurfsdiskussionen und Begründungen für kompilierte Alt-Consumer entfernen. Kurze öffentliche Verträge und nicht offensichtliche Gründe erhalten; erforderliche Regeln in Code und Tests ausdrücken.
- [ ] Die bisherigen Helper-Tests auf die verantwortlichen Verträge verteilen und um einen minimalen konsumierenden Fallout-Build ergänzen. Parameterbindung, Target-Abhängigkeiten, fehlende Eingaben, `NoData`, Warnungen, Summary-Fehler und Exit-Verhalten dort tatsächlich ausführen.
- [ ] Den vollständigen transitiven Paketgraphen prüfen. Die beiden bisherigen `NuGetAuditMode=direct`-Ausnahmen samt zugehörigen Kommentaren nach Behebung ihrer Ursache entfernen und den vollständigen Audit erneut ausführen; verbleibende Befunde benennen.

Checkpoint erfüllt: `DotCov.Fallout` ersetzt die NUKE-Anbindung ohne Legacy-Fassade. Gemeinsame Fachlogik liegt
einmal vor, die Komponente enthält nur ihre Integrationsaufgaben, und ein konsumierender Fallout-Build belegt das Verhalten.

### Checkpoint 8: CI, Coverage und Abschlussprüfung

- [ ] Den bisherigen VSTest-/`coverlet.collector`-Pfad durch MTP-kompatible Coverage ersetzen; `coverlet.MTP` zur Fortführung der Coverlet-Messung verwenden.
- [ ] Bestehende Coverage-Filter übertragen; Ausgabeformat, Dateiname und Ausgabeverzeichnis ausdrücklich konfigurieren.
- [ ] `.github/workflows/nuget-publish.yml` und betroffene Test-/Coverage-Anweisungen auf den neuen Aufruf umstellen.
- [ ] Paketierung und Artefaktverweise von `DotCov.Nuke` auf `DotCov.Fallout` umstellen; die bestehenden Trigger, Berechtigungen und Release-Bedingungen erhalten.
- [ ] Nachweisen, dass dotcov den erzeugten Cobertura-Bericht findet, verarbeitet und daraus die Daten für das Coverage-Badge erzeugt.
- [ ] Build und vollständige Tests ausführen.
- [ ] Die gezielten Nachweise für versteckte Verzeichnisse, unmatched Metrics und Negativ-Null/Variante A separat im Abschluss nennen; ein grüner Gesamtlauf ohne diese Fälle genügt nicht.
- [ ] Verfügbare Ergebnisse für die bestehende Linux-/Windows-CI-Matrix prüfen; nicht ausgeführte Plattformprüfungen benennen.
- [ ] Verbleibende Fehler gegenüber der Ausgangslage ausweisen.
- [ ] Abschließend nach ausführbaren NUKE-Abhängigkeiten, veralteten Aufrufstellen und widersprüchlichen Kommentaren suchen; historische Quellenangaben von aktiver Konfiguration unterscheiden.

Checkpoint erfüllt: Build, TUnit-Suite und Coverage-Verarbeitung sind nachgewiesen; offene Prüfungen sind benannt.

## TUnit-Referenzen

| Zweck | Lokales Beispiel |
|---|---|
| Projektaufbau, Basistests und Lebenszyklus | [Projekt](</Users/ancplua/repo-playground/TUnit-1.67.0/TUnit.Mixed 1.67.0/TUnit.Mixed 1.67.0.csproj>), [BasicTests](</Users/ancplua/repo-playground/TUnit-1.67.0/TUnit.Mixed 1.67.0/BasicTests.cs>) |
| Argumente, typisierte Datenquellen und Matrizen | [DataDrivenTests](</Users/ancplua/repo-playground/TUnit-1.67.0/TUnit.Mixed 1.67.0/DataDrivenTests.cs>) |
| Gemeinsame Verhaltensverträge | [QueueContractTests](</Users/ancplua/repo-playground/TUnit-1.67.0/AdvancedPatterns/Contracts/QueueContractTests.cs>) |
| Temporäre Dateisystem-Ressourcen | [SafeFileStoreTests / TempWorkspace](</Users/ancplua/repo-playground/TUnit-1.67.0/AdvancedPatterns/Security/SafeFileStoreTests.cs>) |
| Parallelitätssteuerung | [ParallelismTests](</Users/ancplua/repo-playground/TUnit-1.67.0/TUnit.Patterns 1.67.0/Parallelism/ParallelismTests.cs>) |
| Kultur pro Test | [ScopedCultureExecutor](</Users/ancplua/repo-playground/TUnit-1.67.0/TUnit.Patterns 1.67.0/Extensions/ScopedCultureExecutor.cs>) |
| Wiederkehrende fachliche Assertions | [OrderAssertions](</Users/ancplua/repo-playground/TUnit-1.67.0/TUnit.Patterns 1.67.0/Assertions/OrderAssertions.cs>) |

- [Offizielle xUnit-Migration einschließlich optionalem TUXU0001-Codefixer](https://tunit.dev/docs/migration/xunit/)
- [Coverlet-Anbindung an Microsoft.Testing.Platform](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/Coverlet.MTP.Integration.md)

## Ergänzung: NUKE und Fallout

Die Parser-Zielstruktur und die TUnit-Umstellung bleiben unverändert. Checkpoint 7 setzt die Fallout-Ablösung
der NUKE-Komponente um; eine zusätzliche Umstellung der eigenen Build-Orchestrierung bleibt ein separater Auftrag.

### NUKE-Status (Stand: 17. September 2026)

- DNS: [Issue #1595](https://github.com/nuke-build/nuke/issues/1595) wurde am 18. Mai 2026 eröffnet und ist weiterhin offen. Die dort dokumentierten Abfragen bei `1.1.1.1` und `8.8.8.8` liefern keine Antwort für `nuke.build`. Eigene A-Abfragen am 17. September 2026 lieferten bei beiden Resolvern `NOERROR`, aber keine A-Records. Der Nutzer berichtet außerdem von einer Wiederholung um den 24. Juli 2026.
- Wartung: Matthias Koch begründet seine Inaktivität am 18. November 2025 mit der OSS-Finanzierungssituation und persönlichen Angriffen. Eine Übergabe des Repositorys an einen Nachfolger lehnt er aus Sicherheits- und Reputationsgründen ab; eigenständige Forks sind möglich. Einen kompatiblen Spin-off mit nachhaltigem beziehungsweise kommerziellem Modell stellt er als Möglichkeit dar. [Maintainer-Antwort in Discussion #1564](https://github.com/nuke-build/nuke/discussions/1564#discussioncomment-15001502).
- Einordnung: Aussagen wie „dead“ oder „at risk“ stammen aus der Community-Diskussion, nicht aus einer formalen Einstellungserklärung. RLittlesII antwortete auf die Frage nach einer Kommerzialisierung am 3. Dezember 2025 lediglich mit „Stay tuned …“. [Future of Nuke](https://github.com/nuke-build/nuke/discussions/1564).
- Pakete: Die hier verwendeten Pakete [Nuke.Common](https://www.nuget.org/packages/Nuke.Common) und [Nuke.Components](https://www.nuget.org/packages/Nuke.Components) stehen auf `10.1.0` vom 2. Dezember 2025. `.NET 10` und `.slnx` werden seit `10.0.0` vom 20. November 2025 unterstützt. Fehlender .NET-10-Support ist deshalb kein Migrationsgrund. [NUKE-Changelog](https://github.com/nuke-build/nuke/blob/master/CHANGELOG.md).
- Abhängigkeiten: `src/DotCov.Nuke/DotCov.Nuke.csproj:7` und `tests/DotCov.Tests/DotCov.Tests.csproj:6` begründen `NuGetAuditMode=direct` mit vulnerablen transitiven Abhängigkeiten. Das ist ein konkreter Anlass für eine vollständige Abhängigkeitsprüfung; ein aktueller Audit wurde für diese Task-Ergänzung nicht ausgeführt. Paketalter und Mindestversionsangaben allein belegen keine konkrete Sicherheitslücke.
- Historischer Fehler: [PowerShell-Issue #1508](https://github.com/nuke-build/nuke/issues/1508) ist seit dem 24. Januar 2025 geschlossen. Ein Kommentar vom 16. September 2025 berichtet noch vom Festhalten an v8 wegen einer ausstehenden Veröffentlichung. Das nicht ungeprüft als aktuellen Fehler von `10.1.0` übernehmen.

### Fallout-Referenzen und Einsatz

- Fallout ist ein eigenständiger NUKE-Hard-Fork. Builds bleiben normale C#-Konsolenprogramme mit IDE-Unterstützung und lokalem Debugging; CI-Konfigurationen können aus der Build-Definition erzeugt werden. Die Projektseite nennt Chrison Simtian und Dennis Doomen als Maintainer. [Fallout](https://fallout.build/).
- Fluent Assertions nutzt Fallout bereits im eigenen Build: `Build : FalloutBuild`, `Fallout.Common` und `Fallout.Components`. Als Implementierungsreferenz dienen [Build.cs](https://github.com/fluentassertions/fluentassertions/blob/e5349243870e70b36945af6bf10ab3b64e768a13/Build/Build.cs#L26) und [build.schema.json](https://github.com/fluentassertions/fluentassertions/blob/e5349243870e70b36945af6bf10ab3b64e768a13/.fallout/build.schema.json). Die Beispiele nicht als zusätzliche Test- oder Assertion-Abhängigkeit übernehmen.
- Einstieg für einen neuen Build: `dotnet tool install Fallout.GlobalTool --global`, anschließend `fallout :setup` und `fallout`. Das ist der dokumentierte Quickstart, kein notwendiger Schritt für die Migration einer Komponentenbibliothek. [Quickstart](https://fallout.build/).
- Bestehender NUKE-Build: `Fallout.Migrate` unterstützt `fallout-migrate --dry-run` vor der eigentlichen Umstellung. Der Migrator richtet sich an Build-Orchestratoren; CI-YAML und separate MSBuild-Props benötigen zusätzliche Bearbeitung. [Migrationsanleitung im Repository](https://github.com/Fallout-build/Fallout/blob/develop/docs/Migration/from-nuke.md).
- Build-Parameter und CI-Erzeugung: [Parameters](https://github.com/Fallout-build/Fallout/blob/develop/docs/website/02-fundamentals/06-parameters.md), [GitHub Actions](https://github.com/Fallout-build/Fallout/blob/develop/docs/website/05-cicd/github-actions.md).
- Wiederverwendbare Builds können über `PackAsTool` und `ToolCommandName` als .NET-Tool paketiert werden. Für reproduzierbare Repository-Builds lokale Tool-Manifeste mit festgelegten Versionen bevorzugen. Das `net6.0` aus dem Dokumentationsbeispiel nicht übernehmen; hier bleibt das Ziel `net10.0`. [Global Builds](https://github.com/Fallout-build/Fallout/blob/develop/docs/website/04-sharing/01-global-builds.md).

Für die Umsetzung kann der globale Skill `fallout` verwendet werden. Er wird bei betroffener NUKE-/Fallout-Arbeit
oder vor ihrer Build-Verifikation geladen; sein vollständiger Inhalt gehört nicht in `AGENTS.md` oder `CLAUDE.md`.
