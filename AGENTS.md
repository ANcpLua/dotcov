# dotcov

Before verification, read the [Fallout commands](src/DotCov.Fallout/AGENTS.md).
Use those targets for routine tests and coverage; use direct runner commands for
diagnostics the targets do not expose.

For requested code reviews, load
[$behavioral-review](.agents/skills/behavioral-review/SKILL.md).
Keep project commands here, not in the reusable skill.

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
