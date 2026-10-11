#!/usr/bin/env bash
# The dotcov badge action (action.yml): gate the reports with `dotcov check`, which renders the
# verdict into coverage-badge.svg and coverage-badge.json, publish both to a branch, then exit
# with the gate's code. With badge-directory it publishes a badge a gate rendered earlier
# instead. Inputs arrive as DOTCOV_* variables. Runs on bash 3.2 (macOS) and later.
set -euo pipefail

fail() {
  echo "::error::$*"
  exit 1
}

if [ -n "$DOTCOV_BADGE_DIRECTORY" ]; then
  [ -z "$DOTCOV_REPORTS" ] || fail "Set reports or badge-directory, not both."
else
  [ -n "$DOTCOV_REPORTS" ] || fail "Set reports, or badge-directory to publish a badge rendered earlier."
fi
case "$DOTCOV_PUBLISH" in true | false) ;; *) fail "publish must be true or false, not '$DOTCOV_PUBLISH'." ;; esac
case "$DOTCOV_FAIL_ON_GATE" in true | false) ;; *) fail "fail-on-gate must be true or false, not '$DOTCOV_FAIL_ON_GATE'." ;; esac

work="${RUNNER_TEMP:?}/dotcov-badge-${GITHUB_RUN_ID:-local}-$$"
mkdir -p "$work"

# The gate's exit code; empty when the badge was rendered earlier and no gate runs here.
code=''
if [ -n "$DOTCOV_BADGE_DIRECTORY" ]; then
  badge_dir="$DOTCOV_BADGE_DIRECTORY"
else
  # A fresh directory per run: files found in it afterwards were written by this gate.
  badge_dir="$work/badge"

  # Whitespace-only values count as empty: they would split into no words at all.
  if [ -n "${DOTCOV_COMMAND//[[:space:]]/}" ]; then
    read -ra dotcov <<<"$DOTCOV_COMMAND"
  else
    tools="$RUNNER_TEMP/dotcov-tool-$DOTCOV_VERSION"
    if [ ! -e "$tools/dotcov" ] && [ ! -e "$tools/dotcov.exe" ]; then
      dotnet tool install DotCov.Tool --version "$DOTCOV_VERSION" --tool-path "$tools"
    fi
    dotcov=("$tools/dotcov")
  fi

  args=(check "$DOTCOV_REPORTS" --min-line "$DOTCOV_MIN_LINE" --min-branch "$DOTCOV_MIN_BRANCH" --badge-dir "$badge_dir")
  if [ -n "$DOTCOV_PATTERN" ]; then args+=(--pattern "$DOTCOV_PATTERN"); fi
  if [ -n "${DOTCOV_ARGS//[[:space:]]/}" ]; then
    read -ra extra <<<"$DOTCOV_ARGS"
    args+=("${extra[@]}")
  fi

  code=0
  "${dotcov[@]}" "${args[@]}" || code=$?
  echo "exit-code=$code" >>"$GITHUB_OUTPUT"
fi

# Commits the badge onto the tip of the branch, or as the first commit of an orphan branch. The
# tree is built in a private index from git objects, so the consumer's checkout, index, branches
# and git config stay untouched. Contents decide, not the number: the same percentage can change
# from PASS to FAIL when a threshold moves.
publish() {
  local message color subject attempt status parent tree svg json commit
  message=$(sed -n 's/.*"message":"\([^"]*\)".*/\1/p' "$badge_dir/coverage-badge.json")
  color=$(sed -n 's/.*"color":"\([^"]*\)".*/\1/p' "$badge_dir/coverage-badge.json")
  subject="badge: $message ($color)${GITHUB_SHA:+ for ${GITHUB_SHA:0:7}}"
  export GIT_AUTHOR_NAME='github-actions[bot]'
  export GIT_AUTHOR_EMAIL='41898282+github-actions[bot]@users.noreply.github.com'
  export GIT_COMMITTER_NAME="$GIT_AUTHOR_NAME" GIT_COMMITTER_EMAIL="$GIT_AUTHOR_EMAIL"

  # A push rejected because another run moved the branch is retried on the new tip.
  for attempt in 1 2 3; do
    parent=''
    status=0
    git ls-remote --exit-code --heads origin "refs/heads/$DOTCOV_BRANCH" >/dev/null || status=$?
    case $status in
      0)
        git fetch --quiet --no-tags origin "refs/heads/$DOTCOV_BRANCH"
        parent=$(git rev-parse FETCH_HEAD)
        ;;
      2) echo "Branch '$DOTCOV_BRANCH' does not exist yet; creating it as an orphan branch." ;;
      *) fail "git ls-remote exited $status, so it is unknown whether '$DOTCOV_BRANCH' exists." ;;
    esac

    export GIT_INDEX_FILE="$work/index"
    rm -f "$GIT_INDEX_FILE"
    if [ -n "$parent" ]; then git read-tree "$parent"; else git read-tree --empty; fi
    svg=$(git hash-object -w --no-filters "$badge_dir/coverage-badge.svg")
    json=$(git hash-object -w --no-filters "$badge_dir/coverage-badge.json")
    git update-index --add --cacheinfo "100644,$svg,coverage-badge.svg" --cacheinfo "100644,$json,coverage-badge.json"
    tree=$(git write-tree)
    unset GIT_INDEX_FILE

    if [ -n "$parent" ] && [ "$tree" = "$(git rev-parse "$parent^{tree}")" ]; then
      echo "The badge on '$DOTCOV_BRANCH' is unchanged: $message ($color)."
      return
    fi

    if [ -n "$parent" ]; then
      commit=$(git commit-tree "$tree" -p "$parent" -m "$subject")
    else
      commit=$(git commit-tree "$tree" -m "$subject")
    fi

    if git push --quiet origin "$commit:refs/heads/$DOTCOV_BRANCH"; then
      echo "Published $message ($color) to '$DOTCOV_BRANCH'."
      return
    fi
    echo "::warning::Pushing the badge to '$DOTCOV_BRANCH' failed (attempt $attempt of 3)."
  done
  fail "Could not publish the badge to '$DOTCOV_BRANCH'."
}

if [ -f "$badge_dir/coverage-badge.svg" ] && [ -f "$badge_dir/coverage-badge.json" ]; then
  echo "directory=$badge_dir" >>"$GITHUB_OUTPUT"
  if [ "$DOTCOV_PUBLISH" = true ]; then publish; fi
elif [ -z "$code" ]; then
  # The gate that should have rendered it failed before evaluating; a stale badge stays unpublished.
  fail "badge-directory '$badge_dir' holds no coverage-badge.svg and coverage-badge.json; nothing was published."
elif [ "$DOTCOV_PUBLISH" = true ]; then
  # No badge means no evaluated gate (an error such as an unreadable report), so nothing to show.
  echo "::error::dotcov check exited $code without writing a badge; nothing was published."
  exit $((code == 0 ? 1 : code))
fi

# A badge rendered earlier was judged by its own gate, which owns the verdict.
if [ -z "$code" ]; then exit 0; fi

# Gate outcomes (FAIL 1, NODATA 3, DISABLED 4) honour fail-on-gate; errors always fail the step.
case $code in
  0) ;;
  1 | 3 | 4)
    if [ "$DOTCOV_FAIL_ON_GATE" = true ]; then exit "$code"; fi
    echo "::warning::The coverage gate did not pass (dotcov exit $code); fail-on-gate is false."
    ;;
  *) exit "$code" ;;
esac
