# DotCov.Tool

`dotnet` global tool for Cobertura coverage reporting, diffing, and CI gating.

```bash
dotnet tool install -g DotCov.Tool
```

```bash
dotcov report   TestResults/ --format table|json|md              # parse and render
dotcov check    TestResults/ --min-line 80 --exclude-generated   # CI gate, exit 1 if below
dotcov crap     TestResults/ --max-crap 6                        # CRAP gate: comp^2*(1-cov)^3+comp
dotcov diff     before.xml after.xml --format md                 # compare two reports
dotcov snapshot TestResults/ --commit SHA --branch main --project MyApp
```

`crap` scores every method (worst-first) and exits 1 when any is strictly above `--max-crap`
(at-threshold passes; default 6). Complexity comes from coverlet's per-method attribute, or pass
`--metrics <file>` from `dotnet msbuild /t:Metrics` (Microsoft.CodeAnalysis.Metrics) for other
emitters. Unscored methods and unmatched metrics members are listed, never silently dropped.

Global flags: `--exclude-generated`, `--keep <substrings>`, `--pattern <glob>` (directory-scan filename, `filename` or `**/filename`; default `**/coverage.cobertura.xml` — override for gcovr/coverage.py names like `coverage.xml`), `--max-chars <N>` (per-file XML character cap, default `50000000`; `0` = no cap), `--upload <url>`, `--github-summary`.

Exit codes: `0` success (gate passed) · `1` gate failed or inconclusive (`NODATA`/`DISABLED`), or the command could not run (parse/IO/size-cap error, invalid flag value, upload failure) — the first stderr token (`FAIL:`/`NODATA:`/`DISABLED:`/`error:`) distinguishes these · `2` unknown command.

[Docs](https://github.com/ANcpLua/dotcov#readme) · [Library — DotCov](https://www.nuget.org/packages/DotCov/) · [NUKE — DotCov.Nuke](https://www.nuget.org/packages/DotCov.Nuke/) · [MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
