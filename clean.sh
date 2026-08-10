#!/usr/bin/env bash
# Removes every obj/ and bin/ folder in the repo, so a stale build/namespace
# cache can never survive a rename or a branch switch.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

found=$(find . -type d \( -name obj -o -name bin \) -not -path "*/node_modules/*")

if [ -z "$found" ]; then
    echo "Nothing to clean."
    exit 0
fi

echo "$found"
echo "$found" | xargs rm -rf
echo "Cleaned."
