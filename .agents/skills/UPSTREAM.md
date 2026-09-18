# Fallout skill adaptations

Reviewed 2026-09-18 against
[Fallout's skill directory at 009fa528763c4dab49ef403b948e3bf0fbacc1e5](https://github.com/Fallout-build/Fallout/tree/009fa528763c4dab49ef403b948e3bf0fbacc1e5/.agents/skills).
These are DotCov-specific adaptations, not a copy synchronized with upstream.

| Upstream skill | DotCov decision |
| --- | --- |
| `editing-ci-workflows` | Adapted as [dotcov-ci-workflows](dotcov-ci-workflows/SKILL.md): workflow ownership, event/permission boundaries, tests before publication. |
| `cutting-a-release` | Adapted as [dotcov-release](dotcov-release/SKILL.md): exact ref, publication intent, package availability rather than a green-run claim. |
| `adding-a-tool-wrapper` | Not imported: DotCov has no Fallout `Tools/*.json` generator or `GenerateTools` target. Its CLI adapter is handwritten. |
| `adding-a-migration-step` | Not imported: DotCov does not implement `Fallout.Migrate`. Consumer migration belongs in the shared Fallout skill. |
| `creating-a-pr` | Not imported: Fallout's `develop`, release branches, and label taxonomy are not DotCov policy. |
| `marking-experimental-apis` | Not imported: DotCov has no corresponding diagnostic-ID registry or experimental-release policy. |
| `plain-english` | Not imported: general writing guidance adds no DotCov-specific workflow and includes upstream PR policies. |

## Sources

- [CI skill](https://github.com/Fallout-build/Fallout/blob/009fa528763c4dab49ef403b948e3bf0fbacc1e5/.agents/skills/editing-ci-workflows/SKILL.md)
  and its [invariants](https://github.com/Fallout-build/Fallout/blob/009fa528763c4dab49ef403b948e3bf0fbacc1e5/.agents/skills/editing-ci-workflows/references/ci-invariants.md).
- [Release skill](https://github.com/Fallout-build/Fallout/blob/009fa528763c4dab49ef403b948e3bf0fbacc1e5/.agents/skills/cutting-a-release/SKILL.md)
  and its [runbook](https://github.com/Fallout-build/Fallout/blob/009fa528763c4dab49ef403b948e3bf0fbacc1e5/docs/branching-and-release.md).

Local instructions were checked against DotCov's workflows, build entrypoint,
package project, and packaging script. Preserve their owning files as the source
of truth when those contracts change. No Fallout framework versioning, branch
protection, or automatic publishing policy was imported.

Upstream is MIT-licensed; its notice is retained in [Fallout.LICENSE](Fallout.LICENSE).
The existing behavioral-review skill is not derived from these upstream files.

## Validation

Both adapted skills passed frontmatter validation and local-link checks.
The root `AGENTS.md` remains under 200 words. This checks packaging and references,
not automatic invocation or agent performance. No CI workflow, release, or .NET
test run was executed for this documentation-only adaptation.
