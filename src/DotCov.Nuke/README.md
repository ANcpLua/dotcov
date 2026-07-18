# DotCov.Nuke

NUKE build component for Cobertura coverage reporting and CI gating.

```bash
nuke :add-package DotCov.Nuke
```

```csharp
using DotCov.Nuke;

class Build : NukeBuild, ICoverageReport { }
```

```bash
nuke ReportCoverage --coverage-min-line 80 --coverage-exclude-generated true
```

Parameters: `--coverage-min-line`, `--coverage-min-branch`, `--coverage-format`, `--coverage-exclude-generated-param`.

[Docs](https://github.com/ANcpLua/dotcov#readme) · [Library — DotCov](https://www.nuget.org/packages/DotCov/) · [CLI — DotCov.Tool](https://www.nuget.org/packages/DotCov.Tool/) · [MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
