# Emitter corpus

Miniature but shape-faithful Cobertura reports, one directory per real-world
producer. Each sample reproduces the structural quirks of its emitter (DOCTYPE
declarations, `<source>` root conventions, branch/condition encodings, casing,
attribute omissions) so `CorpusTests` can pin the parser's semantics against
the formats actually seen in the wild — not just against our own builder.

| Sample | Producer | Upstream reference |
|--------|----------|--------------------|
| `gcovr/coverage.xml` | gcovr 8.3 (C/C++) | <https://github.com/gcovr/gcovr> — `--cobertura` writer; single-quoted XML decl, `coverage-04.dtd` DOCTYPE, repo-root `<source>`, per-`<condition>` percentage coverage, 64-bit hit counts |
| `coveragepy/coverage.xml` | coverage.py 7.10.4 (Python) | <https://coverage.readthedocs.io/> — `coverage xml`; generator comments citing `coverage-04.dtd`, one `<class>` per module, `missing-branches` attribute, no `<conditions>` children |
| `cover2cover/coverage.xml` | cover2cover (JaCoCo → Cobertura, Java) | <https://github.com/rix0rrr/cover2cover> — relative `<source>src/main/java</source>`, method+class line duplication, `condition-coverage` without `<conditions>` |
| `grcov/coverage.xml` | grcov (Rust) | <https://github.com/mozilla/grcov> — `-t cobertura`; `<source>.</source>` no-op root, count-valued (not percentage) `<condition coverage=>`, `100% (0/0)` zero-branch lines |
| `reportgenerator/Cobertura.xml` | ReportGenerator (merged .NET output) | <https://github.com/danielpalme/ReportGenerator> — `Cobertura.xml` file name, `complexity="NaN"`, per-method line partitioning |
| `reference/cobertura-dtd-example.xml` | original Cobertura (Java/Maven) | <https://cobertura.github.io/cobertura/> — canonical `coverage-04.dtd` shape (<http://cobertura.sourceforge.net/xml/coverage-04.dtd>), Windows drive-letter `<source>` |
| `pathidentity/job-{a,b}/coverage.cobertura.xml` | Coverlet 1.9 (.NET), two path conventions | <https://github.com/coverlet-coverage/coverlet> — the same `Calculator.cs` uploaded under the default convention (`<source>/</source>` + machine-absolute filename) and `DeterministicSourcePaths` (`<source>/_/</source>` + repo-relative filename); merging must warn `FileIdentityAmbiguous` |
| `monorepo/svc-{a,b}/coverage.cobertura.xml` | coverage.py per-service CI uploads | monorepo pattern: two genuinely different `app/main.py` files under different `<source>` roots must stay two rooted entries (10/16 lines = 62.5%), never fuse on the relative name |
| `edge/empty-packages.xml` | gcovr 8.3 | `<packages/>` with zero classes — "nothing measured", not "100%" |
| `edge/gcovr-case-sensitive.xml` | gcovr 8.3 on a Linux tree | real kernel-style pair `xt_TCPMSS.c` / `xt_tcpmss.c` (`linux/net/netfilter`): case-differing names are distinct files under Ordinal keying |
| `edge/gcovr-named-dir/{coverage,cobertura}.xml` | gcovr 8.3 + coverage.py | non-default file names the default `**/coverage.cobertura.xml` pattern must NOT match — the reason `--pattern` exists |

Keep samples byte-stable: tests assert exact line/branch totals computed by
hand from each file's content.
