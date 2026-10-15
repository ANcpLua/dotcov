#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
version=$1
commit=$2
stage=$3
output=$4

for window in {1..5}; do
  if python3 "$script_directory/verify_publication.py" wait --version "$version" --commit "$commit" \
      --stage "$stage" --output "$output" --timeout 240; then
    exit 0
  else
    status=$?
  fi
  if ((status != 10)); then
    exit "$status"
  fi
  if ((window < 5)); then
    echo "::warning::NuGet availability window $window/5 expired; starting a fresh process."
  fi
done

echo '::error::NuGet packages are still pending after five availability windows. Publication cannot continue.'
exit 10
