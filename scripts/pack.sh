#!/usr/bin/env bash
#
# pack.sh — build (and optionally push) the hybrid Native-AOT + CoreCLR-fallback
# dotnet-tool packages for DotCov.Tool.
#
# Native AOT can't cross-OS-compile, so each OS's package is built where it can be:
#   osx-arm64   native AOT on this Mac
#   linux-x64   native AOT inside the official 10.0-noble-aot Linux container (needs Docker)
#   linux-arm64 native AOT inside that container (arm64)
#   any         portable CoreCLR fallback — Windows, Intel Mac, Alpine, anything unbuilt
#   pointer     RID-agnostic package that selects the right sub-package at install time
#
# Usage:
#   scripts/pack.sh [version]      # build packages into ./nupkgs (version optional)
#   NUGET_KEY=xxx scripts/pack.sh 0.2.0   # build, then push in the correct order
#
# Requires: .NET SDK 10+; Docker (optional — without it, Linux users use the `any` fallback).
set -euo pipefail
cd "$(dirname "$0")/.."

PROJ="src/DotCov.Tool/DotCov.Tool.csproj"
OUT="nupkgs"
IMG="mcr.microsoft.com/dotnet/sdk:10.0-noble-aot"
SRC="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"
VERSION="${1:-}"
VARG=(); [ -n "$VERSION" ] && VARG=(-p:Version="$VERSION")

rm -rf "$OUT"; mkdir -p "$OUT"

# Native AOT for the host OS (cross-arch within the same OS is fine).
echo "== pack osx-arm64 (native AOT) =="
dotnet pack "$PROJ" -c Release -r osx-arm64 "${VARG[@]}" -o "$OUT"

# Linux AOT via the container — the OS inside the container matches the target RID's OS,
# so it's a native build, not a cross-OS one. --platform selects the arch.
if command -v docker >/dev/null 2>&1; then
  for spec in "linux-arm64 linux/arm64" "linux-x64 linux/amd64"; do
    set -- $spec
    echo "== pack $1 (native AOT in $IMG, $2) =="
    docker run --rm --platform "$2" -v "$PWD":/src -w /src "$IMG" \
      dotnet pack "$PROJ" -c Release -r "$1" "${VARG[@]}" -o "$OUT"
  done
else
  echo "!! docker not found — skipping Linux AOT packages; Linux users will use the 'any' fallback"
fi

# Portable CoreCLR fallback for every platform without a native build.
echo "== pack any (portable CoreCLR fallback) =="
dotnet pack "$PROJ" -c Release -r any -p:PublishAot=false "${VARG[@]}" -o "$OUT"

# RID-agnostic pointer package (built last; must also be PUSHED last).
echo "== pack pointer package =="
dotnet pack "$PROJ" -c Release "${VARG[@]}" -o "$OUT"

echo; echo "== packages in $OUT/ =="; ls -1 "$OUT"

# A package is the pointer iff the segment after "DotCov.Tool." starts with a digit (the
# version) rather than a RID (osx-/linux-/any). Everything else is a RID/any sub-package.
is_pointer() { case "${1#DotCov.Tool.}" in [0-9]*) return 0;; *) return 1;; esac; }

if [ -n "${NUGET_KEY:-}" ]; then
  echo; echo "== pushing sub-packages first, pointer last (install fails if the pointer lands first) =="
  for p in "$OUT"/*.nupkg; do is_pointer "$(basename "$p")" || dotnet nuget push "$p" -k "$NUGET_KEY" -s "$SRC" --skip-duplicate; done
  for p in "$OUT"/*.nupkg; do is_pointer "$(basename "$p")" && dotnet nuget push "$p" -k "$NUGET_KEY" -s "$SRC" --skip-duplicate; done
else
  echo; echo "(set NUGET_KEY to auto-push — sub-packages first, pointer last)"
fi
