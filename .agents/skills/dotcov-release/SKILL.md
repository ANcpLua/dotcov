---
name: dotcov-release
description: >-
  Use to prepare, publish, or verify a DotCov release, including version tags,
  manual release dispatches, NuGet packages, and native-tool package completeness.
  Routine builds and skill installation are not release requests.
---

# DotCov release

1. Read the current [release workflow](../../../.github/workflows/nuget-publish.yml)
   and confirm the requested version, source commit/ref, and destination. Keep
   preparation separate from publication. This repository publishes to NuGet
   on `v*` tag pushes **and** manual dispatch with a version; upstream Fallout's
   separate opt-in publishing policy does not apply.
2. Inspect the selected ref's workflow before dispatching: it determines both
   the executed workflow and the checked-out source. Confirm existing tags and
   package versions; never move a release tag or treat `--skip-duplicate` as
   proof that an existing package came from this commit.
3. Use the [repository verification targets](../../../src/DotCov.Fallout/AGENTS.md)
   and inspect the tool project's
   [runtime package set](../../../src/DotCov.Tool/DotCov.Tool.csproj).
   The release needs native RID packages, the portable `any` fallback, the tool
   pointer, and both libraries. A successful host-only build does not verify
   that complete set. Prefer the existing CI release path; `scripts/pack.sh`
   clears its output and can publish when `NUGET_KEY` is present, so it is not
   an unconditional local smoke test.
4. When publication is requested, use one agreed release trigger and retain
   the workflow's build/test/package dependencies and environment gate. Publish
   sub-packages before the pointer; preserve that order when recovering a
   partial release.
5. Verify the actual run's job and step conclusions, then check the destination
   NuGet feed for every expected package ID/version. A green run with skipped
   publishing, or a duplicate-skipping push, does not prove a new publication.
   Smoke-test tool installation from the intended feed in an isolated tool path
   when feasible; report unverified platforms or indexing delays separately.

Finish with the exact commit/version, run URL, and observed package availability.
For preparation-only work, state that nothing was published. If workflow changes
are needed, also use [dotcov-ci-workflows](../dotcov-ci-workflows/SKILL.md).

[Adaptation source and license](../UPSTREAM.md).
