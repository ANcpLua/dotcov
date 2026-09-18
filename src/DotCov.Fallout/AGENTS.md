# Fallout commands

Run sequentially from the repository root; Fallout shares a build log per checkout.
Use the `fallout` skill when changing build targets
or the component. Before routine verification, choose the matching target:

```sh
dotnet tool restore
dotnet fallout Test
dotnet fallout Test --filter '/*/*/FalloutBuildTests/*'
dotnet fallout Coverage
dotnet fallout Report --format md
dotnet fallout Crap --top 10
dotnet fallout Snapshot
```

`Diff` requires `--before` naming an existing Cobertura file or directory.
Reporting targets collect fresh coverage unless `--reports` supplies an existing
file or single-run directory. Multiple targets share one collection. `Coverage`
accepts `--min-line` and `--min-branch`; `Crap` accepts `--max-crap` and optional
`--metrics`. Failed gates intentionally return nonzero. Snapshot prints its JSON
artifact path; it does not upload.

The wrapper is [build/Build.cs](../../build/Build.cs); measurement settings live
only in [DotCov.Tests.csproj](../../tests/DotCov.Tests/DotCov.Tests.csproj), switched on
by `-p:Coverage=true`.

## Code Review Rules

- Keep discovery, XML interpretation, and gate policy in DotCov; the Fallout
  adapter binds parameters and presents results. Verify parameter binding,
  dependency ordering, diagnostics, and exit behavior through a real consuming
  build, not only helper tests.
