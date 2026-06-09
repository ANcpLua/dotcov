#!/usr/bin/env bash
#
# verify-aot.sh — proves DotCov.Tool is Native-AOT clean, by building and running it.
#
# Not words, facts: this publishes a *real* Native AOT binary, asserts the AOT/trim
# analyzers emitted zero warnings, then runs every command in every format against
# fixtures and asserts no command throws. Exit 0 == AOT-proven; non-zero == a real break.
#
# Usage:  scripts/verify-aot.sh [RID]
#   RID defaults to the host runtime identifier (e.g. osx-arm64, linux-x64).
#   Native AOT cannot cross-compile OSes, so build each OS's package on that OS.
#
# Requires: .NET SDK 10+, and the native toolchain (clang/objcopy) for the target RID.
set -euo pipefail

cd "$(dirname "$0")/.."
PROJECT="src/DotCov.Tool/DotCov.Tool.csproj"
RID="${1:-$(dotnet --info | awk -F'[: ]+' '/^[[:space:]]*RID:/{print $3; exit}')}"
OUT="artifacts/aot-verify/$RID"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

pass=0; fail=0
ok()  { printf '  \033[32mPASS\033[0m %s\n' "$1"; pass=$((pass+1)); }
bad() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; fail=$((fail+1)); }

echo "== Native AOT publish ($RID) =="
LOG="$WORK/publish.log"
# PublishAot here (not in the csproj) keeps the default `dotnet pack` a portable tool;
# this script is the opt-in native path. Warnings stay warnings so we can assert on them.
dotnet publish "$PROJECT" -c Release -r "$RID" \
  -p:PublishAot=true -p:TreatWarningsAsErrors=false >"$LOG" 2>&1 || { cat "$LOG"; exit 1; }

if grep -Eq 'IL(2026|3050|3056|2104|3053)|AOT analysis warning|Trim analysis warning' "$LOG"; then
  bad "AOT/trim analyzer emitted warnings:"; grep -E 'IL[0-9]{4}|AOT analysis|Trim analysis' "$LOG" | sed 's/^/      /'
else
  ok "zero AOT/trim analyzer warnings"
fi

BIN="$OUT/DotCov.Tool"
mkdir -p "$OUT"; cp "src/DotCov.Tool/bin/Release/net10.0/$RID/publish/DotCov.Tool" "$BIN"
if file "$BIN" | grep -Eq 'Mach-O|ELF|PE32'; then
  ok "native binary ($(du -h "$BIN" | cut -f1)): $(file -b "$BIN" | cut -d, -f1)"
else
  bad "published artifact is not a native binary: $(file -b "$BIN")"
fi

# ── Fixtures ──────────────────────────────────────────────────────────────────
cat >"$WORK/a.cobertura.xml" <<'XML'
<?xml version="1.0"?><coverage line-rate="0.66" version="1.9"><packages><package name="D">
<classes><class name="D.Foo" filename="src/Foo.cs"><lines>
<line number="1" hits="3" branch="false"/><line number="2" hits="0" branch="false"/>
<line number="3" hits="5" branch="true" condition-coverage="50% (1/2)"/>
</lines></class></classes></package></packages></coverage>
XML
cat >"$WORK/b.cobertura.xml" <<'XML'
<?xml version="1.0"?><coverage line-rate="0.83" version="1.9"><packages><package name="D">
<classes><class name="D.Foo" filename="src/Foo.cs"><lines>
<line number="1" hits="3" branch="false"/><line number="2" hits="4" branch="false"/>
<line number="3" hits="5" branch="true" condition-coverage="100% (2/2)"/>
</lines></class></classes></package></packages></coverage>
XML
A="$WORK/a.cobertura.xml"; B="$WORK/b.cobertura.xml"

# Validate stdin is JSON: python3 when present, else a minimal brace check.
is_json() {
  if command -v python3 >/dev/null 2>&1; then python3 -c 'import sys,json; json.load(sys.stdin)' 2>/dev/null
  else read -r first; case "$first" in '{'*|'['*) return 0;; *) return 1;; esac; fi
}

# _run <desc> <want-exit> <validate-json:0|1> -- <binary args...>
_run() {
  local desc="$1" want="$2" vjson="$3"; shift 3; shift   # last shift drops the literal "--"
  local out rc; set +e; out="$("$BIN" "$@" 2>&1)"; rc=$?; set -e
  if printf '%s' "$out" | grep -q 'Unhandled exception\|Reflection-based serialization'; then
    bad "$desc — threw at runtime"; printf '%s\n' "$out" | sed 's/^/      /' | head -3; return
  fi
  [ "$rc" = "$want" ] || { bad "$desc — exit $rc, wanted $want"; return; }
  if [ "$vjson" = 1 ] && ! printf '%s' "$out" | is_json; then bad "$desc — invalid JSON"; return; fi
  [ "$vjson" = 1 ] && ok "$desc (exit $rc, valid JSON)" || ok "$desc (exit $rc)"
}
check()  { local d="$1" w="$2"; shift 2; _run "$d" "$w" 0 -- "$@"; }
checkj() { local d="$1" w="$2"; shift 2; _run "$d" "$w" 1 -- "$@"; }

echo "== Run every command × format on the native binary =="
check  "report  table" 0 report   "$A"
checkj "report  json"  0 report   "$A" --format json
check  "report  md"    0 report   "$A" --format md
check  "check   pass"  0 check     "$A" --min-line 50
check  "check   fail"  1 check     "$A" --min-line 99
checkj "diff    json"  0 diff      "$A" "$B" --format json
check  "diff    table" 0 diff      "$A" "$B"
checkj "snapshot json" 0 snapshot  "$A" --commit abc --branch main --project D
check  "version"       0 version

echo
echo "== $pass passed, $fail failed =="
[ "$fail" = 0 ] && { echo "AOT-PROVEN ✅  native binary at $BIN"; exit 0; } || { echo "AOT-BROKEN ❌"; exit 1; }
