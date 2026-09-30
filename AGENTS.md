# dotcov

Run tests and coverage through the [Fallout commands](src/DotCov.Fallout/AGENTS.md);
use direct runner commands only for diagnostics the targets don't expose.
For test isolation, measurement or runner diagnostics, read the
[test guide](tests/CLAUDE.md).

## Skills

- Code reviews: load [$behavioral-review](.agents/skills/behavioral-review/SKILL.md).
  Project commands stay in this file, not in the reusable skill.
- Workflow edits: use [$dotcov-ci-workflows](.agents/skills/dotcov-ci-workflows/SKILL.md).
- Release preparation or publication: use [$dotcov-release](.agents/skills/dotcov-release/SKILL.md).

## Code Review Rules

- Flag successful gates without a measured pass. Missing inputs, `NoData`, and
  disabled thresholds are not passes. Exit status and summaries must reflect the
  same evaluated result; combine only reports from the intended measurement run.
- Flag lost source diagnostics or changed stream ownership. Preserve warnings
  through file and method paths; dispose streams opened through `ReportInput`,
  while directly supplied streams remain the caller's responsibility.
- Flag tests that bypass the behavior they claim to protect. Exercise CLI and
  Fallout contracts through their real entrypoints; isolate process-global state
  and reap owned children before workspace cleanup. Async assertions alone do
  not require changing a synchronous action under test.

Leave formatting and lint checks to CI.
