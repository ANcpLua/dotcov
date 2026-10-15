---
name: dotcov-release
description: >-
  Use to prepare, publish, or verify a DotCov release, including version tags,
  release notes, GitHub Releases, NuGet packages, and native-tool completeness.
  Routine builds and skill installation are not release requests.
---

# DotCov release

1. Use the requested version, source commit/ref, and destination. Read that ref's
   [release workflow](../../../.github/workflows/nuget-publish.yml): the selected
   ref determines the workflow and checked-out source. Check existing tags and
   package versions before choosing a release trigger; never move a release tag
   or treat `--skip-duplicate` as proof that a package came from this commit.
2. Prepare the versioned files together:

   - [Directory.Build.props](../../../Directory.Build.props): `VersionPrefix` and
     the current release's `PackageReleaseNotes` summary.
   - [Badge action](../../../.github/actions/badge/action.yml): the `version` default.
   - [README](../../../README.md): the `badge@v<version>` snippet and release-notes link.
   - `docs/releases/<version>.md`: finished notes for the requested version.

   [ReleaseVersionTests](../../../tests/DotCov.Tests/ReleaseVersionTests.cs) checks
   the version pointers and that the notes file exists; it does not validate the
   notes' content or package summary. For tag releases, missing notes fail the
   GitHub Release job after NuGet publication, so include them before tagging.
3. Use the [Fallout verification targets](../../../src/DotCov.Fallout/AGENTS.md)
   for changed behavior. After `dotnet tool restore`, the focused version check is
   `fallout Test --filter 'FullyQualifiedName~DotCov.Tests.ReleaseVersionTests'`. Tests already compile the
   changed code; do not add a separate build. Keep the full CI release dependencies
   intact: build, Linux/Windows tests, badge-action, consumer-coverage,
   readme-badge-coverage, pack-tool-native, and pack-rest.
4. Derive the expected package set from the workflow and
   [tool project](../../../src/DotCov.Tool/DotCov.Tool.csproj). It currently contains
   seven IDs: `DotCov`, `DotCov.Fallout`, `DotCov.Tool`, and `DotCov.Tool.<rid>` for
   `osx-arm64`, `linux-x64`, `linux-arm64`, and `any`. Native AOT packages are built
   on matching OS runners; `any` is the portable CoreCLR fallback. A host-only
   build cannot verify that set. [scripts/pack.sh](../../../scripts/pack.sh) packs
   only the tool family, clears `nupkgs/`, skips Linux AOT without reachable Docker,
   and publishes when `NUGET_KEY` is set; it is not a complete release check.
5. When publication is requested, use one trigger:

   - Push `v<version>` at the intended commit for NuGet publication followed by a
     GitHub Release. The `release` job runs after `publish`, uses the notes file,
     and leaves an already-existing GitHub Release unchanged.
   - For package-only publication, manually dispatch `nuget-publish.yml` on the
     intended branch with `version` without the `v` prefix. A dispatch does not
     create a tag. Dispatching against a tag also satisfies the `release` job's
     `github.ref_type == 'tag'` condition; ensure the tag and version input agree.

   Preserve the `nuget` environment and trusted-publishing authentication. Push
   all RID/fallback packages and both libraries before the `DotCov.Tool` pointer,
   including when recovering a partial publication.
6. Inspect the actual run's job and step conclusions, then verify every expected
   package ID/version in the destination feed. A green run with skipped publishing
   or duplicate-skipping pushes does not establish new publication. Check package
   availability and registration before installing the exact published version in
   an isolated tool path. Confirm `--version` and exercise a real report or the
   changed CLI behavior through that installed tool; record the tested runtime and
   exit results. Report indexing delays and unverified platforms explicitly.

Finish with the exact commit/version and workflow URL, separating NuGet availability,
GitHub Release status, and installed-tool verification. For preparation-only work,
state that nothing was published. If workflows need changes, also use
[dotcov-ci-workflows](../dotcov-ci-workflows/SKILL.md).

[Adaptation source and license](../UPSTREAM.md).
