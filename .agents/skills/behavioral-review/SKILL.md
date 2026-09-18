---
name: behavioral-review
description: >-
  Review a diff, branch, pull request, or named code scope for consequential
  behavior defects and tests that fail to protect the claimed contract. Use when
  the user requests a code review or applicable project instructions route a
  review here. Ordinary implementation and formatting requests do not trigger
  this workflow.
---

# Behavioral review

## 1. Establish the review boundary

Read applicable project instructions, including `Code Review Rules`. Get local
commands and domain contracts there; this skill defines the review process.

Verify the checkout, branch, HEAD, and working-tree state. For a change review,
resolve the requested base and head to fixed commits and state whether staged,
unstaged, and untracked files are included. For a named-code audit, state the
paths being audited instead. If the intended scope or baseline is ambiguous,
ask one short question.

Done when the review scope and its evidence sources are explicit.

## 2. Trace observable behavior

Read the changed code and its callers through to the public result or side
effect. Compare it with the requested behavior and applicable project rules.
Check the failure paths and ownership boundaries affected by the change:
inputs, diagnostics, resources, cancellation, shared state, and output status.
Treat compatibility as a finding only when a supported contract establishes it.

Done when each candidate issue has a concrete scenario, expected behavior,
actual behavior, and consequence—not merely a preferred implementation.

## 3. Check what the tests establish

Trace setup, action, and assertions. Does the test exercise the claimed entrypoint
and distinguish the defect from correct behavior? Check test doubles by their
actual role and whether they bypass the behavior under test. Interaction checks
are useful when the interaction is itself a contract. Report naming or setup
structure only when it conceals a real coverage gap or causes fragility.

Run relevant, safe checks using project-local commands. Inspect build targets
before invoking them. Keep review work non-destructive: no source edits,
intentional breakage, commits, pushes, or external comments unless requested.
When reproduction would require an edit, describe the case or request approval.

Done when each finding has code or runtime evidence; mark unexecuted checks
explicitly. Test counts and execution coverage do not prove assertion strength.

## 4. Report actionable findings

Lead with findings ordered by consequence. Each needs a file and narrow line
location, triggering scenario, expected versus actual behavior, impact, and a
safe correction direction. Distinguish introduced regressions from pre-existing
issues; label unresolved suspicions as questions, not findings. Avoid findings
that depend on an unstated requirement. Leave formatting and lint to CI.

Finish with the reviewed scope, commands and results, and material limitations.
If no actionable issues remain, say so without implying proof of correctness.
An empty findings list is a valid outcome; do not fill it with style advice.
