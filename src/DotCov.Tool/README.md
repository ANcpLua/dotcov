# DotCov.Tool

`dotnet` global tool for Cobertura coverage reporting, diffing, and CI gating.

```bash
dotnet tool install -g DotCov.Tool
```

```bash
dotcov report   TestResults/ --format table|json|md              # parse and render
dotcov check    TestResults/ --min-line 80 --exclude-generated   # CI gate, exit 1 if below
dotcov diff     before.xml after.xml --format md                 # compare two reports
dotcov snapshot TestResults/ --commit SHA --branch main --project MyApp
```

Global flags: `--exclude-generated`, `--upload <url>`, `--github-summary`.

[Docs](https://github.com/ANcpLua/dotcov#readme) · [Library — DotCov](https://www.nuget.org/packages/DotCov/) · [NUKE — DotCov.Nuke](https://www.nuget.org/packages/DotCov.Nuke/) · [MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
