# DotCov

Streaming Cobertura XML parser. Zero dependencies, 50 MB+ safe, Native-AOT clean.

```bash
dotnet add package DotCov
```

```csharp
using DotCov;

var report = CoberturaParser.ParsePath("TestResults/")   // file or directory
                            .Exclude(ExclusionRules.WellKnown);

var gate = report.Evaluate(minLinePercent: 80);
if (!gate.IsPass) { Console.Error.WriteLine(gate); Environment.Exit(1); }
```

[Docs & public API](https://github.com/ANcpLua/dotcov#readme) · [CLI — DotCov.Tool](https://www.nuget.org/packages/DotCov.Tool/) · [NUKE — DotCov.Nuke](https://www.nuget.org/packages/DotCov.Nuke/) · [MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
